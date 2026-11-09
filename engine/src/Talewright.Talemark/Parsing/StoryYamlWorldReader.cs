using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
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

    internal CharacterCreation? ReadCreation(YamlNode? node, Ruleset ruleset)
    {
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Creation) is not { } map)
        {
            return null;
        }

        _yaml.CheckKeys(map, ManifestVocabulary.Name, ManifestVocabulary.Pronouns, ManifestVocabulary.Presets, ManifestVocabulary.Stats);
        var editName = false;
        if (Get(map, ManifestVocabulary.Name) is { } name && !bool.TryParse(Scalar(name), out editName))
        {
            _yaml.Error(name, CompilerDiagnosticCodes.InvalidConfigurationValue, "creation.name must be true or false.", 0);
        }

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
                var rawId = _yaml.Identifier(key, "creation stat id");
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
                    if (_yaml.Int(option) is { } number && definition.Clamp(number) == number && !values.Contains(number))
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
                var id = _yaml.Identifier(key, "preset id");
                if (id is null)
                {
                    continue;
                }
                if (id.Length > 80)
                {
                    _yaml.Error(key, CompilerDiagnosticCodes.InvalidConfigurationValue, "Preset ids must contain at most 80 characters.", 0);
                    continue;
                }

                var presetStats = ReadStatMap(Get(preset, ManifestVocabulary.Stats), ruleset);
                foreach (var (stat, number) in presetStats)
                {
                    if (ruleset.StatsById[stat].Clamp(number) != number ||
                        stats.TryGetValue(stat, out var choices) && !choices.Contains(number))
                    {
                        _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Preset stat '{stat}' must respect ruleset bounds and its selectable values.", 0);
                    }
                }

                presets.Add(new CharacterPreset(id, Scalar(Get(preset, ManifestVocabulary.Label)) ?? id, presetStats));
            }

            if (presets.Count > 16)
            {
                _yaml.Error(presetNode, CompilerDiagnosticCodes.InvalidConfigurationValue, "Character creation supports at most 16 presets.", 0);
            }
        }

        return new CharacterCreation { EditName = editName, Pronouns = pronouns.AsReadOnly(), Stats = stats, Presets = presets.AsReadOnly() };
    }

    internal Dictionary<ItemId, ItemDefinition> ReadItems(YamlNode? node, Ruleset ruleset)
    {
        var items = new Dictionary<ItemId, ItemDefinition>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Items) is not { } map)
        {
            return items;
        }

        foreach (var (key, value) in map.Children)
        {
            var rawId = _yaml.Identifier(key, "item id");
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            var name = id.Value;
            var modifiers = new Dictionary<StatId, int>();
            var nameAgreement = GrammaticalNumber.Singular;
            var uncountable = false;
            if (value is YamlMappingNode body)
            {
                _yaml.CheckKeys(body, ManifestVocabulary.Name, ManifestVocabulary.Modifiers, ManifestVocabulary.NameAgreement, ManifestVocabulary.Uncountable);
                name = Scalar(Get(body, ManifestVocabulary.Name)) ?? name;
                modifiers = ReadStatMap(Get(body, ManifestVocabulary.Modifiers), ruleset);
                if (Get(body, ManifestVocabulary.NameAgreement) is { } number)
                    nameAgreement = ReadGrammaticalNumber(number, ManifestVocabulary.NameAgreement);
                if (Get(body, ManifestVocabulary.Uncountable) is { } mass)
                    uncountable = bool.TryParse(Scalar(mass), out var parsed)
                        ? parsed
                        : _yaml.Error(mass, CompilerDiagnosticCodes.InvalidConfigurationValue, "'uncountable' must be true or false.", false);
            }
            else if (Scalar(value) is { Length: > 0 } shortName)
            {
                name = shortName;
            }

            items[id] = new ItemDefinition(id, name, modifiers, nameAgreement, uncountable);
        }

        return items;
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
            var id = _yaml.Identifier(key, "faction id");
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

    internal List<CharacterDefinition> ReadCharacters(YamlNode? node, YamlMappingNode root, Ruleset ruleset, Dictionary<ItemId, ItemDefinition> items)
    {
        var characters = new List<CharacterDefinition>();
        var map = node is null ? null : _yaml.Mapping(node, ManifestVocabulary.Characters);
        if (map is null || !map.Children.Any(c => Scalar(c.Key) == EntityId.Player.Value))
        {
            _yaml.Diagnostics.Error(CompilerDiagnosticCodes.MissingRequiredConfiguration, "The story configuration must declare the protagonist under 'characters: player:'.", node is null ? _yaml.At(root) : _yaml.At(node));
        }

        foreach (var (key, value) in map?.Children ?? Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
        {
            var id = _yaml.Identifier(key, "character id");
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
            var stats = ReadStatMap(Get(body, ManifestVocabulary.Stats), ruleset);
            var inventory = ReadInventory(Get(body, ManifestVocabulary.Inventory), items, characterId);
            var nameAgreement = Get(body, ManifestVocabulary.NameAgreement) is { } number
                ? ReadGrammaticalNumber(number, ManifestVocabulary.NameAgreement)
                : GrammaticalNumber.Singular;
            characters.Add(new CharacterDefinition(characterId, firstName, lastName, pronouns, stats, inventory, nameAgreement));
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
            var rawId = _yaml.Identifier(key, "stat id");
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

    private Dictionary<ItemId, int> ReadInventory(YamlNode? node, Dictionary<ItemId, ItemDefinition> items, EntityId character)
    {
        var inventory = new Dictionary<ItemId, int>();
        if (node is null)
        {
            return inventory;
        }

        IEnumerable<(YamlNode Key, YamlNode? QuantityNode, int Quantity)> entries = node switch
        {
            YamlSequenceNode seq => seq.Children.Select(c => (c, (YamlNode?)null, 1)),
            YamlMappingNode map => map.Children.Select(c => (c.Key, (YamlNode?)c.Value, _yaml.Int(c.Value) ?? 1)),
            _ => _yaml.Error<IEnumerable<(YamlNode, YamlNode?, int)>>(node, CompilerDiagnosticCodes.InvalidConfigurationValue, "inventory must be a list of item ids or a map of item id to quantity.", []),
        };

        foreach (var (key, quantityNode, quantity) in entries)
        {
            var rawId = _yaml.Identifier(key, "item id");
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            if (!items.ContainsKey(id))
            {
                _yaml.Error(key, CompilerDiagnosticCodes.UnknownConfigurationReference, $"Unknown item '{id}'. Declare it under items.", 0);
                continue;
            }

            var startingQuantity = quantity;
            if (quantityNode is not null && quantity < 0)
            {
                startingQuantity = _yaml.Error(quantityNode, CompilerDiagnosticCodes.InvalidConfigurationValue,
                    $"Starting inventory quantity for item '{id}' on character '{character}' must be non-negative.", 0);
            }

            inventory[id] = inventory.GetValueOrDefault(id) + startingQuantity;
        }

        return inventory;
    }

    internal List<FlagId> ReadFlags(YamlNode? node)
    {
        var flags = new List<FlagId>();
        if (node is null) return flags;
        foreach (var item in _yaml.Sequence(node, ManifestVocabulary.Flags))
        {
            if (_yaml.Identifier(item, "flag id") is { } id) flags.Add(new FlagId(id));
        }

        return flags;
    }

    internal (Dictionary<VarId, int> Numbers, Dictionary<VarId, string> Text) ReadVars(YamlNode? node)
    {
        var numericVars = new Dictionary<VarId, int>();
        var textVars = new Dictionary<VarId, string>();
        if (node is null || _yaml.Mapping(node, ManifestVocabulary.Vars) is not { } map)
        {
            return (numericVars, textVars);
        }

        foreach (var (key, value) in map.Children)
        {
            if (_yaml.Identifier(key, "variable id") is not { } id) continue;
            if (value is YamlScalarNode { Style: ScalarStyle.DoubleQuoted or ScalarStyle.SingleQuoted } text)
            {
                textVars.Add(new VarId(id), text.Value ?? "");
            }
            else if (value is YamlScalarNode { Value: { } raw } &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                numericVars.Add(new VarId(id), number);
            }
            else
            {
                _yaml.Error(value, CompilerDiagnosticCodes.InvalidConfigurationValue, $"Variable '{id}' needs a whole number or quoted text value.", 0);
            }
        }

        return (numericVars, textVars);
    }
}
