using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

/// <summary>
/// Reads what a <c>.tale</c> file declares straight from its syntax: scene ids, and the effect text whose
/// assignments declare flags and variables. Every file is collected before any is bound, so forward references
/// across files resolve. Nothing here consults story symbols or parses prose.
/// </summary>
internal static class TaleDeclarations
{
    /// <summary>Reports each scene header and, in source order, each effect the binder will bind.</summary>
    /// <param name="file">Story-relative path used for locations.</param>
    /// <param name="syntax">The file's syntax.</param>
    /// <param name="scene">Receives each scene id at its header.</param>
    /// <param name="effects">Receives effect text at its first character, or <see langword="null"/> to skip effects.</param>
    /// <param name="cancellationToken">Stops collection.</param>
    public static void Collect(string file, TaleFileSyntax syntax, Action<SceneId, SourceLocation> scene,
        Action<string, SourceLocation>? effects, CancellationToken cancellationToken = default)
    {
        var lines = 0;
        foreach (var sceneSyntax in syntax.Scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scene(new SceneId(sceneSyntax.Id), new SourceLocation(file, sceneSyntax.Line + 1, sceneSyntax.HeaderIndent + 1));
            if (effects is null)
                continue;

            foreach (var line in sceneSyntax.Lines)
            {
                if ((lines++ & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                // Outcome lines no check attached are never bound; dropped choices still bind their clauses.
                if (line.IsSuppressed ||
                    line.Kind is TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome && !line.IsAttached)
                    continue;

                if (line.Kind == TaleLineKind.Effects)
                {
                    effects(syntax.Value(sceneSyntax, line).ToString(),
                        At(file, sceneSyntax, line, sceneSyntax.Part(line).ValueStart));
                    continue;
                }

                if (line.Kind is not (TaleLineKind.Choice or TaleLineKind.SuccessOutcome or TaleLineKind.FailureOutcome))
                    continue;
                foreach (var clause in sceneSyntax.Clauses(line))
                {
                    if (clause.Kind == TaleClauseKind.Effects)
                        effects(syntax.Value(sceneSyntax, line, clause).ToString(),
                            At(file, sceneSyntax, line, clause.ValueStart));
                }
            }
        }
    }

    private static SourceLocation At(string file, TaleSceneSyntax scene, TaleLineSyntax line, int column) =>
        new(file, scene.FileLine(line.Line) + 1, column + 1);
}
