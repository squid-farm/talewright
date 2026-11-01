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
    }

    private static readonly HashSet<string> YamlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null",
    };

    private readonly List<TaleFlagRef> _flags = [];
    private readonly List<(TaleVariableRef Reference, string Value, bool IsText)> _variables = [];
    private readonly List<(TaleItemRef Reference, string Name)> _items = [];
    private readonly List<(TaleCharacterRef Reference, string FirstName, string? LastName, KeyValuePair<string, int>[] Stats)> _characters = [];
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

    internal void IncludeItemsSection() => IncludeSection(Section.Items);

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

    public TaleItemRef Item(string id, string name)
    {
        var reference = new TaleItemRef(id);
        ArgumentNullException.ThrowIfNull(name);
        if (_items.Any(item => item.Reference.Id == id))
        {
            throw new ArgumentException($"Item '{id}' is already declared.", nameof(id));
        }

        _items.Add((reference, name));
        IncludeSection(Section.Items);
        return reference;
    }

    public TaleCharacterRef Character(
        string id,
        string firstName,
        string? lastName = null,
        IReadOnlyDictionary<string, int>? stats = null)
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

        _characters.Add((reference, firstName, lastName, stats?.ToArray() ?? []));
        IncludeSection(Section.Characters);
        return reference;
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
        if (Seed is not null)
        {
            lines.Add($"  seed: {Seed.Value.ToString(CultureInfo.InvariantCulture)}");
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
                    foreach (var (reference, name) in _items)
                    {
                        lines.Add($"  {Scalar(reference.Id)}: {{ name: {Scalar(name)} }}");
                    }
                    break;

                case Section.Characters:
                    lines.Add("characters:");
                    foreach (var (reference, firstName, lastName, stats) in _characters)
                    {
                        if (lastName is null && stats.Length == 0)
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

                        if (stats.Length > 0)
                        {
                            lines.Add($"    stats: {{ {string.Join(", ", stats.Select(stat =>
                                $"{Scalar(stat.Key)}: {stat.Value.ToString(CultureInfo.InvariantCulture)}"))} }}");
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
