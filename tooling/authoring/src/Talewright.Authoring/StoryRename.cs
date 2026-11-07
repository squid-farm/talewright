using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring;

/// <summary>The renameable name at a position: its identity and the source range of the occurrence there.</summary>
public sealed record StoryRenameTarget(AuthoringSymbolKind Kind, string Name, SourceRange Range);

/// <summary>
/// The outcome of checking a position for rename: a <see cref="Target"/>, a <see cref="Refusal"/> explaining why the
/// name there cannot be renamed, or neither when the position is not on a name.
/// </summary>
public sealed record StoryRenamePreparation(StoryRenameTarget? Target, string? Refusal);

/// <summary>One replacement of an occurrence's source range.</summary>
public sealed record StoryRenameEdit(SourceRange Range, string NewText);

/// <summary>
/// The edits that rename every occurrence of one identity, with a <see cref="Warning"/> the author should confirm, or
/// a <see cref="Refusal"/> and no edits.
/// </summary>
public sealed record StoryRenameResult(IReadOnlyList<StoryRenameEdit> Edits, string? Refusal, string? Warning)
{
    internal static StoryRenameResult Refused(string reason) => new([], reason, null);
}

/// <summary>
/// Renames an author-declared name across a whole story, using the occurrences <see cref="StoryReferences"/> finds.
/// A rename is refused unless the edited story would analyze with the same diagnostics and lint findings and the new
/// name would have exactly the occurrences the old one had.
/// </summary>
public static class StoryRename
{
    /// <summary>Finds the renameable name at <paramref name="position"/>. <paramref name="analysis"/> must be a whole-story analysis.</summary>
    public static StoryRenamePreparation Prepare(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is AuthoringDiagnosticCodes.CustomFeaturesUnavailable or AuthoringDiagnosticCodes.StoryAnalysisUnavailable))
            throw new InvalidOperationException("Rename is unavailable until project authoring metadata is supplied and valid.");
        if (analysis.Names.At(position) is not { } occurrence) return new(null, Unowned(analysis, position));

        var kind = occurrence.Kind;
        var name = occurrence.Name;
        if (kind == AuthoringSymbolKind.Character && name == ExpressionVocabulary.Player)
            return new(null, $"'{name}' is the fixed ID of the player character and cannot be renamed.");
        if (kind == AuthoringSymbolKind.Stat && analysis.BaseRulesetStats.Contains(name))
            return new(null, $"Stat '{name}' overrides a built-in stat of the same ID in the story's base ruleset. " +
                "The ruleset's code refers to it by that ID, so renaming it would leave the built-in stat in place " +
                "and change what every reference means.");

        var occurrences = analysis.Names.Occurrences(kind, name);
        var declarations = occurrences.Count(item => item.IsDeclaration);
        if (kind != AuthoringSymbolKind.Tag && declarations > 1)
            return new(null, $"{Describe(kind, true)} '{name}' is declared {declarations} times. Remove the duplicate " +
                "declarations before renaming it.");
        if (HidingError(analysis, name) is { } error)
            return new(null, $"Fix the error at {Format(error.Location)} before renaming '{name}': {error.Message} " +
                "The error could hide a reference to the name.");
        return new(new(kind, name, occurrence.Range), null);
    }

    /// <summary>
    /// Renames the name at <paramref name="position"/> to <paramref name="newName"/>. <paramref name="analyze"/>
    /// analyzes an edited copy of the story with the same options as <paramref name="analysis"/>, which must be a
    /// whole-story analysis of the current text.
    /// </summary>
    public static StoryRenameResult Rename(StoryAnalysis analysis, SourceLocation position, string newName,
        Func<StorySource, CancellationToken, StoryAnalysis> analyze, LintConfiguration? lintConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(analyze);
        var preparation = Prepare(analysis, position);
        if (preparation.Target is not { } target)
            return StoryRenameResult.Refused(preparation.Refusal ?? "There is no renameable name here.");
        var (kind, name, _) = target;
        if (newName == name) return new([], null, null);
        if (InvalidName(analysis, kind, name, newName) is { } invalid) return StoryRenameResult.Refused(invalid);
        if (Collision(analysis, kind, newName) is { } collision) return StoryRenameResult.Refused(collision);

        var occurrences = analysis.Names.Occurrences(kind, name);
        var edits = new List<StoryRenameEdit>();
        var files = analysis.Source.Files.ToDictionary(StringComparer.Ordinal);
        foreach (var group in occurrences.GroupBy(occurrence => occurrence.Range.Start.File, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(group.Key, out var text))
                return StoryRenameResult.Refused($"'{name}' occurs in '{group.Key}', which is not part of the story source.");
            var lines = LineStarts(text);
            var spans = new List<(int Start, int End, SourceRange Range)>();
            foreach (var range in group.Select(occurrence => occurrence.Range).Distinct())
            {
                if (Offset(lines, text, range.Start) is not { } start || Offset(lines, text, range.End) is not { } end ||
                    end < start || !MatchesName(text.AsSpan(start, end - start), name))
                    return StoryRenameResult.Refused($"The occurrence of '{name}' at {Format(range.Start)} could not be " +
                        "located in the current text.");
                spans.Add((start, end, range));
            }
            spans.Sort((left, right) => left.Start.CompareTo(right.Start));
            for (var index = 1; index < spans.Count; index++)
                if (spans[index].Start < spans[index - 1].End)
                    return StoryRenameResult.Refused($"Occurrences of '{name}' overlap at {Format(spans[index].Range.Start)}.");
            for (var index = spans.Count - 1; index >= 0; index--)
                text = string.Concat(text.AsSpan(0, spans[index].Start), newName, text.AsSpan(spans[index].End));
            files[group.Key] = text;
            edits.AddRange(spans.Select(span => new StoryRenameEdit(span.Range, newName)));
        }

        var renamed = analyze(new StorySource(files, analysis.Source.ImagePaths), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Introduced(analysis, renamed, lintConfiguration) is { } introduced)
            return StoryRenameResult.Refused($"Renaming '{name}' to '{newName}' would introduce {introduced}");
        if (renamed.Names.Occurrences(kind, newName).Count != occurrences.Count ||
            renamed.Names.Occurrences(kind, name).Count != 0)
            return StoryRenameResult.Refused($"Renaming '{name}' to '{newName}' would change which names refer to " +
                $"the {Describe(kind, false)}.");
        return new(edits, null, Warning(kind, name));
    }

    private static string Warning(AuthoringSymbolKind kind, string name) => kind == AuthoringSymbolKind.Story
        ? $"Changing the story ID '{name}' changes the story's save identity: existing saves will no longer belong " +
          "to this story, and debugger scenarios that name it may stop working. Saves are not migrated."
        : $"Existing saves and debugger scenarios that refer to '{name}' may stop working after the rename. " +
          "Saves are not migrated.";

    // Names the parser recognized in a slot but that no story declaration owns, such as built-in stats and END.
    private static string? Unowned(StoryAnalysis analysis, SourceLocation position)
    {
        foreach (var site in analysis.CompletionSitesAt(position))
        {
            if (site is not { Text.Length: > 0, Kinds.Count: 1 }) continue;
            var name = site.Text;
            return site.Kinds[0] switch
            {
                AuthoringSymbolKind.Scene when name == SceneVocabulary.End =>
                    $"'{name}' ends the story; it is not a scene and cannot be renamed.",
                AuthoringSymbolKind.Stat when analysis.BaseRulesetStats.Contains(name) =>
                    $"Stat '{name}' is built into the story's ruleset and cannot be renamed.",
                AuthoringSymbolKind.Predicate or AuthoringSymbolKind.Modifier or AuthoringSymbolKind.Ruleset =>
                    $"{Describe(site.Kinds[0], true)} '{name}' is registered in C# code and cannot be renamed here.",
                AuthoringSymbolKind.Image => null,
                var kind => $"{Describe(kind, true)} '{name}' is not declared in this story, so there is nothing to rename.",
            };
        }
        return null;
    }

    private static string? InvalidName(StoryAnalysis analysis, AuthoringSymbolKind kind, string name, string newName)
    {
        if (kind != AuthoringSymbolKind.Tag)
        {
            if (!IdentifierRules.IsValid(newName))
                return $"'{newName}' is not a valid {Describe(kind, false)} ID. IDs start with a letter or '_' and " +
                       "contain letters, digits and '_', with single '.' or '-' separators.";
            if (kind == AuthoringSymbolKind.Scene && newName == SceneVocabulary.End)
                return $"'{newName}' is reserved for ending the story.";
            if (kind == AuthoringSymbolKind.Character && newName == ExpressionVocabulary.Player)
                return $"'{newName}' is reserved for the player character.";
            return null;
        }

        if (newName.Length == 0 || newName.Trim().Length != newName.Length || newName.Contains(ExpressionVocabulary.ArgumentSeparator) ||
            newName.Any(char.IsControl))
            return $"'{newName}' is not a valid tag. Tags cannot be empty, start or end with whitespace, or contain commas.";
        if (newName is ExpressionVocabulary.And or ExpressionVocabulary.Or)
            return $"'{newName}' is reserved for combining tags in @hub selectors.";
        if (analysis.Names.Occurrences(AuthoringSymbolKind.Tag, name).Any(occurrence => !occurrence.IsDeclaration) &&
            newName.Any(character => char.IsWhiteSpace(character) || character is '(' or ')'))
            return $"'{newName}' cannot be used in the @hub selectors that refer to '{name}'. Hub tags cannot contain " +
                   "whitespace or parentheses.";
        return null;
    }

    private static string? Collision(StoryAnalysis analysis, AuthoringSymbolKind kind, string newName)
    {
        if (kind == AuthoringSymbolKind.Stat && analysis.BaseRulesetStats.Contains(newName))
            return $"'{newName}' is a built-in stat of the story's base ruleset; a story stat with that ID would " +
                   "override it.";
        return analysis.Symbols.Any(symbol => symbol.Kind == kind && symbol.Name == newName) ||
               analysis.Names.Occurrences(kind, newName).Count > 0
            ? $"{Describe(kind, true)} '{newName}' already exists."
            : null;
    }

    // An error on a line that mentions the name, or anywhere in a configuration file YAML could not read, may have
    // stopped the parser from recording a reference, so renaming could leave that reference behind.
    private static Diagnostic? HidingError(StoryAnalysis analysis, string name)
    {
        foreach (var diagnostic in analysis.Diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error || diagnostic.Location.Line < 1) continue;
            var file = string.IsNullOrEmpty(diagnostic.Location.File) ? StorySource.ManifestFileName : diagnostic.Location.File;
            if (!analysis.Source.Files.TryGetValue(file, out var text)) continue;
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            var first = diagnostic.Code == CompilerDiagnosticCodes.InvalidYaml ? 1 : diagnostic.Location.Line;
            var last = diagnostic.Code == CompilerDiagnosticCodes.InvalidYaml ? lines.Length
                : Math.Max(first, diagnostic.EndLocation?.Line ?? first);
            for (var line = first; line <= Math.Min(last, lines.Length); line++)
                if (Mentions(lines[line - 1], name)) return diagnostic;
        }
        return null;
    }

    private static bool IsWordChar(char value) => IdentifierRules.IsStart(value) || char.IsAsciiDigit(value);

    private static bool Mentions(string line, string name)
    {
        for (var index = line.IndexOf(name, StringComparison.Ordinal); index >= 0;
             index = line.IndexOf(name, index + 1, StringComparison.Ordinal))
        {
            var end = index + name.Length;
            if ((index == 0 || !IsWordChar(line[index - 1])) &&
                (end == line.Length || !IsWordChar(line[end])))
                return true;
        }
        return false;
    }

    private static string? Introduced(StoryAnalysis before, StoryAnalysis after, LintConfiguration? lintConfiguration)
    {
        var beforeLint = StoryLintAnalyzer.Analyze(before, lintConfiguration);
        var afterLint = StoryLintAnalyzer.Analyze(after, lintConfiguration);
        var existing = beforeLint.CompilerDiagnostics
            .Select(diagnostic => (diagnostic.Severity, diagnostic.Code, File(diagnostic.Location), diagnostic.Location.Line))
            .Concat(beforeLint.RuleDiagnostics.Select(diagnostic =>
                (diagnostic.Severity, diagnostic.Code, diagnostic.Range.Start.File, diagnostic.Range.Start.Line)))
            .GroupBy(key => key)
            .ToDictionary(group => group.Key, group => group.Count());
        bool IsNew((DiagnosticSeverity, string, string, int) key) =>
            !existing.TryGetValue(key, out var count) || (existing[key] = count - 1) < 0;
        foreach (var diagnostic in afterLint.CompilerDiagnostics)
            if (IsNew((diagnostic.Severity, diagnostic.Code, File(diagnostic.Location), diagnostic.Location.Line)))
                return $"{diagnostic.Code} at {Format(diagnostic.Location)}: {diagnostic.Message}";
        foreach (var diagnostic in afterLint.RuleDiagnostics)
            if (IsNew((diagnostic.Severity, diagnostic.Code, diagnostic.Range.Start.File, diagnostic.Range.Start.Line)))
                return $"lint finding {diagnostic.Code} at {Format(diagnostic.Range.Start)}: {diagnostic.Message}";
        return null;
    }

    private static string File(SourceLocation location) =>
        string.IsNullOrEmpty(location.File) ? StorySource.ManifestFileName : location.File;

    private static string Format(SourceLocation location) => $"{File(location)}:{location.Line}:{location.Column}";

    // Escaped YAML text spells the same name differently; any other mismatch means the range is not current.
    private static bool MatchesName(ReadOnlySpan<char> text, string name) =>
        text.SequenceEqual(name) || text.Contains('\\');

    // Source columns count UTF-16 code units on lines ended by CR, LF or CRLF, as editors do.
    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            starts.Add(index + 1);
        }
        return starts.ToArray();
    }

    private static int? Offset(int[] lineStarts, string text, SourceLocation location)
    {
        if (location.Line < 1 || location.Line > lineStarts.Length || location.Column < 1) return null;
        var start = lineStarts[location.Line - 1];
        var lineEnd = location.Line < lineStarts.Length ? lineStarts[location.Line] : text.Length;
        var offset = start + location.Column - 1;
        return offset <= lineEnd ? offset : null;
    }

    private static string Describe(AuthoringSymbolKind kind, bool capitalized)
    {
        var text = kind switch
        {
            AuthoringSymbolKind.Story => "story ID",
            AuthoringSymbolKind.Variable => "variable",
            _ => kind.ToString().ToLowerInvariant(),
        };
        return capitalized ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }
}
