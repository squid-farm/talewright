using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

internal sealed record VariableDeclaration(VarId Id, string Expression, bool Numeric, SourceLocation Location);

internal static class VariableDiscovery
{
    internal static HashSet<VarId> Resolve(WorldDefinition manifest, HashSet<FlagId> flags,
        HashSet<SceneId> scenes, IReadOnlyList<VariableDeclaration> declarations,
        Dictionary<VarId, int> numbers, Dictionary<VarId, string> text,
        CompilerOptions options, DiagnosticBag diagnostics)
    {
        var first = declarations.DistinctBy(declaration => declaration.Id)
            .Where(declaration => !numbers.ContainsKey(declaration.Id) && !text.ContainsKey(declaration.Id)).ToArray();
        var unresolved = first.Select(declaration => declaration.Id).ToHashSet();
        var world = new WorldDefinition(manifest.Ruleset, manifest.Characters.Values, manifest.Items.Values,
            manifest.Factions.Values, flags, numbers, text, manifest.InitialFlags);
        var symbols = new StorySymbols
        {
            World = world, Flags = flags, Scenes = scenes,
            Variables = numbers.Keys.Concat(text.Keys).Concat(unresolved).ToHashSet(),
            UnresolvedVariables = unresolved, Predicates = options.Predicates, Modifiers = options.Modifiers,
        };
        var syntax = first.Where(declaration => !declaration.Numeric).ToDictionary(declaration => declaration.Id,
            declaration =>
            {
                var diagnostics = new DiagnosticBag();
                return ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(declaration.Expression),
                    declaration.Expression.Length, declaration.Location.Offset, diagnostics, sumOnly: true);
            });
        bool changed;
        do
        {
            changed = false;
            foreach (var declaration in first.Where(declaration => unresolved.Contains(declaration.Id)))
            {
                // Arithmetic and compound assignment constrain a variable to integers, including self-increments.
                var type = declaration.Numeric ? ExprType.Number :
                    ExpressionBinder.Bind(syntax[declaration.Id], symbols, new DiagnosticBag(),
                        declaration.Location.Offset)?.Type;
                if (type is not (ExprType.Number or ExprType.Text)) continue;
                if (type == ExprType.Text) text.Add(declaration.Id, "");
                else numbers.Add(declaration.Id, 0);
                unresolved.Remove(declaration.Id);
                changed = true;
            }
        } while (changed && unresolved.Count > 0);

        foreach (var declaration in first.Where(declaration => unresolved.Contains(declaration.Id)))
            diagnostics.Error(CompilerDiagnosticCodes.UnresolvedVariableType,
                $"Cannot infer an integer or text type for variable '{declaration.Id}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                declaration.Location);
        return unresolved;
    }
}
