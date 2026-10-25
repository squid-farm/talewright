using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring;

public sealed record AuthoringCompletion(string Label, string Detail, SourceRange Range, string NewText);

/// <summary>Projects parser-recognized reference slots onto the current story's partial symbol table.</summary>
public static class StoryCompletion
{
    public static IReadOnlyList<AuthoringCompletion> Complete(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is "TW5001" or "TW5002"))
            throw new InvalidOperationException("Semantic completion is unavailable until project authoring metadata is supplied and valid.");
        var sites = analysis.CompletionSites.Where(site => site.Range.Start.File == position.File &&
            Compare(site.Range.Start, position) <= 0 && Compare(position, site.Range.End) <= 0).ToArray();
        var result = new List<AuthoringCompletion>();
        foreach (var site in sites)
        {
            var length = site.Positions.TakeWhile(point => Compare(point, position) <= 0).Count() - 1;
            var prefix = site.Text[..Math.Clamp(length, 0, site.Text.Length)];
            var symbols = (site.Candidates ?? analysis.Symbols).Where(symbol => site.Kinds.Contains(symbol.Kind))
                .Select(symbol => (symbol.Name, Detail: $"{symbol.Kind}: {symbol.Type}"));
            foreach (var (name, detail) in symbols.Concat(site.Keywords.Select(keyword =>
                         (keyword, site.KeywordDetails?.GetValueOrDefault(keyword) ?? "Talemark syntax"))))
            {
                if (!name.StartsWith(prefix, site.IsImagePath ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                var newText = site.FollowingDelimiter is { } delimiter && name.EndsWith(delimiter) ? name[..^1] : name;
                if (site.IsImagePath) newText = newText.Replace("\\", "\\\\").Replace("\"", "\\\"");
                result.Add(new(name, detail, site.Range, newText));
            }
        }
        return result.Distinct().OrderBy(item => item.Label, StringComparer.Ordinal).ToArray();
    }

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
