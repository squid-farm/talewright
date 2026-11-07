using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring;

/// <summary>Finds every authored occurrence of the declared name at a position, from a declaration or a reference.</summary>
public static class StoryReferences
{
    /// <summary>
    /// Source ranges naming the same identity as the name at <paramref name="position"/>, in file and source order,
    /// or an empty list when the position is not on an author-declared name. Declarations are included only when
    /// <paramref name="includeDeclaration"/> is set. <paramref name="analysis"/> must be a whole-story analysis.
    /// </summary>
    public static IReadOnlyList<SourceRange> Find(StoryAnalysis analysis, SourceLocation position, bool includeDeclaration)
    {
        if (analysis.Diagnostics.Any(diagnostic => diagnostic.Code is AuthoringDiagnosticCodes.CustomFeaturesUnavailable or AuthoringDiagnosticCodes.StoryAnalysisUnavailable))
            throw new InvalidOperationException("References are unavailable until project authoring metadata is supplied and valid.");
        var names = analysis.Names;
        if (names.At(position) is not { } name) return [];
        return names.Occurrences(name.Kind, name.Name)
            .Where(occurrence => includeDeclaration || !occurrence.IsDeclaration)
            .Select(occurrence => occurrence.Range)
            .ToArray();
    }
}
