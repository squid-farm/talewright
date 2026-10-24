using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring;

public enum LintSeverity
{
    Off,
    Warning,
    Error,
}

public sealed record StoryLintRule(string Id, string Description, LintSeverity DefaultSeverity);

public static class StoryLintRules
{
    public const string SceneIdStyle = "TWL001";
    public const string StoryIdStyle = "TWL002";

    public static IReadOnlyList<StoryLintRule> All { get; } = Array.AsReadOnly(new[]
    {
        new StoryLintRule(SceneIdStyle, "Scene IDs use lowercase kebab-case segments, optionally grouped with dots.", LintSeverity.Warning),
        new StoryLintRule(StoryIdStyle, "The story ID uses lowercase kebab-case.", LintSeverity.Warning),
    });

    internal static bool Contains(string id) => All.Any(rule => rule.Id == id);
}

public sealed record StoryLintDiagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceRange Range)
{
    public override string ToString() =>
        $"{Range.Start}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

public sealed record StoryLintResult(
    IReadOnlyList<Diagnostic> CompilerDiagnostics,
    IReadOnlyList<StoryLintDiagnostic> RuleDiagnostics);

public sealed class LintConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LintConfiguration
{
    public const string FileName = "talewright.lint.json";

    private static readonly Regex SceneIdPattern = new(
        @"\A[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StoryIdPattern = new(
        @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, LintSeverity> _ruleSeverities;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _suppressions;

    private LintConfiguration(
        IDictionary<string, LintSeverity> ruleSeverities,
        IDictionary<string, IReadOnlySet<string>> suppressions)
    {
        _ruleSeverities = new ReadOnlyDictionary<string, LintSeverity>(
            new Dictionary<string, LintSeverity>(ruleSeverities, StringComparer.Ordinal));
        _suppressions = new ReadOnlyDictionary<string, IReadOnlySet<string>>(
            new Dictionary<string, IReadOnlySet<string>>(suppressions, StringComparer.Ordinal));
    }

    public static LintConfiguration Defaults { get; } = new(
        new Dictionary<string, LintSeverity>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));

    public static LintConfiguration LoadFromDirectory(string storyRoot)
    {
        var path = Path.Combine(storyRoot, FileName);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return Defaults;
        }
        catch (DirectoryNotFoundException)
        {
            return Defaults;
        }

        try
        {
            return Parse(json);
        }
        catch (LintConfigurationException ex)
        {
            throw new LintConfigurationException($"{path}: {ex.Message}", ex);
        }
    }

    public static LintConfiguration Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LintConfigurationException($"Invalid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LintConfigurationException("The configuration must be a JSON object.");
            }

