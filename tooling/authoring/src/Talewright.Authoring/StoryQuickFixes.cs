using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring;

public enum StoryQuickFixKind { TextEdit, CreateFile }

public sealed record StoryQuickFix(string Title, StoryQuickFixKind Kind, string File, SourceRange Range, string NewText);
public sealed record StoryQuickFixResult(
    IReadOnlyList<StoryQuickFix> Fixes,
    string? DisabledReason = null,
    string? Title = null);

/// <summary>Creates source-aware quick fixes for common Talemark compiler diagnostics.</summary>
public static class StoryQuickFixes
{
    public static StoryQuickFixResult ForMissingFlag(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != CompilerDiagnosticCodes.UnsetFlagOrUnassignedVariable ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return new([]);

        var flag = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(site => site.Range.Start == diagnostic.Location &&
                site.Kinds.Contains(AuthoringSymbolKind.Flag) &&
                IdentifierRules.IsValid(site.Text))?.Text;
        if (flag is null || analysis.Symbols.Any(symbol =>
                symbol.Kind == AuthoringSymbolKind.Flag && symbol.Name == flag))
            return new([]);

        var title = $"Declare flag '{flag}'";
        if (!analysis.Source.Files.TryGetValue(StorySource.ManifestFileName, out var manifest))
            return new([], $"Cannot declare '{flag}' because the root story manifest is unavailable.", title);

        var edit = ManifestListEditor.Append(manifest, StorySource.ManifestFileName, ManifestVocabulary.Flags, flag);
        if (edit.Edit is not { } textEdit)
            return new([], edit.Reason ?? $"Cannot safely declare '{flag}' in the root story manifest.", title);

        return new([
            new(title, StoryQuickFixKind.TextEdit, StorySource.ManifestFileName,
                textEdit.Range, textEdit.NewText)
        ], Title: title);
    }

    public static StoryQuickFixResult ForMissingVariable(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != CompilerDiagnosticCodes.UnsetFlagOrUnassignedVariable ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return new([]);

        var variable = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(site => site.Range.Start == diagnostic.Location &&
                site.Kinds.Contains(AuthoringSymbolKind.Variable) &&
                IdentifierRules.IsValid(site.Text))?.Text;
        if (variable is null || analysis.Symbols.Any(symbol =>
                symbol.Kind == AuthoringSymbolKind.Variable && symbol.Name == variable))
            return new([]);

        var title = $"Declare variable '{variable}'";
        if (!analysis.Source.Files.TryGetValue(StorySource.ManifestFileName, out var manifest))
            return new([], $"Cannot declare '{variable}' because the root story manifest is unavailable.", title);

        VariableUsageType[] types = VariableUsageInference.Infer(analysis, diagnostic.Location, variable) switch
        {
            VariableUsageType.Number => [VariableUsageType.Number],
            VariableUsageType.Text => [VariableUsageType.Text],
            _ => [VariableUsageType.Number, VariableUsageType.Text]
        };
        var fixes = new List<StoryQuickFix>();
        foreach (var type in types)
        {
            var value = type == VariableUsageType.Number ? NumberInitialValue : TextInitialValue;
            var edit = ManifestListEditor.AppendMappingEntry(
                manifest, StorySource.ManifestFileName, ManifestVocabulary.Vars, variable, value, value);
            if (edit.Edit is not { } textEdit)
                return new([], edit.Reason ?? $"Cannot safely declare '{variable}' in the root story manifest.", title);
            var description = type == VariableUsageType.Number ? "a number" : ManifestVocabulary.SymbolText;
            fixes.Add(new($"{title} as {description}", StoryQuickFixKind.TextEdit, StorySource.ManifestFileName,
                textEdit.Range, textEdit.NewText));
        }

        return new(fixes, Title: title);
    }

    private const string NumberInitialValue = "0";
    private const string TextInitialValue = "\"\"";

    public static StoryQuickFixResult ForMissingManifestName(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != CompilerDiagnosticCodes.UnknownNameOrProperty ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return new([]);

        var site = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(candidate => candidate.Range.Start == diagnostic.Location &&
                candidate.Kinds.Count == 1 &&
                (candidate.Kinds[0] is AuthoringSymbolKind.Character or AuthoringSymbolKind.Item or AuthoringSymbolKind.Faction) &&
                IdentifierRules.IsValid(candidate.Text));
        if (site is null) return new([]);

        var kind = site.Kinds[0];
        if ((kind == AuthoringSymbolKind.Character && site.Text == EntityId.Player.Value) ||
            analysis.Symbols.Any(symbol => symbol.Kind == kind && symbol.Name == site.Text))
            return new([]);

        var (section, blockValue, flowValue) = kind switch
        {
            AuthoringSymbolKind.Character =>
                (ManifestVocabulary.Characters,
                    $"\n{ManifestVocabulary.FirstName}: {ManifestListEditor.FormatScalar(Humanize(site.Text))}",
                    $"{{{ManifestVocabulary.FirstName}: {ManifestListEditor.FormatScalar(Humanize(site.Text))}}}"),
            AuthoringSymbolKind.Item =>
                (ManifestVocabulary.Items, ManifestListEditor.FormatScalar(Humanize(site.Text)),
                    ManifestListEditor.FormatScalar(Humanize(site.Text))),
            AuthoringSymbolKind.Faction =>
                (ManifestVocabulary.Factions, "{}", "{}"),
            _ => throw new InvalidOperationException($"Unsupported manifest name kind '{kind}'.")
        };
        var name = kind switch
        {
            AuthoringSymbolKind.Character => ManifestVocabulary.SymbolCharacter,
            AuthoringSymbolKind.Item => SyntaxSpelling(AuthoringTokenKind.Item),
            AuthoringSymbolKind.Faction => SyntaxSpelling(AuthoringTokenKind.Faction),
            _ => throw new InvalidOperationException($"Unsupported manifest name kind '{kind}'.")
        };
        var title = $"Declare {name} '{site.Text}'";
        if (!analysis.Source.Files.TryGetValue(StorySource.ManifestFileName, out var manifest))
            return new([], $"Cannot declare '{site.Text}' because the root story manifest is unavailable.", title);

        var edit = ManifestListEditor.AppendMappingEntry(
            manifest, StorySource.ManifestFileName, section, site.Text, blockValue, flowValue);
        if (edit.Edit is not { } textEdit)
            return new([], edit.Reason ?? $"Cannot safely declare '{site.Text}' in the root story manifest.", title);

        return new([
            new(title, StoryQuickFixKind.TextEdit, StorySource.ManifestFileName,
                textEdit.Range, textEdit.NewText)
        ], Title: title);
    }

