using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Parsing;

/// <summary>An expression or effect list written in story configuration, parsed later once all symbols are known.</summary>
internal sealed record RawTrigger(string Id, string When, SourceLocation WhenLocation, string Do, SourceLocation DoLocation, bool Once);

/// <summary>Core settings and declarations assembled from the story's configuration inputs.</summary>
internal sealed class StoryManifest
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = "0.0.0";
    public string Start { get; set; } = string.Empty;
    public SourceLocation StartLocation { get; set; }
    public RandomnessMode Randomness { get; set; } = RandomnessMode.ChecksOnly;
    public ulong? Seed { get; set; }
    public bool UndoEnabled { get; set; } = true;
    public CharacterCreation? Creation { get; set; }
    public required WorldDefinition World { get; init; }
    public List<RawTrigger> Triggers { get; } = [];

    /// <summary>Raw trigger text, for the flag/variable pre-scan.</summary>
    public IEnumerable<string> EffectText => Triggers.Select(t => t.Do);
}

/// <summary>
/// Reads story configuration with the YamlDotNet representation model (no reflection, so it is trim/WASM safe and keeps
/// line numbers for diagnostics). Codes are <c>TW4xxx</c>.
/// </summary>
internal sealed class StoryYamlReader
{
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private readonly CompilerOptions _options;
    private readonly Dictionary<YamlNode, string> _files = new(ReferenceEqualityComparer.Instance);
    private AnalysisCollector? _analysis;

    private StoryYamlReader(string file, DiagnosticBag diagnostics, CompilerOptions options)
    {
        _file = file;
        _diagnostics = diagnostics;
        _options = options;
    }

    public static StoryManifest? Read(StorySource source, DiagnosticBag diagnostics, CompilerOptions options,
        AnalysisCollector? analysis = null)
    {
        var reader = new StoryYamlReader(StorySource.ManifestFileName, diagnostics, options) { _analysis = analysis };
        var root = reader.Parse(StorySource.ManifestFileName, source.Files[StorySource.ManifestFileName]);
        if (root is null) return null;
        analysis?.Manifest(root);
        var sections = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, Dictionary<string, SourceLocation>>(StringComparer.Ordinal);
        var flags = new YamlSequenceNode();
        var flagLocations = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
        YamlNode? triggers = null;
        SourceLocation? triggerLocation = null;
        void Duplicate(string kind, string id, YamlNode node, SourceLocation first) =>
            diagnostics.Error("TW4007", $"{kind} '{id}' is declared more than once (first at {first}).", reader.At(node));
        void Collect(YamlMappingNode document)
        {
            reader.CheckKeys(document, "game", "characters", "factions", "items", "flags", "vars", "triggers");
            foreach (var name in new[] { "characters", "factions", "items", "vars" })
            {
                if (Get(document, name) is not { } node || reader.Mapping(node, name) is not { } map) continue;
                if (!sections.TryGetValue(name, out var combined))
                {
                    sections.Add(name, combined = new YamlMappingNode());
                    declarations.Add(name, new(StringComparer.Ordinal));
                }
                foreach (var (key, value) in map.Children)
                {
                    var id = Scalar(key);
                    if (id is not null && !declarations[name].TryAdd(id, reader.At(key)))
                        Duplicate(name, id, key, declarations[name][id]);
                    else
                        combined.Add(key, value);
                }
            }
            if (Get(document, "flags") is { } flagNode)
            {
                foreach (var flag in reader.Sequence(flagNode, "flags"))
                {
                    var id = Scalar(flag);
                    if (id is not null && !flagLocations.TryAdd(id, reader.At(flag)))
                        Duplicate("Flag", id, flag, flagLocations[id]);
                    else
                        flags.Add(flag);
                }
            }
            if (Get(document, "triggers") is { } triggerNode)
            {
                if (triggerLocation is { } first)
                    diagnostics.Error("TW4007", $"Only one 'triggers' section is allowed (first at {first}).", reader.At(triggerNode));
                else
                {
                    triggers = triggerNode;
                    triggerLocation = reader.At(triggerNode);
                }
            }
        }
        Collect(root);
        foreach (var (file, text) in source.Files
            .Where(file => file.Key != StorySource.ManifestFileName && StorySource.IsConfigurationFile(file.Key))
            .OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (file.EndsWith("/" + StorySource.ManifestFileName, StringComparison.Ordinal))
            {
                diagnostics.Error("TW4002",
                    $"'{StorySource.ManifestFileName}' is reserved for the single root story configuration; found additional manifest '{file}'.",
                    new SourceLocation(file, 1, 1));
                continue;
            }
            if (!file.Contains('/'))
            {
                diagnostics.Error("TW4002",
                    $"Only '{StorySource.ManifestFileName}' is allowed at the story root; found extra configuration file '{file}'.",
                    new SourceLocation(file, 1, 1));
                continue;
            }
            if (reader.Parse(file, text) is not { } document) continue;
            analysis?.Manifest(document, file);
            if (Get(document, "game") is { } game)
                diagnostics.Error("TW4002", "'game:' is allowed only in the root story configuration.", reader.At(game));
            Collect(document);
        }
        var assembled = new YamlMappingNode();
        if (Get(root, "game") is { } rootGame) assembled.Add("game", rootGame);
        foreach (var (name, map) in sections) assembled.Add(name, map);
        assembled.Add("flags", flags);
        if (triggers is not null) assembled.Add("triggers", triggers);
        return reader.Read(assembled);
    }

