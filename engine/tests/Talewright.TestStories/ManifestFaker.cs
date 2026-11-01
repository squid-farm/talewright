using System.Globalization;
using System.Text.Json;

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

    private static readonly HashSet<string> YamlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null",
    };

    private readonly List<TaleFlagRef> _flags = [];
    private readonly List<(TaleVariableRef Reference, string Value, bool IsText)> _variables = [];
    private readonly List<(TaleItemRef Reference, string Name, KeyValuePair<string, int>[] Modifiers, string? NameAgreement, bool? Uncountable)> _items = [];
    private readonly List<(TaleCharacterRef Reference, string FirstName, string? LastName, string? Pronouns, KeyValuePair<string, int>[] Stats, KeyValuePair<string, int>[] Inventory)> _characters = [];
    private readonly List<(TaleFactionRef Reference, string Name, int Standing)> _factions = [];
    private readonly List<(string Id, string When, string Do, bool Once)> _triggers = [];
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

    public TaleVariableRef Number(string id, int value) => Variable(id, value.ToString(CultureInfo.InvariantCulture), false);

    public TaleVariableRef Text(string id, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Variable(id, value, true);
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

    public void Trigger(string id, string when, string effect, bool once = false)
    {
        FakerIdentifier.Require(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(when);
        ArgumentException.ThrowIfNullOrWhiteSpace(effect);
        if (_triggers.Any(trigger => trigger.Id == id))
        {
            throw new ArgumentException($"Trigger '{id}' is already declared.", nameof(id));
        }

        _triggers.Add((id, when, effect, once));
        IncludeSection(Section.Triggers);
    }

    public string Build(bool trailingNewline = false)
    {
        List<string> lines =
        [
            "game:",
            $"  id: {Scalar(Id)}",
            $"  title: {Scalar(Title)}",
        ];
        if (Version is not null)
        {
            lines.Add($"  version: {Scalar(Version)}");
        }

        lines.Add($"  start: {Scalar(Start)}");
        if (Randomness is not null)
        {
            lines.Add($"  randomness: {Scalar(Randomness)}");
        }

        if (Seed is not null)
        {
            lines.Add($"  seed: {Seed.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (Undo is not null)
        {
            lines.Add($"  undo: {Undo.Value.ToString().ToLowerInvariant()}");
        }

        if (CreationName is not null || _creationPronouns is not null || _creationStats.Count > 0 || _creationPresets.Count > 0)
        {
            lines.Add("  creation:");
            if (CreationName is not null)
            {
                lines.Add($"    name: {CreationName.Value.ToString().ToLowerInvariant()}");
            }

            if (_creationPronouns is not null)
            {
                lines.Add($"    pronouns: [{string.Join(", ", _creationPronouns.Select(Scalar))}]");
            }

            if (_creationStats.Count > 0)
            {
                lines.Add("    stats:");
                foreach (var (id, values) in _creationStats)
                {
                    lines.Add($"      {Scalar(id)}: [{string.Join(", ", values.Select(value => value.ToString(CultureInfo.InvariantCulture)))}]");
                }
            }

            if (_creationPresets.Count > 0)
            {
                lines.Add("    presets:");
                foreach (var (id, label, stats) in _creationPresets)
                {
                    lines.Add($"      {Scalar(id)}:");
                    lines.Add($"        label: {Scalar(label)}");
                    if (stats.Length > 0)
                    {
                        lines.Add($"        stats: {{ {string.Join(", ", stats.Select(stat =>
                            $"{Scalar(stat.Key)}: {stat.Value.ToString(CultureInfo.InvariantCulture)}"))} }}");
                    }
                }
            }
        }

        if (BaseRuleset is not null)
        {
            lines.Add("  ruleset:");
            lines.Add($"    base: {Scalar(BaseRuleset)}");
        }

        foreach (var section in _sections)
        {
            switch (section)
            {
                case Section.Flags:
                    lines.Add($"flags: [{string.Join(", ", _flags.Select(flag => Scalar(flag.Id)))}]");
                    break;

                case Section.Variables:
                    lines.Add($"vars: {{{string.Join(", ", _variables.Select(variable =>
                        $"{Scalar(variable.Reference.Id)}: {(variable.IsText ? Quoted(variable.Value) : variable.Value)}"))}}}");
                    break;

                case Section.Items:
                    lines.Add("items:");
                    foreach (var (reference, name, modifiers, nameAgreement, uncountable) in _items)
                    {
                        var modifierText = modifiers.Length == 0
                            ? ""
                            : $", modifiers: {{ {string.Join(", ", modifiers.Select(modifier =>
                                $"{Scalar(modifier.Key)}: {modifier.Value.ToString(CultureInfo.InvariantCulture)}"))} }}";
                        var agreementText = nameAgreement is null ? "" : $", name-agreement: {Scalar(nameAgreement)}";
                        var uncountableText = uncountable is null ? "" : $", uncountable: {uncountable.Value.ToString().ToLowerInvariant()}";
                        lines.Add($"  {Scalar(reference.Id)}: {{ name: {Scalar(name)}{modifierText}{agreementText}{uncountableText} }}");
                    }
                    break;

                case Section.Characters:
                    lines.Add("characters:");
                    foreach (var (reference, firstName, lastName, pronouns, stats, inventory) in _characters)
                    {
                        if (lastName is null && pronouns is null && stats.Length == 0 && inventory.Length == 0)
                        {
                            lines.Add($"  {Scalar(reference.Id)}: {{first-name: {Scalar(firstName)}}}");
                            continue;
                        }

                        lines.Add($"  {Scalar(reference.Id)}:");
                        lines.Add($"    first-name: {Scalar(firstName)}");
                        if (lastName is not null)
                        {
                            lines.Add($"    last-name: {Scalar(lastName)}");
                        }

                        if (pronouns is not null)
                        {
                            lines.Add($"    pronouns: {Scalar(pronouns)}");
                        }

                        if (stats.Length > 0)
                        {
                            lines.Add($"    stats: {{ {string.Join(", ", stats.Select(stat =>
                                $"{Scalar(stat.Key)}: {stat.Value.ToString(CultureInfo.InvariantCulture)}"))} }}");
                        }

                        if (inventory.Length > 0)
                        {
                            lines.Add($"    inventory: {{ {string.Join(", ", inventory.Select(item =>
                                $"{Scalar(item.Key)}: {item.Value.ToString(CultureInfo.InvariantCulture)}"))} }}");
                        }
                    }
                    break;

                case Section.Factions:
                    lines.Add("factions:");
                    foreach (var (reference, name, standing) in _factions)
                    {
                        lines.Add($"  {Scalar(reference.Id)}: {{ name: {Scalar(name)}, standing: {standing.ToString(CultureInfo.InvariantCulture)} }}");
                    }
                    break;

                case Section.Triggers:
                    lines.Add("triggers:");
                    foreach (var (id, when, effect, once) in _triggers)
                    {
                        lines.Add($"  - id: {Scalar(id)}");
                        lines.Add($"    when: {Scalar(when)}");
                        lines.Add($"    do: {Scalar(effect)}");
                        if (once)
                        {
                            lines.Add("    once: true");
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown manifest section '{section}'.");
            }
        }

        return string.Join('\n', lines) + (trailingNewline ? "\n" : "");
    }

    private TaleVariableRef Variable(string id, string value, bool isText)
    {
        var reference = new TaleVariableRef(id);
        if (_variables.Any(variable => variable.Reference.Id == id))
        {
            throw new ArgumentException($"Variable '{id}' is already declared.", nameof(id));
        }

        _variables.Add((reference, value, isText));
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

    private static string Scalar(string text)
    {
        if (text.Length > 0 &&
            char.IsAsciiLetterOrDigit(text[0]) &&
            text == text.Trim() &&
            text.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.') &&
            !YamlKeywords.Contains(text))
        {
            return text;
        }

        return Quoted(text);
    }

    private static string Quoted(string text) => JsonSerializer.Serialize(text);
}