    public static StoryQuickFixResult ForMissingStat(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != CompilerDiagnosticCodes.UnknownNameOrProperty ||
            !diagnostic.Message.StartsWith("Unknown stat '", StringComparison.Ordinal) ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return new([]);

        var stat = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(site => site.Range.Start == diagnostic.Location &&
                site.Kinds.Contains(AuthoringSymbolKind.Stat) &&
                IdentifierRules.IsValid(site.Text))?.Text;
        if (stat is null || analysis.Symbols.Any(symbol =>
                symbol.Kind == AuthoringSymbolKind.Stat && symbol.Name == stat))
            return new([]);

        var title = $"Declare stat '{stat}'";
        if (!analysis.Source.Files.TryGetValue(StorySource.ManifestFileName, out var manifest))
            return new([], $"Cannot declare '{stat}' because the root story manifest is unavailable.", title);
        var edit = ManifestListEditor.AppendStoryStat(manifest, StorySource.ManifestFileName, stat, Humanize(stat));
        if (edit.SuppressAction)
            return new([]);
        if (edit.Edit is not { } textEdit)
            return new([], edit.Reason ?? $"Cannot safely declare '{stat}' in the root story manifest.", title);

        return new([
            new(title, StoryQuickFixKind.TextEdit, StorySource.ManifestFileName,
                textEdit.Range, textEdit.NewText)
        ], Title: title);
    }

    public static IReadOnlyList<StoryQuickFix> ForMissingScene(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != CompilerDiagnosticCodes.UnknownScene ||
            !analysis.Source.Files.TryGetValue(diagnostic.Location.File, out var sourceText) ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return [];

        var sceneId = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(site => site.Range.Start == diagnostic.Location &&
                site.Kinds.Contains(AuthoringSymbolKind.Scene) &&
                IdentifierRules.IsValid(site.Text))?.Text;
        if (sceneId is null || sceneId == SceneVocabulary.End ||
            analysis.Symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Scene && symbol.Name == sceneId))
            return [];

        var hasFinalNewline = sourceText.EndsWith('\n') || sourceText.EndsWith('\r');
        var newline = DetectNewline(sourceText);
        var separator = sourceText.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
        var insertion = $"{separator}{SceneVocabulary.SceneHeader} {sceneId}{(hasFinalNewline ? newline : "")}";
        var end = EndOfDocument(diagnostic.Location.File, sourceText);
        var fixes = new List<StoryQuickFix>
        {
            new($"Create scene '{sceneId}' in this file", StoryQuickFixKind.TextEdit, diagnostic.Location.File,
                new(end, end), insertion)
        };

        var directory = Path.GetDirectoryName(diagnostic.Location.File.Replace('/', Path.DirectorySeparatorChar));
        var newFile = string.IsNullOrEmpty(directory)
            ? $"{sceneId}.tale"
            : $"{directory.Replace('\\', '/')}/{sceneId}.tale";
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (analysis.Source.Files.Keys.Any(file => comparer.Equals(file, newFile))) return fixes;

        var beginning = new SourceLocation(newFile, 1, 1);
        fixes.Add(new($"Create scene '{sceneId}' in a new file", StoryQuickFixKind.CreateFile, newFile,
            new(beginning, beginning), $"{SceneVocabulary.SceneHeader} {sceneId}{(hasFinalNewline ? newline : "")}"));
        return fixes;
    }

    private static SourceLocation EndOfDocument(string file, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return new(file, lines.Length, lines[^1].Length + 1);
    }

    private static string DetectNewline(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            if (text[index] == '\n') return "\n";
        }

        return "\n";
    }

    private static string Humanize(string id) =>
        string.Join(' ', id.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[..1].ToUpperInvariant() + part[1..]));

    private static string SyntaxSpelling(AuthoringTokenKind kind) =>
        AuthoringSyntaxVocabulary.TryGetHelp(kind, out var help)
            ? help.Spelling
            : throw new InvalidOperationException($"No Talemark syntax label is registered for '{kind}'.");
}
