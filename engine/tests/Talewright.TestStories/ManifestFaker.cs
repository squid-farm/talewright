using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Talewright.Testing;

/// <summary>Builds manifest YAML and returns references for the declarations it contains.</summary>
public sealed class ManifestFaker
{
    private enum Section
    {
        Flags,
        Variables,
        Items,
        Characters,
        Factions,
        Triggers,
    }

    private readonly List<TaleFlagRef> _flags = [];
    private readonly List<(TaleVariableRef Reference, object Value)> _variables = [];
    private readonly List<(TaleItemRef Reference, string Name, KeyValuePair<string, int>[] Modifiers, string? NameAgreement, bool? Uncountable)> _items = [];
    private readonly List<(TaleCharacterRef Reference, string FirstName, string? LastName, string? Pronouns, KeyValuePair<string, int>[] Stats, KeyValuePair<string, int>[] Inventory)> _characters = [];
    private readonly List<(TaleFactionRef Reference, string Name, int Standing)> _factions = [];
    private readonly List<(string Id, string When, string Do, bool Once, bool Save)> _triggers = [];
    private readonly List<(string Id, int[] Values)> _creationStats = [];
    private readonly List<(string Id, string Label, KeyValuePair<string, int>[] Stats)> _creationPresets = [];
    private string[]? _creationPronouns;
    private readonly List<Section> _sections = [];

    public ManifestFaker(string id, string title, string start, int seed)
        : this(id, title, start, seed >= 0 ? (ulong)seed : throw new ArgumentOutOfRangeException(nameof(seed)))
    {
    }

