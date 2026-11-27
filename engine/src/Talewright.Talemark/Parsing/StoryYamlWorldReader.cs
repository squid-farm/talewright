using System.Globalization;
using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.Text;
using Talewright.Core.World;
using Talewright.Talemark.Model;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using static Talewright.Talemark.Parsing.StoryYamlContext;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

/// <summary>Reads world declarations and character-creation configuration.</summary>
internal sealed class StoryYamlWorldReader(StoryYamlContext yaml, StatDiagnosticGuidance statGuidance)
{
    private readonly StoryYamlContext _yaml = yaml;
    private readonly StatDiagnosticGuidance _statGuidance = statGuidance;
    internal Dictionary<EntityId, SourceLocation> CharacterLocations { get; } = [];

    internal CharacterCreation? ReadCreation(YamlNode? node, Ruleset ruleset, int talemarkVersion)
    {
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Creation) is not { } map)
        {
            return null;
        }

        _yaml.CheckKeys(map, ManifestVocabulary.Name, ManifestVocabulary.Pronouns, ManifestVocabulary.Presets, ManifestVocabulary.Stats);
        var editName = Get(map, ManifestVocabulary.Name) is { } name
            && _yaml.Boolean(name, $"{ManifestVocabulary.Creation}.{ManifestVocabulary.Name}", false);

        var pronouns = new List<string>();
        if (Get(map, ManifestVocabulary.Pronouns) is { } pronounNode)
        {
            foreach (var value in _yaml.Sequence(pronounNode, "creation.pronouns"))
            {
                var keyword = Scalar(value) ?? "";
                if (!ManifestVocabulary.BuiltInPronounKeywords.Contains(keyword, StringComparer.Ordinal) ||
                    pronouns.Contains(keyword))
                {
                    _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, "creation.pronouns must contain distinct built-in keywords: she, he, they, it.", 0);
                }
                else
                {
                    pronouns.Add(keyword);
                }
            }
        }

        var stats = new Dictionary<StatId, IReadOnlyList<int>>();
        if (Get(map, ManifestVocabulary.Stats) is { } statNode && _yaml.Mapping(statNode, "creation.stats") is { } statMap)
        {
            foreach (var (key, value) in statMap.Children)
            {
                var rawId = _yaml.Identifier(key, IdentifierLabels.CreationStat);
                if (rawId is null) continue;
                var id = new StatId(rawId);
                if (!ruleset.StatsById.TryGetValue(id, out var definition))
                {
                    _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference,
                        $"Unknown creation stat '{id}'." + _statGuidance.ForMissingStat(id), 0);
                    continue;
                }

                var values = new List<int>();
                foreach (var option in _yaml.Sequence(value, "creation stat values"))
                {
                    if (_yaml.Int(option) is { } number &&
                        (talemarkVersion == 2 ? definition.ClampExact(number) == number : definition.Clamp(number) == number)
                        && !values.Contains(number))
                    {
                        values.Add(number);
                    }
                    else
                    {
                        _yaml.Error(option, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Creation values for '{id}' must be distinct integers within ruleset bounds.", 0);
                    }
                }

                if (values.Count is < 1 or > 100)
                {
                    _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, "Each creation stat needs 1 to 100 legal values.", 0);
                }

                stats[id] = values.AsReadOnly();
            }
        }

        if (stats.Count > 32)
        {
            _yaml.Error(map, CompilerDiagnosticCodes.InvalidConfigurationValue, "Character creation supports at most 32 selectable stats.", 0);
        }

        var presets = new List<CharacterPreset>();
        if (Get(map, ManifestVocabulary.Presets) is { } presetNode && _yaml.Mapping(presetNode, "creation.presets") is { } presetMap)
        {
            foreach (var (key, value) in presetMap.Children)
            {
                if (_yaml.Mapping(value, "creation preset") is not { } preset)
                {
                    continue;
                }

                _yaml.CheckKeys(preset, ManifestVocabulary.Label, ManifestVocabulary.Stats);
                var id = _yaml.Identifier(key, IdentifierLabels.Preset);
                if (id is null)
                {
                    continue;
                }
                if (id.Length > 80)
                {
                    _yaml.Error(key, CompilerDiagnosticCodes.InvalidConfigurationValue, "Preset ids must contain at most 80 characters.", 0);
                    continue;
                }

                var exactStats = talemarkVersion == 2 ? ReadExactStatMap(Get(preset, ManifestVocabulary.Stats), ruleset) : null;
                var presetStats = exactStats is null ? ReadStatMap(Get(preset, ManifestVocabulary.Stats), ruleset)
                    : exactStats.ToDictionary(pair => pair.Key, pair => pair.Value.ToInt32RoundedAwayFromZero());
                foreach (var (stat, number) in presetStats)
                {
                    var exact = exactStats is not null ? exactStats[stat] : ExactNumber.FromInteger(number);
                    if ((talemarkVersion == 2 ? ruleset.StatsById[stat].ClampExact(exact) != exact : ruleset.StatsById[stat].Clamp(number) != number) ||
                        stats.TryGetValue(stat, out var choices) && (exact != number || !choices.Contains(number)))
                    {
                        _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Preset stat '{stat}' must respect ruleset bounds and its selectable values.", 0);
                    }
                }

                presets.Add(new CharacterPreset(id, Scalar(Get(preset, ManifestVocabulary.Label)) ?? id, presetStats)
                    { ExactStats = exactStats });
            }

            if (presets.Count > 16)
            {
                _yaml.Error(presetNode, CompilerDiagnosticCodes.InvalidConfigurationValue, "Character creation supports at most 16 presets.", 0);
            }
        }

        return new CharacterCreation { EditName = editName, Pronouns = pronouns.AsReadOnly(), Stats = stats, Presets = presets.AsReadOnly() };
    }

    internal Dictionary<ItemId, ItemDefinition> ReadItems(YamlNode? node, Ruleset ruleset, int talemarkVersion)
    {
        var items = new Dictionary<ItemId, ItemDefinition>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Items) is not { } map)
        {
            return items;
        }

        foreach (var (key, value) in map.Children)
        {
            var rawId = _yaml.Identifier(key, IdentifierLabels.Item);
            if (rawId is null) continue;
            // Item counts are read as character:inventory:item:count, where an item named inventory is ambiguous.
            if (rawId == ExpressionVocabulary.Inventory)
            {
                _yaml.Diagnostics.Error(CompilerDiagnosticCodes.InvalidConfigurationValue,
                    $"Item id '{rawId}' is reserved.", _yaml.At(key));
                continue;
            }

            var id = new ItemId(rawId);
            var name = id.Value;
            var modifiers = new Dictionary<StatId, int>();
            Dictionary<StatId, ExactNumber>? exactModifiers = null;
            var nameAgreement = GrammaticalNumber.Singular;
            var uncountable = false;
            var divisible = false;
            var customProperties = new Dictionary<string, ItemPropertyValue>(StringComparer.Ordinal);
            if (value is YamlMappingNode body)
            {
                foreach (var (propertyKey, propertyValue) in body.Children)
                {
                    if (Scalar(propertyKey) is not { } propertyName)
                    {
                        _yaml.Diagnostics.Error(CompilerDiagnosticCodes.UnknownConfigurationKey,
                            "Configuration keys must be scalar values. Expected item engine keys or custom property names.",
                            _yaml.At(propertyKey));
                        continue;
                    }

                    if (propertyName == ManifestVocabulary.Stowable)
                    {
                        _yaml.Diagnostics.Error(CompilerDiagnosticCodes.UnknownConfigurationKey,
                            $"Unknown key '{propertyName}'. This engine setting is not supported yet.",
                            _yaml.At(propertyKey));
                        continue;
                    }

                    if (ManifestVocabulary.ItemEngineKeys.Contains(propertyName))
                        continue;

                    if (_yaml.Identifier(propertyKey, "item property") is { } customName)
                    {
                        HintIfNearItemEngineKey(propertyKey, customName);
                        if (ReadCustomItemProperty(customName, propertyValue, talemarkVersion) is { } customValue)
                            customProperties.Add(customName, customValue);
                    }
                }

                name = Scalar(Get(body, ManifestVocabulary.Name)) ?? name;
                if (talemarkVersion == 1)
                    modifiers = ReadStatMap(Get(body, ManifestVocabulary.Modifiers), ruleset);
                else
                {
                    exactModifiers = ReadExactStatMap(Get(body, ManifestVocabulary.Modifiers), ruleset);
                    modifiers = exactModifiers.ToDictionary(pair => pair.Key, pair => pair.Value.ToInt32RoundedAwayFromZero());
                }
                if (Get(body, ManifestVocabulary.NameAgreement) is { } number)
                    nameAgreement = ReadGrammaticalNumber(number, ManifestVocabulary.NameAgreement);
                if (Get(body, ManifestVocabulary.Uncountable) is { } mass)
                    uncountable = _yaml.Boolean(mass, ManifestVocabulary.Uncountable, false);
                if (Get(body, ManifestVocabulary.Divisible) is { } division)
                    divisible = _yaml.Boolean(division, ManifestVocabulary.Divisible, false);
            }
            else if (Scalar(value) is { Length: > 0 } shortName)
            {
                name = shortName;
            }

            items[id] = new ItemDefinition(id, name, modifiers, nameAgreement, uncountable, divisible)
                { ExactModifiers = exactModifiers, CustomProperties = customProperties };
        }

        return items;
    }

    private void HintIfNearItemEngineKey(YamlNode keyNode, string propertyName)
    {
        var candidates = ManifestVocabulary.ItemEngineKeys
            .Where(engineKey => Math.Abs(propertyName.Length - engineKey.Length) <= 2)
            .Select(engineKey => (Name: engineKey, Distance: DamerauLevenshtein.Distance(propertyName, engineKey)))
            .Where(candidate => candidate.Distance is > 0 and <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            return;

        var start = _yaml.At(keyNode);
        var endMark = keyNode.End;
        var end = new SourceLocation(start.File, Math.Max(1, (int)endMark.Line), Math.Max(1, (int)endMark.Column));
        _yaml.Diagnostics.Hint(CompilerDiagnosticCodes.PossibleItemEngineKey,
            $"Custom item key '{propertyName}' is close to engine key '{candidates[0].Name}'.", start, end);
    }

    private ItemPropertyValue? ReadCustomItemProperty(string name, YamlNode node, int talemarkVersion)
    {
        if (node is not YamlScalarNode scalar)
            return _yaml.Error<ItemPropertyValue?>(node, CompilerDiagnosticCodes.InvalidConfigurationValue,
                $"Item property '{name}' must be a scalar value.", null);

        var raw = scalar.Value;
        if (raw is null || raw.Length == 0 || scalar.Style is ScalarStyle.Plain &&
            (raw == "~" || raw.Equals("null", StringComparison.OrdinalIgnoreCase)))
        {
            return _yaml.Error<ItemPropertyValue?>(node, CompilerDiagnosticCodes.InvalidConfigurationValue,
                $"Item property '{name}' cannot be null or empty.", null);
        }

        if (scalar.Style is not ScalarStyle.Plain)
            return new ItemPropertyValue.Text(raw);

        if (bool.TryParse(raw, out var boolean))
            return new ItemPropertyValue.Boolean(boolean);

        if (TryParseVariableNumber(raw, talemarkVersion, out var number))
            return new ItemPropertyValue.Number(number);

        if (ExactNumber.TryParse(raw, out _) || IsSlashFraction(raw))
        {
            var requirement = talemarkVersion == 1 ? "a whole number" : "a decimal number without slash fractions";
            return _yaml.Error<ItemPropertyValue?>(node, CompilerDiagnosticCodes.InvalidConfigurationValue,
                $"Item property '{name}' must be {requirement} in Talemark {talemarkVersion}.", null);
        }

        return new ItemPropertyValue.Text(raw);
    }

    private static bool IsSlashFraction(string value)
    {
        var separator = value.IndexOf('/');
        return separator > 0 && separator == value.LastIndexOf('/') &&
            ExactNumber.TryParse(value[..separator], out _) &&
            ExactNumber.TryParse(value[(separator + 1)..], out _);
    }

    internal List<FactionDefinition> ReadFactions(YamlNode? node)
    {
        var factions = new List<FactionDefinition>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Factions) is not { } map)
        {
            return factions;
        }

        foreach (var (key, value) in map.Children)
        {
            var id = _yaml.Identifier(key, IdentifierLabels.Faction);
            if (id is null) continue;
            if (value is not YamlMappingNode body)
            {
                _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Faction '{id}' must be a mapping (key: value pairs).", 0);
                continue;
            }

            _yaml.CheckKeys(body, ManifestVocabulary.Name, ManifestVocabulary.Standing);
            var name = Scalar(Get(body, ManifestVocabulary.Name)) ?? id;
            var standing = _yaml.Int(Get(body, ManifestVocabulary.Standing)) ?? 0;

            factions.Add(new FactionDefinition(new FactionId(id), name, standing));
        }

        return factions;
    }

    internal List<CharacterDefinition> ReadCharacters(YamlNode? node, YamlMappingNode root, Ruleset ruleset, Dictionary<ItemId, ItemDefinition> items, int talemarkVersion)
    {
        var characters = new List<CharacterDefinition>();
        var map = node is null ? null : _yaml.Mapping(node, ManifestVocabulary.Characters);
        if (map is null || !map.Children.Any(c => Scalar(c.Key) == EntityId.Player.Value))
        {
            _yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration, "The story configuration must declare the protagonist under 'characters: player:'.", node is null ? _yaml.At(root) : _yaml.At(node));
        }

        foreach (var (key, value) in map?.Children ?? Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
        {
            var id = _yaml.Identifier(key, IdentifierLabels.Character);
            if (id is null) continue;
            if (id == ExpressionVocabulary.Scene)
            {
                _yaml.Diagnostics.Error(CompilerDiagnosticCodes.InvalidConfigurationValue,
                    $"Character id '{id}' is reserved.", _yaml.At(key));
                continue;
            }

            var characterId = new EntityId(id);
            CharacterLocations[characterId] = _yaml.At(key);
            if (_yaml.Mapping(value, $"characters.{id}") is not { } body)
            {
                continue;
            }

            _yaml.CheckKeys(body, ManifestVocabulary.FirstName, ManifestVocabulary.LastName, ManifestVocabulary.Pronouns, ManifestVocabulary.Stats, ManifestVocabulary.Inventory, ManifestVocabulary.NameAgreement);
            var firstName = _yaml.RequiredScalar(body, ManifestVocabulary.FirstName) ?? id;
            var lastName = Scalar(Get(body, ManifestVocabulary.LastName)) ?? string.Empty;
            var pronouns = ReadPronouns(Get(body, ManifestVocabulary.Pronouns));
            var exactStats = talemarkVersion == 2 ? ReadExactStatMap(Get(body, ManifestVocabulary.Stats), ruleset) : null;
            var stats = exactStats is null ? ReadStatMap(Get(body, ManifestVocabulary.Stats), ruleset)
                : exactStats.ToDictionary(pair => pair.Key, pair => pair.Value.ToInt32RoundedAwayFromZero());
            var inventory = ReadInventory(Get(body, ManifestVocabulary.Inventory), items, characterId, talemarkVersion);
            var nameAgreement = Get(body, ManifestVocabulary.NameAgreement) is { } number
                ? ReadGrammaticalNumber(number, ManifestVocabulary.NameAgreement)
                : GrammaticalNumber.Singular;
            characters.Add(new CharacterDefinition(characterId, firstName, lastName, pronouns, stats,
                inventory.ToDictionary(pair => pair.Key, pair => pair.Value.ToInt32RoundedAwayFromZero()), nameAgreement)
            {
                ExactInventory = inventory,
                ExactStats = exactStats,
            });
        }

        if (map is not null && !characters.Any(c => c.Id == EntityId.Player))
        {
            // Keep going with a placeholder so the rest of the story can still be checked.
            characters.Add(new CharacterDefinition(EntityId.Player, "Player", string.Empty, Pronouns.They, new Dictionary<StatId, int>(), new Dictionary<ItemId, int>()));
        }
        else if (map is null)
        {
            characters.Add(new CharacterDefinition(EntityId.Player, "Player", string.Empty, Pronouns.They, new Dictionary<StatId, int>(), new Dictionary<ItemId, int>()));
        }

        return characters;
    }

    private Pronouns ReadPronouns(YamlNode? node)
    {
        if (node is null)
        {
            return Pronouns.They;
        }

        if (node is YamlMappingNode map)
        {
            _yaml.CheckKeys(map, ManifestVocabulary.Subject, ManifestVocabulary.Object, ManifestVocabulary.PossessivePronoun, ManifestVocabulary.Reflexive, ManifestVocabulary.Agreement);
            var possessive = Get(map, ManifestVocabulary.PossessivePronoun);
            var determiner = ManifestVocabulary.Determiner;
            var independent = ManifestVocabulary.Independent;
            if (possessive is YamlMappingNode forms)
            {
                _yaml.CheckKeys(forms, ManifestVocabulary.Determiner, ManifestVocabulary.Independent);
                determiner = _yaml.RequiredScalar(forms, ManifestVocabulary.Determiner) ?? determiner;
                independent = _yaml.RequiredScalar(forms, ManifestVocabulary.Independent) ?? independent;
            }
            else if (possessive is null)
            {
                _yaml.Error(map, CompilerDiagnosticCodes.MissingRequiredConfiguration, "Missing required 'possessive-pronoun'. Use 'possessive-pronoun: { determiner: xer, independent: xers }'.", 0);
            }
            else
            {
                _yaml.Error(possessive, CompilerDiagnosticCodes.InvalidConfigurationValue, "'possessive-pronoun' must be a mapping with 'determiner' and 'independent' values. Use 'possessive-pronoun: { determiner: xer, independent: xers }'.", 0);
            }

            string Form(string key) => _yaml.RequiredScalar(map, key) ?? key;
            var agreement = Get(map, ManifestVocabulary.Agreement) is { } number
                ? ReadGrammaticalNumber(number, ManifestVocabulary.Agreement)
                : _yaml.Error(map, CompilerDiagnosticCodes.MissingRequiredConfiguration, "Custom pronoun sets require 'agreement: singular' or 'agreement: plural'. Agreement describes grammar, not population.", GrammaticalNumber.Singular);
            return new Pronouns(Form(ManifestVocabulary.Subject), Form(ManifestVocabulary.Object), determiner, independent, Form(ManifestVocabulary.Reflexive), agreement);
        }

        var keyword = Scalar(node) ?? string.Empty;
        return Pronouns.FromKeyword(keyword)
            ?? _yaml.Error(node, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Unknown pronouns '{keyword}'. Use she, he, they, it, or a mapping with subject, object, reflexive, agreement: singular|plural and possessive-pronoun: {{ determiner: xer, independent: xers }}.", Pronouns.They);
    }

    private GrammaticalNumber ReadGrammaticalNumber(YamlNode node, string field) => Scalar(node) switch
    {
        ManifestVocabulary.GrammaticalSingular => GrammaticalNumber.Singular,
        ManifestVocabulary.GrammaticalPlural => GrammaticalNumber.Plural,
        _ => _yaml.Error(node, CompilerDiagnosticCodes.InvalidConfigurationValue, $"'{field}' must be 'singular' or 'plural'; it describes grammar, not population.", GrammaticalNumber.Singular),
    };

    private Dictionary<StatId, int> ReadStatMap(YamlNode? node, Ruleset ruleset)
    {
        var stats = new Dictionary<StatId, int>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Stats) is not { } map)
        {
            return stats;
        }

        foreach (var (key, value) in map.Children)
        {
            var rawId = _yaml.Identifier(key, IdentifierLabels.Stat);
            if (rawId is null) continue;
            var id = new StatId(rawId);
            if (!ruleset.HasStat(id))
            {
                var guidance = _statGuidance.ForMissingStat(id);
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference,
                    $"Unknown stat '{id}' (ruleset '{ruleset.Id}')." +
                    (guidance.Length > 0 ? guidance : " Add it under ruleset.stats."), 0);
                continue;
            }

            stats[id] = _yaml.Int(value) ?? 0;
        }

        return stats;
    }

    private Dictionary<StatId, ExactNumber> ReadExactStatMap(YamlNode? node, Ruleset ruleset)
    {
        var stats = new Dictionary<StatId, ExactNumber>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Stats) is not { } map) return stats;
        foreach (var (key, value) in map.Children)
        {
            var rawId = _yaml.Identifier(key, IdentifierLabels.Stat);
            if (rawId is null) continue;
            var id = new StatId(rawId);
            if (!ruleset.HasStat(id))
            {
                var guidance = _statGuidance.ForMissingStat(id);
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference,
                    $"Unknown stat '{id}' (ruleset '{ruleset.Id}')." +
                    (guidance.Length > 0 ? guidance : " Add it under ruleset.stats."), 0);
                continue;
            }
            var number = _yaml.Number(value) ?? ExactNumber.Zero;
            stats[id] = ruleset.IsFractionalResource(id) ? number : number.ToInt32RoundedAwayFromZero();
        }
        return stats;
    }

    private Dictionary<ItemId, ExactNumber> ReadInventory(YamlNode? node, Dictionary<ItemId, ItemDefinition> items, EntityId character, int talemarkVersion)
    {
        if (talemarkVersion == 1)
            return ReadIntegerInventory(node, items, character)
                .ToDictionary(pair => pair.Key, pair => ExactNumber.FromInteger(pair.Value));

        var inventory = new Dictionary<ItemId, ExactNumber>();
        if (node is null)
        {
            return inventory;
        }

        IEnumerable<(YamlNode Key, YamlNode? QuantityNode)> entries = node switch
        {
            YamlSequenceNode seq => seq.Children.Select(c => (c, (YamlNode?)null)),
            YamlMappingNode map => map.Children.Select(c => (c.Key, (YamlNode?)c.Value)),
            _ => _yaml.Error<IEnumerable<(YamlNode, YamlNode?)>>(node, CompilerDiagnosticCodes.InvalidConfigurationValue, "inventory must be a list of item ids or a map of item id to quantity.", []),
        };

        foreach (var (key, quantityNode) in entries)
        {
            var rawId = _yaml.Identifier(key, IdentifierLabels.Item);
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            if (!items.TryGetValue(id, out var item))
            {
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference, $"Unknown item '{id}'. Declare it under items.", 0);
                continue;
            }

            var quantity = ExactNumber.One;
            if (quantityNode is not null)
            {
                if (quantityNode is not YamlScalarNode { Style: not (ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted), Value: { } raw }
                    || !TryParseVariableNumber(raw, talemarkVersion, out quantity))
                    quantity = _yaml.Error(quantityNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                        "Starting inventory quantity must be a decimal number.", ExactNumber.Zero);

                if (quantity < 0)
                    quantity = _yaml.Error(quantityNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                        $"Starting inventory quantity for item '{id}' on character '{character}' must be non-negative.", ExactNumber.Zero);
            }

            if (!item.Divisible)
                quantity = quantity.ToInt32RoundedAwayFromZero();
            inventory[id] = inventory.GetValueOrDefault(id) + quantity;
        }

        return inventory;
    }

    private Dictionary<ItemId, int> ReadIntegerInventory(YamlNode? node, Dictionary<ItemId, ItemDefinition> items, EntityId character)
    {
        var inventory = new Dictionary<ItemId, int>();
        if (node is null)
            return inventory;

        IEnumerable<(YamlNode Key, YamlNode? QuantityNode, int Quantity)> entries = node switch
        {
            YamlSequenceNode seq => seq.Children.Select(c => (c, (YamlNode?)null, 1)),
            YamlMappingNode map => map.Children.Select(c => (c.Key, (YamlNode?)c.Value, _yaml.Int(c.Value) ?? 1)),
            _ => _yaml.Error<IEnumerable<(YamlNode, YamlNode?, int)>>(node, CompilerDiagnosticCodes.InvalidConfigurationValue, "inventory must be a list of item ids or a map of item id to quantity.", []),
        };
        foreach (var (key, quantityNode, quantity) in entries)
        {
            var rawId = _yaml.Identifier(key, IdentifierLabels.Item);
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            if (!items.ContainsKey(id))
            {
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference, $"Unknown item '{id}'. Declare it under items.", 0);
                continue;
            }
            var startingQuantity = quantity;
            if (quantityNode is not null && quantity < 0)
                startingQuantity = _yaml.Error(quantityNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                    $"Starting inventory quantity for item '{id}' on character '{character}' must be non-negative.", 0);
            inventory[id] = inventory.GetValueOrDefault(id) + startingQuantity;
        }
        return inventory;
    }

    internal Dictionary<FlagId, bool> ReadFlags(YamlNode? node)
    {
        var flags = new Dictionary<FlagId, bool>();
        if (node is not YamlMappingNode map) return flags;
        foreach (var (key, value) in map.Children)
        {
            if (_yaml.Identifier(key, IdentifierLabels.Flag) is not { } id) continue;
            var flag = new FlagId(id);
            flags[flag] = _yaml.Boolean(value, $"{ManifestVocabulary.Flags}.{id}", false);
        }

        return flags;
    }

    internal (Dictionary<VarId, ExactNumber> Numbers, Dictionary<VarId, string> Text,
        Dictionary<VarId, ItemId> Items) ReadVars(
        YamlNode? node, int talemarkVersion, IReadOnlyDictionary<ItemId, ItemDefinition> items)
    {
        var numericVars = new Dictionary<VarId, ExactNumber>();
        var textVars = new Dictionary<VarId, string>();
        var itemVars = new Dictionary<VarId, ItemId>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Vars) is not { } map)
        {
            return (numericVars, textVars, itemVars);
        }

        foreach (var (key, value) in map.Children)
        {
            if (_yaml.Identifier(key, IdentifierLabels.Variable) is not { } id) continue;
            if (value is YamlScalarNode { Style: ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted } text)
            {
                textVars.Add(new VarId(id), text.Value ?? "");
            }
            else if (talemarkVersion == 2 && value is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } reference }
                && reference.StartsWith(ExpressionVocabulary.Item + ExpressionVocabulary.Separator, StringComparison.Ordinal))
            {
                var name = reference[(ExpressionVocabulary.Item.Length + 1)..];
                if (!IdentifierRules.IsValid(name))
                {
                    _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue,
                        $"Variable '{id}' needs a valid item reference, such as item:iron-ore.", 0);
                }
                else if (!items.ContainsKey(new ItemId(name)))
                {
                    _yaml.Error(value, CompilerDiagnosticCodes.UnknownConfigurationReference,
                        $"Unknown item '{name}' for variable '{id}'. Declare it under items.", 0);
                }
                else
                {
                    var item = new ItemId(name);
                    itemVars.Add(new VarId(id), item);
                    textVars.Add(new VarId(id), ItemReference.ForItem(item).Encode());
                }
            }
            else if (value is YamlScalarNode { Value: { } raw } &&
                TryParseVariableNumber(raw, talemarkVersion, out var number))
            {
                numericVars.Add(new VarId(id), number);
            }
            else
            {
                _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue,
                    talemarkVersion == 1 && value is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } legacy }
                        && legacy.StartsWith(ExpressionVocabulary.Item + ExpressionVocabulary.Separator, StringComparison.Ordinal)
                        ? $"Variable '{id}' needs a whole number or quoted text value. Item variable initializers require game.talemark: 2."
                    : talemarkVersion == 1
                        ? $"Variable '{id}' needs a whole number or quoted text value."
                        : $"Variable '{id}' needs a decimal number, quoted text, or an item reference such as item:iron-ore.", 0);
            }
        }

        return (numericVars, textVars, itemVars);
    }

    private static bool TryParseVariableNumber(string raw, int talemarkVersion, out ExactNumber number)
    {
        if (talemarkVersion == 1 &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            number = ExactNumber.FromInteger(integer);
            return true;
        }

        if (talemarkVersion == 2 && !raw.Contains('/') && ExactNumber.TryParse(raw, out number))
            return true;

        number = ExactNumber.Zero;
        return false;
    }
}
