using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring;

public sealed record AuthoringCompletion(string Label, string Detail, SourceRange Range, string NewText);

/// <summary>Projects parser-recognized reference slots onto the current story's partial symbol table.</summary>
public static class StoryCompletion
{
    public static IReadOnlyList<AuthoringCompletion> Complete(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is AuthoringDiagnosticCodes.CustomFeaturesUnavailable or AuthoringDiagnosticCodes.StoryAnalysisUnavailable))
            throw new InvalidOperationException("Semantic completion is unavailable until project authoring metadata is supplied and valid.");
        var sites = analysis.CompletionSitesAt(position);
        var result = new List<AuthoringCompletion>();
        foreach (var site in sites)
        {
            var length = site.Positions.TakeWhile(point => Compare(point, position) <= 0).Count() - 1;
            var prefix = site.Text[..Math.Clamp(length, 0, site.Text.Length)];
            var symbols = (site.Candidates ?? analysis.Symbols).Where(symbol => site.Kinds.Contains(symbol.Kind))
                .Select(symbol => (Name: symbol.Name, Detail: symbol.Kind == AuthoringSymbolKind.Modifier &&
                    AuthoringSyntaxVocabulary.TryGetKeywordHelp(symbol.Name, out var help)
                        ? help.Description
                        : symbol.Kind == AuthoringSymbolKind.Variable && analysis.ItemVariables.Contains(symbol.Name)
                            ? $"{symbol.Kind}: {ManifestVocabulary.SymbolItem}"
                        : $"{symbol.Kind}: {symbol.Type}", IsKeyword: false));
            var keywords = site.Keywords.Select(keyword =>
                (Name: keyword, Detail: KeywordDetail(site, keyword), IsKeyword: true));
            foreach (var (name, detail, isKeyword) in symbols.Concat(keywords))
            {
                if (!name.StartsWith(prefix, site.IsImagePath ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                var newText = site.FollowingDelimiter is { } delimiter && name.EndsWith(delimiter) ? name[..^1] : name;
                if (isKeyword && name == SceneVocabulary.If)
                    newText = SceneVocabulary.IfPrefix;
                if (site.IsImagePath) newText = AuthoringSyntaxVocabulary.EscapeQuotedText(newText);
                newText = string.Concat(site.CompletionPrefix, newText, site.CompletionSuffix);
                result.Add(new(name, detail, site.ReplacementRange ?? site.Range, newText));
            }
        }
        return result.Distinct().OrderBy(item => item.Label, StringComparer.Ordinal).ToArray();
    }

    private static string KeywordDetail(CompletionSite site, string keyword) =>
        site.KeywordDetails?.GetValueOrDefault(keyword) ??
        (AuthoringSyntaxVocabulary.TryGetKeywordHelp(keyword, out var help) ? help.Description : "Talemark syntax");

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
