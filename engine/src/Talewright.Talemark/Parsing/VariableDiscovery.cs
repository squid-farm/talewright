using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal sealed record VariableDeclaration(VarId Id, string Expression, bool Numeric, SourceLocation Location);

internal static class VariableDiscovery
{
    internal static HashSet<VarId> Resolve(WorldDefinition manifest, HashSet<FlagId> flags,
        HashSet<SceneId> scenes, IReadOnlyList<VariableDeclaration> declarations,
        Dictionary<VarId, ExactNumber> numbers, Dictionary<VarId, string> text,
        CompilerOptions options, DiagnosticBag diagnostics, HashSet<VarId> itemVariables)
    {
        var first = declarations.DistinctBy(declaration => declaration.Id)
            .Where(declaration => !numbers.ContainsKey(declaration.Id) && !text.ContainsKey(declaration.Id)).ToArray();
        var unresolved = first.Select(declaration => declaration.Id).ToHashSet();
        var world = new WorldDefinition(manifest.Ruleset, manifest.Characters.Values, manifest.Items.Values,
            manifest.Factions.Values, flags, numbers, text, manifest.InitialFlags, manifest.TalemarkVersion);
        var symbols = new StorySymbols
        {
            World = world, Flags = flags, Scenes = scenes,
            Variables = numbers.Keys.Concat(text.Keys).Concat(unresolved).ToHashSet(),
            UnresolvedVariables = unresolved, Predicates = options.Predicates, Modifiers = options.Modifiers,
            ItemVariables = itemVariables,
        };
        var syntax = first.Where(declaration => !declaration.Numeric).ToDictionary(declaration => declaration.Id,
            declaration =>
            {
                var diagnostics = new DiagnosticBag();
                return ExpressionSyntaxParser.ParseExpression(
                    Lexer.Tokenize(declaration.Expression, manifest.TalemarkVersion),
                    declaration.Expression.Length, declaration.Location.Offset, diagnostics, sumOnly: true,
                    allowSkillChecks: true);
            });
        bool changed;
        do
        {
            changed = false;
            foreach (var declaration in first.Where(declaration => unresolved.Contains(declaration.Id)))
            {
                // Arithmetic and compound assignment constrain a variable to numbers, including self-increments.
                var expression = declaration.Numeric ? null :
                    ExpressionBinder.Bind(syntax[declaration.Id], symbols, new DiagnosticBag(),
                        declaration.Location.Offset, createSkillCheckIdentity: static (_, _) => default);
                var type = declaration.Numeric ? ExprType.Number : expression?.Type;
                if (type is not (ExprType.Number or ExprType.Text)) continue;
                if (!declaration.Numeric && manifest.TalemarkVersion == 2 &&
                    ExpressionBinder.IsItemSource(syntax[declaration.Id].Root, expression))
                    itemVariables.Add(declaration.Id);
                if (type == ExprType.Text) text.Add(declaration.Id, "");
                else numbers.Add(declaration.Id, ExactNumber.Zero);
                unresolved.Remove(declaration.Id);
                changed = true;
            }
        } while (changed && unresolved.Count > 0);

        foreach (var declaration in first.Where(declaration => unresolved.Contains(declaration.Id)))
            diagnostics.Error(CompilerDiagnosticCodes.UnresolvedVariableType,
                $"Cannot infer a numeric or text type for variable '{declaration.Id}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                declaration.Location);
        return unresolved;
    }

    internal static IReadOnlyDictionary<VarId, ItemId> StaticallyKnownItemVariables(
        IReadOnlyList<VariableDeclaration> declarations, IReadOnlySet<VarId> itemVariables, int talemarkVersion)
    {
        var knownItems = new Dictionary<VarId, ItemId>();
        foreach (var group in declarations.Where(declaration => itemVariables.Contains(declaration.Id))
                     .GroupBy(declaration => declaration.Id))
        {
            ItemId? item = null;
            var isKnown = true;
            foreach (var declaration in group)
            {
                if (declaration.Numeric)
                {
                    isKnown = false;
                    break;
                }

                var tokens = Lexer.Tokenize(declaration.Expression, talemarkVersion);
                var syntax = ExpressionSyntaxParser.ParseExpression(tokens, declaration.Expression.Length,
                    declaration.Location.Offset, new DiagnosticBag(), sumOnly: true, allowSkillChecks: true);
                var source = syntax.Root;
                while (source is ParenthesizedExpressionNode parenthesized)
                    source = parenthesized.Inner;
                if (syntax.HasErrors ||
                    source is not ReferenceExpressionNode { Kind: ReferenceNamespace.Item, Member: null } reference)
                {
                    isKnown = false;
                    break;
                }

                var value = new ItemId(tokens.Source[reference.Name.Start..reference.Name.End]);
                if (item is { } existing && existing != value)
                {
                    isKnown = false;
                    break;
                }
                item = value;
            }

            if (isKnown && item is { } known)
                knownItems.Add(group.Key, known);
        }

        return knownItems;
    }
}