    private SourceLocation At(YamlNode node) => new(_files.GetValueOrDefault(node, _file),
        Math.Max(1, (int)node.Start.Line), Math.Max(1, (int)node.Start.Column));

    private YamlMappingNode? Parse(string file, string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            _diagnostics.Error("TW4001", $"Invalid YAML: {ex.InnerException?.Message ?? ex.Message}", new SourceLocation(file, (int)ex.Start.Line, (int)ex.Start.Column));
            return null;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            _diagnostics.Error("TW4001", "A story configuration file must contain one mapping (key: value pairs).", new SourceLocation(file, 1, 1));
            return null;
        }
        void Register(YamlNode node)
        {
            if (!_files.TryAdd(node, file)) return;
            if (node is YamlMappingNode map)
                foreach (var (key, value) in map.Children) { Register(key); Register(value); }
            else if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) Register(child);
        }
        Register(root);
        return root;
    }

    private StoryManifest Read(YamlMappingNode root)
    {
        var gameNode = Get(root, "game");
        if (gameNode is null)
        {
            _diagnostics.Error("TW4002", "The root story configuration must declare a 'game:' mapping.", At(root));
        }
        var game = gameNode is null ? new YamlMappingNode() : Mapping(gameNode, "game") ?? new YamlMappingNode();
        CheckKeys(game, "id", "title", "version", "start", "randomness", "seed", "undo", "ruleset", "creation");

        var ruleset = ReadRuleset(Get(game, "ruleset"));
        var items = ReadItems(Get(root, "items"), ruleset);
        var factions = ReadFactions(Get(root, "factions"));
        var characters = ReadCharacters(Get(root, "characters"), root, ruleset, items);
        var flags = ReadFlags(Get(root, "flags"));
        var (numericVars, textVars) = ReadVars(Get(root, "vars"));

        var manifest = new StoryManifest
        {
            World = new WorldDefinition(ruleset, characters, items.Values, factions, flags, numericVars, textVars),
            Id = RequiredIdentifier(game, "id") ?? string.Empty,
            Title = RequiredScalar(game, "title") ?? string.Empty,
            Version = Scalar(Get(game, "version")) ?? "0.0.0",
            Start = RequiredIdentifier(game, "start") ?? string.Empty,
            StartLocation = Get(game, "start") is { } startNode ? At(startNode) : At(game),
        };

        if (Get(game, "randomness") is { } randomness)
        {
            manifest.Randomness = Scalar(randomness) switch
            {
                "off" => RandomnessMode.Off,
                "checks-only" => RandomnessMode.ChecksOnly,
                var other => Error(randomness, "TW4003", $"randomness must be 'off' or 'checks-only', not '{other}'.", RandomnessMode.ChecksOnly),
            };
        }

        if (Get(game, "seed") is { } seed)
        {
            manifest.Seed = ulong.TryParse(Scalar(seed), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : Error<ulong?>(seed, "TW4003", "seed must be a non-negative whole number.", null);
        }

        if (Get(game, "undo") is { } undo)
        {
            manifest.UndoEnabled = bool.TryParse(Scalar(undo), out var enabled)
                ? enabled
                : Error(undo, "TW4003", "undo must be true or false.", true);
        }

        ReadTriggers(Get(root, "triggers"), manifest.Triggers);
        manifest.Creation = ReadCreation(Get(game, "creation"), ruleset);
        return manifest;
    }

    private CharacterCreation? ReadCreation(YamlNode? node, Ruleset ruleset)
    {
        if (node is null || Mapping(node, "creation") is not { } map)
        {
            return null;
        }

        CheckKeys(map, "name", "pronouns", "presets", "stats");
        var editName = false;
        if (Get(map, "name") is { } name && !bool.TryParse(Scalar(name), out editName))
        {
            Error(name, "TW4003", "creation.name must be true or false.", 0);
        }

        var pronouns = new List<string>();
        if (Get(map, "pronouns") is { } pronounNode)
        {
            foreach (var value in Sequence(pronounNode, "creation.pronouns"))
            {
                var keyword = Scalar(value) ?? "";
                if (keyword is not ("she" or "he" or "they" or "it") || pronouns.Contains(keyword))
                {
                    Error(value, "TW4003", "creation.pronouns must contain distinct built-in keywords: she, he, they, it.", 0);
                }
                else
                {
                    pronouns.Add(keyword);
                }
            }
        }

        var stats = new Dictionary<StatId, IReadOnlyList<int>>();
        if (Get(map, "stats") is { } statNode && Mapping(statNode, "creation.stats") is { } statMap)
        {
            foreach (var (key, value) in statMap.Children)
            {
                var rawId = Identifier(key, "creation stat id");
                if (rawId is null) continue;
                var id = new StatId(rawId);
                if (!ruleset.StatsById.TryGetValue(id, out var definition))
                {
                    Error(key, "TW4005", $"Unknown creation stat '{id}'.", 0);
                    continue;
                }

                var values = new List<int>();
                foreach (var option in Sequence(value, "creation stat values"))
                {
                    if (Int(option) is { } number && definition.Clamp(number) == number && !values.Contains(number))
                    {
                        values.Add(number);
                    }
                    else
                    {
                        Error(option, "TW4003", $"Creation values for '{id}' must be distinct integers within ruleset bounds.", 0);
                    }
                }

                if (values.Count is < 1 or > 100)
                {
                    Error(value, "TW4003", "Each creation stat needs 1 to 100 legal values.", 0);
                }

                stats[id] = values.AsReadOnly();
            }
        }

        if (stats.Count > 32)
        {
            Error(map, "TW4003", "Character creation supports at most 32 selectable stats.", 0);
        }

        var presets = new List<CharacterPreset>();
        if (Get(map, "presets") is { } presetNode && Mapping(presetNode, "creation.presets") is { } presetMap)
        {
            foreach (var (key, value) in presetMap.Children)
            {
                if (Mapping(value, "creation preset") is not { } preset)
                {
                    continue;
                }

                CheckKeys(preset, "label", "stats");
                var id = Identifier(key, "preset id");
                if (id is null)
                {
                    continue;
                }
                if (id.Length > 80)
                {
                    Error(key, "TW4003", "Preset ids must contain at most 80 characters.", 0);
                    continue;
                }

                var presetStats = ReadStatMap(Get(preset, "stats"), ruleset);
                foreach (var (stat, number) in presetStats)
                {
                    if (ruleset.StatsById[stat].Clamp(number) != number ||
                        stats.TryGetValue(stat, out var choices) && !choices.Contains(number))
                    {
                        Error(value, "TW4003", $"Preset stat '{stat}' must respect ruleset bounds and its selectable values.", 0);
                    }
                }

                presets.Add(new CharacterPreset(id, Scalar(Get(preset, "label")) ?? id, presetStats));
            }

            if (presets.Count > 16)
            {
                Error(presetNode, "TW4003", "Character creation supports at most 16 presets.", 0);
            }
        }

        return new CharacterCreation { EditName = editName, Pronouns = pronouns.AsReadOnly(), Stats = stats, Presets = presets.AsReadOnly() };
    }

    private Ruleset ReadRuleset(YamlNode? node)
    {
        if (node is null)
        {
            return _options.Rulesets[Rulesets.Generic.GenericRuleset.Id]();
        }

        var map = Mapping(node, "ruleset");
        if (map is null)
        {
            return _options.Rulesets[Rulesets.Generic.GenericRuleset.Id]();
        }

        CheckKeys(map, "base", "disable", "stats");
        var baseNode = Get(map, "base");
        var baseId = baseNode is null ? Rulesets.Generic.GenericRuleset.Id
            : Identifier(baseNode, "ruleset id") ?? Rulesets.Generic.GenericRuleset.Id;
        if (!_options.Rulesets.TryGetValue(baseId, out var factory))
        {
            Error(Get(map, "base")!, "TW4004", $"Unknown ruleset '{baseId}'. Available: {string.Join(", ", _options.Rulesets.Keys)}.", 0);
            factory = _options.Rulesets[Rulesets.Generic.GenericRuleset.Id];
        }

        var baseRuleset = factory();
        var builder = baseRuleset.ToBuilder();
        if (Get(map, "disable") is { } disable)
        {
            _analysis?.DisabledStats(disable, baseRuleset);
            foreach (var item in Sequence(disable, "ruleset.disable"))
            {
                var rawId = Identifier(item, "stat id");
                if (rawId is null) continue;
                var id = new StatId(rawId);
                if (builder.HasStat(id))
                {
                    builder.RemoveStat(id);
                }
                else
                {
                    Error(item, "TW4005", $"Cannot disable '{id}': the '{baseId}' ruleset has no such stat.", 0);
                }
            }
        }

        if (Get(map, "stats") is { } stats)
        {
            foreach (var item in Sequence(stats, "ruleset.stats"))
            {
                if (Mapping(item, "stat") is not { } stat)
                {
                    continue;
                }

                CheckKeys(stat, "id", "name", "category", "min", "max", "default");
                var id = RequiredIdentifier(stat, "id");
                if (id is null)
                {
                    continue;
                }

                var category = Scalar(Get(stat, "category")) ?? "custom";
                if (!Enum.TryParse<StatCategory>(category, ignoreCase: true, out var parsedCategory))
                {
                    Error(Get(stat, "category")!, "TW4003", $"Unknown stat category '{category}'. Use attribute, skill, resource, progression or custom.", 0);
                }

                var min = Int(Get(stat, "min")) ?? 0;
                var max = Int(Get(stat, "max")) ?? int.MaxValue;
                if (min > max)
                {
                    Error(Get(stat, "min") ?? stat, "TW4003", $"Stat '{id}' minimum ({min}) must not be greater than its maximum ({max}).", 0);
                    continue;
                }

                var definition = new StatDefinition(new StatId(id), Scalar(Get(stat, "name")) ?? id, parsedCategory, min, max, Int(Get(stat, "default")) ?? min);
                if (builder.HasStat(definition.Id))
                {
                    builder.ReplaceStat(definition);
                }
                else
                {
                    builder.AddStat(definition);
                }
            }
        }

        return builder.Build();
    }

    private Dictionary<ItemId, ItemDefinition> ReadItems(YamlNode? node, Ruleset ruleset)
    {
        var items = new Dictionary<ItemId, ItemDefinition>();
        if (node is null || Mapping(node, "items") is not { } map)
        {
            return items;
        }

        foreach (var (key, value) in map.Children)
        {
            var rawId = Identifier(key, "item id");
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            var name = id.Value;
            var modifiers = new Dictionary<StatId, int>();
            if (value is YamlMappingNode body)
            {
                CheckKeys(body, "name", "modifiers");
                name = Scalar(Get(body, "name")) ?? name;
                modifiers = ReadStatMap(Get(body, "modifiers"), ruleset);
            }
            else if (Scalar(value) is { Length: > 0 } shortName)
            {
                name = shortName;
            }

            items[id] = new ItemDefinition(id, name, modifiers);
        }

        return items;
    }

    private List<FactionDefinition> ReadFactions(YamlNode? node)
    {
        var factions = new List<FactionDefinition>();
        if (node is null || Mapping(node, "factions") is not { } map)
        {
            return factions;
        }

        foreach (var (key, value) in map.Children)
        {
            var id = Identifier(key, "faction id");
            if (id is null) continue;
            var name = id;
            var standing = 0;
            if (value is YamlMappingNode body)
            {
                CheckKeys(body, "name", "standing");
                name = Scalar(Get(body, "name")) ?? id;
                standing = Int(Get(body, "standing")) ?? 0;
            }

            factions.Add(new FactionDefinition(new FactionId(id), name, standing));
        }

        return factions;
    }

    private List<CharacterDefinition> ReadCharacters(YamlNode? node, YamlMappingNode root, Ruleset ruleset, Dictionary<ItemId, ItemDefinition> items)
    {
        var characters = new List<CharacterDefinition>();
        var map = node is null ? null : Mapping(node, "characters");
        if (map is null || !map.Children.Any(c => Scalar(c.Key) == EntityId.Player.Value))
        {
            _diagnostics.Error("TW4002", "The story configuration must declare the protagonist under 'characters: player:'.", node is null ? At(root) : At(node));
        }

        foreach (var (key, value) in map?.Children ?? Enumerable.Empty<KeyValuePair<YamlNode, YamlNode>>())
        {
            var id = Identifier(key, "character id");
            if (id is null) continue;
            if (Mapping(value, $"characters.{id}") is not { } body)
            {
                continue;
            }

            CheckKeys(body, "first-name", "last-name", "pronouns", "stats", "inventory", "name-agreement");
            var firstName = RequiredScalar(body, "first-name") ?? id;
            var lastName = Scalar(Get(body, "last-name")) ?? string.Empty;
            var pronouns = ReadPronouns(Get(body, "pronouns"));
            var stats = ReadStatMap(Get(body, "stats"), ruleset);
            var inventory = ReadInventory(Get(body, "inventory"), items);
            var nameAgreement = Get(body, "name-agreement") is { } number
                ? ReadGrammaticalNumber(number, "name-agreement")
                : GrammaticalNumber.Singular;
            characters.Add(new CharacterDefinition(new EntityId(id), firstName, lastName, pronouns, stats, inventory, nameAgreement));
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
            CheckKeys(map, "subject", "object", "possessive-pronoun", "reflexive", "agreement");
            if (Get(map, "possessive") is { } oldPossessive)
            {
                Error(oldPossessive, "TW4003", "The flat 'possessive' field is no longer supported. Use 'possessive-pronoun: { determiner: xer, independent: xers }'.", 0);
            }

            var possessive = Get(map, "possessive-pronoun");
            var determiner = "determiner";
            var independent = "independent";
            if (possessive is YamlMappingNode forms)
            {
                CheckKeys(forms, "determiner", "independent");
                determiner = RequiredScalar(forms, "determiner") ?? determiner;
                independent = RequiredScalar(forms, "independent") ?? independent;
            }
            else if (possessive is null)
            {
                Error(map, "TW4002", "Missing required 'possessive-pronoun'. Use 'possessive-pronoun: { determiner: xer, independent: xers }'.", 0);
            }
            else
            {
                Error(possessive, "TW4003", "'possessive-pronoun' must be a mapping with 'determiner' and 'independent' values. Use 'possessive-pronoun: { determiner: xer, independent: xers }'.", 0);
            }

            string Form(string key) => RequiredScalar(map, key) ?? key;
            var agreement = Get(map, "agreement") is { } number
                ? ReadGrammaticalNumber(number, "agreement")
                : Error(map, "TW4002", "Custom pronoun sets require 'agreement: singular' or 'agreement: plural'. Agreement describes grammar, not population.", GrammaticalNumber.Singular);
            return new Pronouns(Form("subject"), Form("object"), determiner, independent, Form("reflexive"), agreement);
        }

        var keyword = Scalar(node) ?? string.Empty;
        return Pronouns.FromKeyword(keyword)
            ?? Error(node, "TW4003", $"Unknown pronouns '{keyword}'. Use she, he, they, it, or a mapping with subject, object, reflexive, agreement: singular|plural and possessive-pronoun: {{ determiner: xer, independent: xers }}.", Pronouns.They);
    }

    private GrammaticalNumber ReadGrammaticalNumber(YamlNode node, string field) => Scalar(node) switch
    {
        "singular" => GrammaticalNumber.Singular,
        "plural" => GrammaticalNumber.Plural,
        _ => Error(node, "TW4003", $"'{field}' must be 'singular' or 'plural'; it describes grammar, not population.", GrammaticalNumber.Singular),
    };

    private Dictionary<StatId, int> ReadStatMap(YamlNode? node, Ruleset ruleset)
    {
        var stats = new Dictionary<StatId, int>();
        if (node is null || Mapping(node, "stats") is not { } map)
        {
            return stats;
        }

        foreach (var (key, value) in map.Children)
        {
            var rawId = Identifier(key, "stat id");
            if (rawId is null) continue;
            var id = new StatId(rawId);
            if (!ruleset.HasStat(id))
            {
                Error(key, "TW4005", $"Unknown stat '{id}' (ruleset '{ruleset.Id}'). Add it under ruleset.stats.", 0);
                continue;
            }

            stats[id] = Int(value) ?? 0;
        }

        return stats;
    }

    private Dictionary<ItemId, int> ReadInventory(YamlNode? node, Dictionary<ItemId, ItemDefinition> items)
    {
        var inventory = new Dictionary<ItemId, int>();
        if (node is null)
        {
            return inventory;
        }

        IEnumerable<(YamlNode Key, int Quantity)> entries = node switch
        {
            YamlSequenceNode seq => seq.Children.Select(c => (c, 1)),
            YamlMappingNode map => map.Children.Select(c => (c.Key, Int(c.Value) ?? 1)),
            _ => Error<IEnumerable<(YamlNode, int)>>(node, "TW4003", "inventory must be a list of item ids or a map of item id to quantity.", []),
        };

        foreach (var (key, quantity) in entries)
        {
            var rawId = Identifier(key, "item id");
            if (rawId is null) continue;
            var id = new ItemId(rawId);
            if (!items.ContainsKey(id))
            {
                Error(key, "TW4005", $"Unknown item '{id}'. Declare it under items.", 0);
                continue;
            }

            inventory[id] = inventory.GetValueOrDefault(id) + quantity;
        }

        return inventory;
    }

    private List<FlagId> ReadFlags(YamlNode? node)
    {
        var flags = new List<FlagId>();
        if (node is null) return flags;
        foreach (var item in Sequence(node, "flags"))
        {
            if (Identifier(item, "flag id") is { } id) flags.Add(new FlagId(id));
        }

        return flags;
    }

    private (Dictionary<VarId, int> Numbers, Dictionary<VarId, string> Text) ReadVars(YamlNode? node)
    {
        var numericVars = new Dictionary<VarId, int>();
        var textVars = new Dictionary<VarId, string>();
        if (node is null || Mapping(node, "vars") is not { } map)
        {
            return (numericVars, textVars);
        }

        foreach (var (key, value) in map.Children)
        {
            if (Identifier(key, "variable id") is not { } id) continue;
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
                Error(value, "TW4003", $"Variable '{id}' needs a whole number or quoted text value.", 0);
            }
        }

        return (numericVars, textVars);
    }

    private void ReadTriggers(YamlNode? node, List<RawTrigger> triggers)
    {
        if (node is null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Sequence(node, "triggers"))
        {
            if (Mapping(item, "trigger") is not { } map)
            {
                continue;
            }

            CheckKeys(map, "id", "when", "do", "once");
            if (_analysis is not null)
            {
                foreach (var (key, kind) in new[] { ("when", ExpressionContextKind.Condition), ("do", ExpressionContextKind.Effects) })
                {
                    if (Get(map, key) is not YamlScalarNode scalar) continue;
                    _analysis.Scalar(scalar, At(scalar).File);
                    _analysis.Expression(kind, scalar.Value ?? string.Empty, At(scalar));
                }
            }
            var idNode = Get(map, "id");
            var id = RequiredIdentifier(map, "id");
            if (id is null)
            {
                continue;
            }

            if (!ids.Add(id))
            {
                _diagnostics.Error("TW4007", $"Trigger id '{id}' is declared more than once.", At(idNode ?? item));
                continue;
            }

            var when = RequiredScalar(map, "when");
            var effects = RequiredScalar(map, "do");
            if (when is null || effects is null)
            {
                continue;
            }

            triggers.Add(new RawTrigger(id, when, At(Get(map, "when")!), effects, At(Get(map, "do")!), Scalar(Get(map, "once")) is "true" or "yes"));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private string? RequiredScalar(YamlMappingNode map, string key)
    {
        var node = Get(map, key);
        if (node is null)
        {
            _diagnostics.Error("TW4002", $"Missing required '{key}'.", At(map));
            return null;
        }

        if (Scalar(node) is not { Length: > 0 } value)
        {
            _diagnostics.Error("TW4003", $"'{key}' must be a single value.", At(node));
            return null;
        }

        return value;
    }

    private string? RequiredIdentifier(YamlMappingNode map, string key)
    {
        var value = RequiredScalar(map, key);
        var node = Get(map, key);
        return value is null || node is null ? null : Identifier(node, $"'{key}'");
    }

    private string? Identifier(YamlNode node, string what)
    {
        var value = Scalar(node);
        if (value is null || !IdentifierRules.IsValid(value))
        {
            var found = value is null ? "a non-string value" : $"'{value}'";
            return Error<string?>(node, "TW4003",
                $"Invalid {what} {found}. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators.", null);
        }

        return value;
    }

    private int? Int(YamlNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (int.TryParse(Scalar(node), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        _diagnostics.Error("TW4003", $"Expected a whole number but found '{Scalar(node) ?? node.NodeType.ToString()}'.", At(node));
        return null;
    }

    private YamlMappingNode? Mapping(YamlNode node, string what)
    {
        if (node is YamlMappingNode map)
        {
            return map;
        }

        if (node is YamlScalarNode { Value: null or "" or "~" })
        {
            return null;
        }

        _diagnostics.Error("TW4003", $"'{what}' must be a mapping (key: value pairs).", At(node));
        return null;
    }

    private IEnumerable<YamlNode> Sequence(YamlNode node, string what)
    {
        if (node is YamlSequenceNode seq)
        {
            return seq.Children;
        }

        if (node is YamlScalarNode { Value: null or "" or "~" })
        {
            return [];
        }

        _diagnostics.Error("TW4003", $"'{what}' must be a list.", At(node));
        return [];
    }

    private void CheckKeys(YamlMappingNode map, params string[] allowed)
    {
        foreach (var key in map.Children.Keys)
        {
            if (Scalar(key) is { } name && !allowed.Contains(name))
            {
                if (name == "plural" && (allowed.Contains("agreement") || allowed.Contains("name-agreement")))
                {
                    var replacement = allowed.Contains("agreement") ? "agreement" : "name-agreement";
                    Error(key, "TW4003", $"The obsolete 'plural' key is no longer supported. Use '{replacement}: singular' or '{replacement}: plural' for grammar, not population.", 0);
                    continue;
                }

                _diagnostics.Warning("TW4006", $"Unknown key '{name}' (ignored). Expected one of: {string.Join(", ", allowed)}.", At(key));
            }
        }
    }

    private T Error<T>(YamlNode node, string code, string message, T fallback)
    {
        _diagnostics.Error(code, message, At(node));
        return fallback;
    }
}
