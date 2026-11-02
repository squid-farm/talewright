using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

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
        if (diagnostic.Code != "TW2004" ||
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

        var edit = ManifestListEditor.Append(manifest, StorySource.ManifestFileName, "flags", flag);
        if (edit.Edit is not { } textEdit)
            return new([], edit.Reason ?? $"Cannot safely declare '{flag}' in the root story manifest.", title);

        return new([
            new(title, StoryQuickFixKind.TextEdit, StorySource.ManifestFileName,
                textEdit.Range, textEdit.NewText)
        ], Title: title);
    }

    public static IReadOnlyList<StoryQuickFix> ForMissingScene(StoryAnalysis analysis, Diagnostic diagnostic)
    {
        if (diagnostic.Code != "TW2002" ||
            !analysis.Source.Files.TryGetValue(diagnostic.Location.File, out var sourceText) ||
            !diagnostic.Location.File.EndsWith(".tale", StringComparison.OrdinalIgnoreCase))
            return [];

        var sceneId = analysis.CompletionSitesAt(diagnostic.Location)
            .FirstOrDefault(site => site.Range.Start == diagnostic.Location &&
                site.Kinds.Contains(AuthoringSymbolKind.Scene) &&
                IdentifierRules.IsValid(site.Text))?.Text;
        if (sceneId is null || sceneId == "END" ||
            analysis.Symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Scene && symbol.Name == sceneId))
            return [];

        var hasFinalNewline = sourceText.EndsWith('\n') || sourceText.EndsWith('\r');
        var newline = DetectNewline(sourceText);
        var separator = sourceText.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
        var insertion = $"{separator}=== {sceneId}{(hasFinalNewline ? newline : "")}";
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
            new(beginning, beginning), $"=== {sceneId}{(hasFinalNewline ? newline : "")}"));
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
}