    public ManifestFaker(string id, string title, string start, ulong? seed = null)
    {
        FakerIdentifier.Require(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        FakerIdentifier.Require(start, nameof(start));
        Id = id;
        Title = title;
        Start = start;
        Seed = seed;
    }

    public string Id { get; }
    public string Title { get; }
    public string Start { get; }
    public ulong? Seed { get; }
    public string? Version { get; init; }
    public string? BaseRuleset { get; init; }
    public string? Randomness { get; init; }
    public bool? Undo { get; init; }
    public bool? Autosave { get; init; }
    /// <summary>Writes <c>character-sheet: true|false</c>; takes precedence over <see cref="CharacterSheetSections"/>.</summary>
    public bool? CharacterSheet { get; init; }
    /// <summary>Writes a <c>character-sheet</c> mapping of section names to true or false.</summary>
    public IReadOnlyDictionary<string, bool>? CharacterSheetSections { get; init; }
    public bool? CreationName { get; init; }

    internal void IncludeItemsSection() => IncludeSection(Section.Items);

    public void CreationPronouns(params string[] keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        _creationPronouns = keywords.ToArray();
    }

    public void CreationStat(string id, params int[] values)
    {
        FakerIdentifier.Require(id, nameof(id));
        ArgumentNullException.ThrowIfNull(values);
        if (_creationStats.Any(stat => stat.Id == id))
        {
            throw new ArgumentException($"Creation stat '{id}' is already declared.", nameof(id));
        }

        _creationStats.Add((id, values.ToArray()));
    }

    public void CreationPreset(string id, string label, IReadOnlyDictionary<string, int>? stats = null)
    {
        FakerIdentifier.Require(id, nameof(id));
        ArgumentNullException.ThrowIfNull(label);
        if (_creationPresets.Any(preset => preset.Id == id))
        {
            throw new ArgumentException($"Creation preset '{id}' is already declared.", nameof(id));
        }

        _creationPresets.Add((id, label, stats?.ToArray() ?? []));
    }

    public TaleFlagRef Flag(string id)
    {
        var flag = new TaleFlagRef(id);
        if (_flags.Any(existing => existing.Id == id))
        {
            throw new ArgumentException($"Flag '{id}' is already declared.", nameof(id));
        }

        _flags.Add(flag);
        IncludeSection(Section.Flags);
        return flag;
    }

    public TaleVariableRef Number(string id, int value) => Variable(id, value);

    public TaleVariableRef Text(string id, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Variable(id, value);
    }

    public TaleItemRef Item(
        string id,
        string name,
        IReadOnlyDictionary<string, int>? modifiers = null,
        string? nameAgreement = null,
        bool? uncountable = null)
    {
        var reference = new TaleItemRef(id);
        ArgumentNullException.ThrowIfNull(name);
        if (_items.Any(item => item.Reference.Id == id))
        {
            throw new ArgumentException($"Item '{id}' is already declared.", nameof(id));
        }

        if (modifiers is not null)
        {
            foreach (var modifier in modifiers.Keys)
            {
                FakerIdentifier.Require(modifier, nameof(modifiers));
            }
        }

        _items.Add((reference, name, modifiers?.ToArray() ?? [], nameAgreement, uncountable));
        IncludeSection(Section.Items);
        return reference;
    }

    public TaleCharacterRef Character(
        string id,
        string firstName,
        string? lastName = null,
        IReadOnlyDictionary<string, int>? stats = null,
        string? pronouns = null,
        IReadOnlyDictionary<string, int>? inventory = null)
    {
        var reference = new TaleCharacterRef(id);
        ArgumentNullException.ThrowIfNull(firstName);
        if (_characters.Any(character => character.Reference.Id == id))
        {
            throw new ArgumentException($"Character '{id}' is already declared.", nameof(id));
        }

        if (stats is not null)
        {
            foreach (var stat in stats.Keys)
            {
                FakerIdentifier.Require(stat, nameof(stats));
            }
        }

        if (inventory is not null)
        {
            foreach (var item in inventory.Keys)
            {
                FakerIdentifier.Require(item, nameof(inventory));
            }
        }

        _characters.Add((reference, firstName, lastName, pronouns, stats?.ToArray() ?? [], inventory?.ToArray() ?? []));
        IncludeSection(Section.Characters);
        return reference;
    }

    public TaleFactionRef Faction(string id, string name, int standing = 0)
    {
        var reference = new TaleFactionRef(id);
        ArgumentNullException.ThrowIfNull(name);
        if (_factions.Any(faction => faction.Reference.Id == id))
        {
            throw new ArgumentException($"Faction '{id}' is already declared.", nameof(id));
        }

        _factions.Add((reference, name, standing));
        IncludeSection(Section.Factions);
        return reference;
    }

    public void Trigger(string id, string when, string effect, bool once = false, bool save = false)
    {
        FakerIdentifier.Require(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(when);
        ArgumentException.ThrowIfNullOrWhiteSpace(effect);
        if (_triggers.Any(trigger => trigger.Id == id))
        {
            throw new ArgumentException($"Trigger '{id}' is already declared.", nameof(id));
        }

        _triggers.Add((id, when, effect, once, save));
        IncludeSection(Section.Triggers);
    }

    public string Build(bool trailingNewline = false)
    {
        var manifest = Mapping();
        Add(manifest, "game", BuildGame());
        foreach (var section in _sections)
        {
            Add(manifest, SectionKey(section), BuildSection(section));
        }

        var textVariableIds = _variables
            .Where(variable => variable.Value is string)
            .Select(variable => variable.Reference.Id)
            .ToHashSet(StringComparer.Ordinal);
        var root = ToYamlNode(manifest) as YamlMappingNode
            ?? throw new InvalidOperationException("The generated manifest root must be a mapping.");

        if (textVariableIds.Count > 0)
        {
            var variablesEntry = root.Children.SingleOrDefault(entry =>
                entry.Key is YamlScalarNode { Value: "vars" });
            if (variablesEntry.Value is not YamlMappingNode variables)
            {
                throw new InvalidOperationException("The generated text variables must be a mapping.");
            }

            foreach (var (key, value) in variables.Children)
            {
                if (key is YamlScalarNode { Value: { } id } && textVariableIds.Contains(id))
                {
                    if (value is not YamlScalarNode scalar)
                    {
                        throw new InvalidOperationException($"Generated text variable '{id}' must be a scalar.");
                    }
                    scalar.Style = ScalarStyle.DoubleQuoted;
                }
            }
        }

        var stream = new YamlStream(new YamlDocument(root));
        using (var writer = new StringWriter(CultureInfo.InvariantCulture))
        {
            var defaults = EmitterSettings.Default;
            var settings = new EmitterSettings(
                defaults.BestIndent,
                defaults.BestWidth,
                defaults.IsCanonical,
                defaults.MaxSimpleKeyLength,
                defaults.SkipAnchorName,
                defaults.IndentSequences,
                "\n",
                defaults.UseUtf16SurrogatePairs);
            stream.Save(new Emitter(writer, settings), assignAnchors: false);
            var yaml = writer.ToString();
            const string documentEnd = "...\n";
            if (yaml.EndsWith(documentEnd, StringComparison.Ordinal))
            {
                yaml = yaml[..^documentEnd.Length];
            }

            if (trailingNewline)
            {
                return yaml.EndsWith('\n') ? yaml : yaml + "\n";
            }

            return yaml.EndsWith("\r\n", StringComparison.Ordinal) ? yaml[..^2]
                : yaml.EndsWith('\n') ? yaml[..^1]
                : yaml;
        }
    }

    private Dictionary<string, object?> BuildGame()
    {
        var game = Mapping();
        Add(game, "id", Id);
        Add(game, "title", Title);
        if (Version is not null)
        {
            Add(game, "version", Version);
        }
        Add(game, "start", Start);
        if (Randomness is not null)
        {
            Add(game, "randomness", Randomness);
        }
        if (Seed is not null)
        {
            Add(game, "seed", Seed.Value);
        }
        if (Undo is not null)
        {
            Add(game, "undo", Undo.Value);
        }
        if (Autosave is not null)
        {
            Add(game, "autosave", Autosave.Value);
        }
        if (CharacterSheet is not null)
        {
            Add(game, "character-sheet", CharacterSheet.Value);
        }
        else if (CharacterSheetSections is not null)
        {
            var sheet = Mapping();
            foreach (var (section, shown) in CharacterSheetSections)
            {
                Add(sheet, section, shown);
            }
            Add(game, "character-sheet", sheet);
        }

        if (CreationName is not null || _creationPronouns is not null || _creationStats.Count > 0 || _creationPresets.Count > 0)
        {
            var creation = Mapping();
            if (CreationName is not null)
            {
                Add(creation, "name", CreationName.Value);
            }
            if (_creationPronouns is not null)
            {
                Add(creation, "pronouns", _creationPronouns);
            }
            if (_creationStats.Count > 0)
            {
                var stats = Mapping();
                foreach (var (id, values) in _creationStats)
                {
                    Add(stats, id, values);
                }
                Add(creation, "stats", stats);
            }
            if (_creationPresets.Count > 0)
            {
                var presets = Mapping();
                foreach (var (id, label, values) in _creationPresets)
                {
                    var preset = Mapping();
                    Add(preset, "label", label);
                    if (values.Length > 0)
                    {
                        Add(preset, "stats", BuildValues(values));
                    }
                    Add(presets, id, preset);
                }
                Add(creation, "presets", presets);
            }
            Add(game, "creation", creation);
        }

        if (BaseRuleset is not null)
        {
            var ruleset = Mapping();
            Add(ruleset, "base", BaseRuleset);
            Add(game, "ruleset", ruleset);
        }

        return game;
    }

    private object BuildSection(Section section) => section switch
    {
        Section.Flags => _flags.Select(flag => flag.Id).ToArray(),
        Section.Variables => BuildVariables(),
        Section.Items => BuildItems(),
        Section.Characters => BuildCharacters(),
        Section.Factions => BuildFactions(),
        Section.Triggers => BuildTriggers(),
        _ => throw new InvalidOperationException($"Unknown manifest section '{section}'."),
    };

    private Dictionary<string, object?> BuildVariables()
    {
        var variables = Mapping();
        foreach (var (reference, value) in _variables)
        {
            Add(variables, reference.Id, value);
        }

        return variables;
    }

    private Dictionary<string, object?> BuildItems()
    {
        var items = Mapping();
        foreach (var (reference, name, modifiers, nameAgreement, uncountable) in _items)
        {
            var item = Mapping();
            Add(item, "name", name);
            if (modifiers.Length > 0)
            {
                Add(item, "modifiers", BuildValues(modifiers));
            }
            if (nameAgreement is not null)
            {
                Add(item, "name-agreement", nameAgreement);
            }
            if (uncountable is not null)
            {
                Add(item, "uncountable", uncountable.Value);
            }
            Add(items, reference.Id, item);
        }

        return items;
    }

    private Dictionary<string, object?> BuildCharacters()
    {
        var characters = Mapping();
        foreach (var (reference, firstName, lastName, pronouns, stats, inventory) in _characters)
        {
            var character = Mapping();
            Add(character, "first-name", firstName);
            if (lastName is not null)
            {
                Add(character, "last-name", lastName);
            }
            if (pronouns is not null)
            {
                Add(character, "pronouns", pronouns);
            }
            if (stats.Length > 0)
            {
                Add(character, "stats", BuildValues(stats));
            }
            if (inventory.Length > 0)
            {
                Add(character, "inventory", BuildValues(inventory));
            }
            Add(characters, reference.Id, character);
        }

        return characters;
    }

    private Dictionary<string, object?> BuildFactions()
    {
        var factions = Mapping();
        foreach (var (reference, name, standing) in _factions)
        {
            var faction = Mapping();
            Add(faction, "name", name);
            Add(faction, "standing", standing);
            Add(factions, reference.Id, faction);
        }

        return factions;
    }

    private List<Dictionary<string, object?>> BuildTriggers()
    {
        var triggers = new List<Dictionary<string, object?>>(_triggers.Count);
        foreach (var (id, when, effect, once, save) in _triggers)
        {
            var trigger = Mapping();
            Add(trigger, "id", id);
            Add(trigger, "when", when);
            Add(trigger, "do", effect);
            if (once)
            {
                Add(trigger, "once", true);
            }
            if (save)
            {
                Add(trigger, "save", true);
            }
            triggers.Add(trigger);
        }

        return triggers;
    }

    private static Dictionary<string, object?> BuildValues(IEnumerable<KeyValuePair<string, int>> values)
    {
        var mapping = Mapping();
        foreach (var (key, value) in values)
        {
            Add(mapping, key, value);
        }

        return mapping;
    }

    private static Dictionary<string, object?> Mapping() => new(StringComparer.Ordinal);

    private static void Add(Dictionary<string, object?> mapping, string key, object? value) =>
        mapping.Add(key, value);

    private static YamlNode ToYamlNode(object? value) => value switch
    {
        Dictionary<string, object?> mapping => ToYamlMapping(mapping),
        string text => new YamlScalarNode(text),
        int number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain },
        ulong number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain },
        bool boolean => new YamlScalarNode(boolean ? "true" : "false") { Style = ScalarStyle.Plain },
        System.Collections.IEnumerable sequence => new YamlSequenceNode(sequence.Cast<object?>().Select(ToYamlNode)),
        _ => throw new InvalidOperationException($"Unsupported manifest value type '{value?.GetType().FullName ?? "null"}'."),
    };

    private static YamlMappingNode ToYamlMapping(Dictionary<string, object?> mapping)
    {
        var node = new YamlMappingNode();
        foreach (var (key, value) in mapping)
        {
            node.Add(key, ToYamlNode(value));
        }

        return node;
    }

    private static string SectionKey(Section section) => section switch
    {
        Section.Flags => "flags",
        Section.Variables => "vars",
        Section.Items => "items",
        Section.Characters => "characters",
        Section.Factions => "factions",
        Section.Triggers => "triggers",
        _ => throw new InvalidOperationException($"Unknown manifest section '{section}'."),
    };

    private TaleVariableRef Variable(string id, object value)
    {
        var reference = new TaleVariableRef(id);
        if (_variables.Any(variable => variable.Reference.Id == id))
        {
            throw new ArgumentException($"Variable '{id}' is already declared.", nameof(id));
        }

        _variables.Add((reference, value));
        IncludeSection(Section.Variables);
        return reference;
    }

    private void IncludeSection(Section section)
    {
        if (!_sections.Contains(section))
        {
            _sections.Add(section);
        }
    }

}
