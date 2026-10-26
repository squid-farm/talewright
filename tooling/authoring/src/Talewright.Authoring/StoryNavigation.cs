using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring;

/// <summary>Resolves parser-recognized references to the declaration in its original source file.</summary>
public static class StoryNavigation
{
    public static SourceRange? Definition(StoryAnalysis analysis, SourceLocation position)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is "TW5001" or "TW5002"))
            throw new InvalidOperationException("Navigation is unavailable until project authoring metadata is supplied and valid.");
        var declaration = analysis.Symbols.FirstOrDefault(symbol => symbol.Declaration is { } range && Contains(range, position));
        if (declaration is not null) return declaration.Declaration;
        foreach (var site in analysis.CompletionSites.Where(site => site.Text.Length > 0 && Contains(site.Range, position)))
        {
            var symbol = (site.Candidates ?? analysis.Symbols).FirstOrDefault(candidate =>
                site.Kinds.Contains(candidate.Kind) && candidate.Name == site.Text && candidate.Declaration is not null);
            if (symbol is not null) return symbol.Declaration;
        }
        return null;
    }

    private static bool Contains(SourceRange range, SourceLocation point) =>
        range.Start.File == point.File && Compare(range.Start, point) <= 0 && Compare(point, range.End) < 0;

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
