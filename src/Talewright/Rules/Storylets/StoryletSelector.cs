using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Expressions;

namespace Talewright.Rules.Storylets;

/// <summary>
/// Anything that can be selected by game state: a scene, a storylet, an encounter. Rules only need the
/// selection metadata; the narrative content lives elsewhere.
/// </summary>
public interface IStorylet
{
    SceneId Id { get; }

    /// <summary>Eligibility predicate; null means always eligible.</summary>
    Expr? Requires { get; }

    IReadOnlySet<string> Tags { get; }

    /// <summary>Higher priority sorts first.</summary>
    int Priority { get; }

    /// <summary>If true the storylet becomes ineligible once it has been visited.</summary>
    bool Once { get; }
}

/// <summary>
/// Quality-based narrative selection. "On rails" stories mostly use explicit diverts; hubs and (later)
/// open-world locations ask the selector for everything currently eligible.
/// </summary>
public static class StoryletSelector
{
    public static bool IsEligible(IStorylet storylet, EvaluationContext context) =>
        !(storylet.Once && context.State.VisitCount(storylet.Id) > 0)
        && (storylet.Requires is null || ExpressionEvaluator.IsTrue(storylet.Requires, context));

    /// <summary>
    /// Eligible storylets carrying <paramref name="tag"/>, ordered by descending priority then declaration order.
    /// </summary>
    public static IReadOnlyList<T> SelectEligible<T>(IEnumerable<T> storylets, string tag, EvaluationContext context)
        where T : IStorylet =>
        storylets
            .Select((s, index) => (s, index))
            .Where(x => x.s.Tags.Contains(tag) && IsEligible(x.s, context))
            .OrderByDescending(x => x.s.Priority)
            .ThenBy(x => x.index)
            .Select(x => x.s)
            .ToArray();
}
