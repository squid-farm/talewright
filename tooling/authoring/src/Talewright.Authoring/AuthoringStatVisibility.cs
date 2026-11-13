using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;

namespace Talewright.Authoring;

internal static class AuthoringStatVisibility
{
    internal static bool IsVisible(StoryAnalysis analysis, StatId stat, IGameStateView state)
    {
        if (analysis.CharacterSheet is not { } sheet || !sheet.Stats)
            return false;
        if (!sheet.StatRules.TryGetValue(stat, out var condition))
            return true;
        return CanEvaluate(condition, state) &&
            ExpressionEvaluator.IsTrue(condition, new EvaluationContext(state, NamedPredicateRegistry.Empty));
    }

    // Exported ruleset metadata cannot establish whether effective stats have project-defined modifiers.
    private static bool CanEvaluate(Expr expression, IGameStateView state) => expression switch
    {
        UnaryExpr unary => CanEvaluate(unary.Operand, state),
        BinaryExpr binary => CanEvaluate(binary.Left, state) && CanEvaluate(binary.Right, state),
        StandingTierRef => state.Ruleset.Tiers.Count > 0,
        NumberLiteral or BoolLiteral or TextLiteral or TierLiteral or StandingRef or FlagRef or VarRef or TextVarRef
            or NameRef or PronounRef or FactionNameRef or ItemNameRef or HasItem or Visited
            or SceneVisitCountRef or InventoryCountRef => true,
        _ => false,
    };
}