            var ruleSeverities = new Dictionary<string, LintSeverity>(StringComparer.Ordinal);
            var suppressions = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    throw new LintConfigurationException($"Duplicate configuration property '{property.Name}'.");
                }

                switch (property.Name)
                {
                    case "rules":
                        ParseRules(property.Value, ruleSeverities);
                        break;
                    case "suppressions":
                        ParseSuppressions(property.Value, suppressions);
                        break;
                    default:
                        throw new LintConfigurationException($"Unknown configuration property '{property.Name}'.");
                }
            }

            return new LintConfiguration(ruleSeverities, suppressions);
        }
    }

    public LintConfiguration WithRuleOverrides(IEnumerable<string> overrides)
    {
        var severities = new Dictionary<string, LintSeverity>(_ruleSeverities, StringComparer.Ordinal);
        foreach (var value in overrides)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('=', separator + 1) >= 0)
            {
                throw new LintConfigurationException($"Expected --rule ID=off|warning|error, not '{value}'.");
            }

            var id = value[..separator];
            EnsureKnownRule(id);
            severities[id] = ParseSeverity(value[(separator + 1)..], $"--rule {id}");
        }

        return new LintConfiguration(severities,
            new Dictionary<string, IReadOnlySet<string>>(_suppressions, StringComparer.Ordinal));
    }

    public LintSeverity SeverityFor(string ruleId)
    {
        if (_ruleSeverities.TryGetValue(ruleId, out var severity))
        {
            return severity;
        }

        return StoryLintRules.All.First(rule => rule.Id == ruleId).DefaultSeverity;
    }

    public bool IsSuppressed(string file, string ruleId) =>
        _suppressions.TryGetValue(file.Replace('\\', '/'), out var rules) && rules.Contains(ruleId);

    private static void ParseRules(JsonElement value, IDictionary<string, LintSeverity> severities)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new LintConfigurationException("'rules' must be an object mapping rule IDs to severities.");
        }

        foreach (var property in value.EnumerateObject())
        {
            EnsureKnownRule(property.Name);
            if (!severities.TryAdd(property.Name,
                    ParseSeverity(property.Value, $"rules.{property.Name}")))
            {
                throw new LintConfigurationException($"Duplicate rule setting '{property.Name}'.");
            }
        }
    }

    private static void ParseSuppressions(JsonElement value, IDictionary<string, IReadOnlySet<string>> suppressions)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new LintConfigurationException("'suppressions' must map relative file paths to rule-ID arrays.");
        }

        foreach (var property in value.EnumerateObject())
        {
            var file = ValidateRelativeFilePath(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new LintConfigurationException($"suppressions.{file} must be an array of rule IDs.");
            }

            var rules = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in property.Value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } ruleId)
                {
                    throw new LintConfigurationException($"Every suppression for '{file}' must be a rule ID string.");
                }

                EnsureKnownRule(ruleId);
                if (!rules.Add(ruleId))
                {
                    throw new LintConfigurationException($"Duplicate suppression '{ruleId}' for '{file}'.");
                }
            }

            if (!suppressions.TryAdd(file, rules))
            {
                throw new LintConfigurationException($"Duplicate suppression file '{file}'.");
            }
        }
    }

    private static string ValidateRelativeFilePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\\') || file.Contains(':') ||
            Path.IsPathRooted(file) || file.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new LintConfigurationException(
                $"Suppression path '{file}' must be a normalized relative path using '/' separators.");
        }

        return file;
    }

    private static LintSeverity ParseSeverity(JsonElement value, string setting)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } severity)
        {
            throw new LintConfigurationException($"'{setting}' must be 'off', 'warning', or 'error'.");
        }

        return ParseSeverity(severity, setting);
    }

    private static LintSeverity ParseSeverity(string severity, string setting) =>
        severity switch
        {
            "off" => LintSeverity.Off,
            "warning" => LintSeverity.Warning,
            "error" => LintSeverity.Error,
            _ => throw new LintConfigurationException($"'{setting}' must be 'off', 'warning', or 'error'."),
        };

    private static void EnsureKnownRule(string id)
    {
        if (!StoryLintRules.Contains(id))
        {
            throw new LintConfigurationException($"Unknown lint rule '{id}'.");
        }
    }

    internal static bool IsSceneIdStyle(string id) => SceneIdPattern.IsMatch(id);
    internal static bool IsStoryIdStyle(string id) => StoryIdPattern.IsMatch(id);
}

public static class StoryLintAnalyzer
{
    public static StoryLintResult Analyze(StoryAnalysis analysis, LintConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        configuration ??= LintConfiguration.Defaults;
        var findings = new List<StoryLintDiagnostic>();
        var invalidSceneIdLocations = analysis.Diagnostics
            .Where(diagnostic => diagnostic.Code == "TW1008")
            .Select(diagnostic => (diagnostic.Location.File, diagnostic.Location.Line))
            .ToHashSet();

        foreach (var symbol in analysis.Symbols)
        {
            var (ruleId, expectedStyle) = symbol.Kind switch
            {
                AuthoringSymbolKind.Scene => (StoryLintRules.SceneIdStyle,
                    LintConfiguration.IsSceneIdStyle(symbol.Name)),
                AuthoringSymbolKind.Story => (StoryLintRules.StoryIdStyle,
                    LintConfiguration.IsStoryIdStyle(symbol.Name)),
                _ => (string.Empty, true),
            };
            if (ruleId.Length == 0 || expectedStyle || symbol.Declaration is not { } range)
            {
                continue;
            }

            if (ruleId == StoryLintRules.SceneIdStyle &&
                invalidSceneIdLocations.Contains((range.Start.File, range.Start.Line)))
            {
                continue;
            }

            var severity = configuration.SeverityFor(ruleId);
            if (severity == LintSeverity.Off || configuration.IsSuppressed(range.Start.File, ruleId))
            {
                continue;
            }

            var diagnosticSeverity = severity == LintSeverity.Error
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;
            var message = ruleId == StoryLintRules.SceneIdStyle
                ? $"Scene ID '{symbol.Name}' should use lowercase kebab-case segments separated by dots."
                : $"Story ID '{symbol.Name}' should use lowercase kebab-case.";
            findings.Add(new StoryLintDiagnostic(ruleId, diagnosticSeverity, message, range));
        }

        return new StoryLintResult(analysis.Diagnostics, Array.AsReadOnly(findings
            .OrderBy(diagnostic => diagnostic.Range.Start.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Range.Start.Line)
            .ThenBy(diagnostic => diagnostic.Range.Start.Column)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray()));
    }
}
