using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
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

/// <summary>Deterministic all/any tag matching shared by runtime selection and conservative validation.</summary>
public sealed class StoryletTags
{
    public StoryletTags(IEnumerable<string> tags, bool matchAll = false)
    {
        var values = tags.ToArray();
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("A tag query requires nonempty distinct tags.", nameof(tags));
        }

        Tags = Array.AsReadOnly(values);
        MatchAll = matchAll;
    }

    public IReadOnlyList<string> Tags { get; }
    public bool MatchAll { get; }
    public bool Matches(IReadOnlySet<string> tags) => MatchAll ? Tags.All(tags.Contains) : Tags.Any(tags.Contains);
}

/// <summary>
/// Quality-based narrative selection. "On rails" stories mostly use explicit diverts; hubs and (later)
/// open-world locations ask the selector for everything currently eligible.
/// </summary>
public static class StoryletSelector
{
    public static bool IsEligible(IStorylet storylet, EvaluationContext context) =>
        EligibilityExclusion(storylet, context, capture: false, evaluateRequirement: null, out _) is null;

    private static DecisionReason? EligibilityExclusion<T>(T storylet, EvaluationContext context,
        bool capture, Func<T, (bool Available, ExpressionTrace? Trace)>? evaluateRequirement,
        out ExpressionTrace? trace)
        where T : IStorylet
    {
        trace = null;
        if (storylet.Once && context.State.VisitCount(storylet.Id) > 0)
        {
            return DecisionReason.AlreadyVisited;
        }
        if (evaluateRequirement is not null)
        {
            var result = evaluateRequirement(storylet);
            trace = result.Trace;
            return result.Available ? null : DecisionReason.ConditionFalse;
        }
        return RuleObservation.Test(storylet.Requires, context, capture, out trace) ? null : DecisionReason.ConditionFalse;
    }

    /// <summary>Filters before stable ordering and limiting; excludes the hub itself and duplicate scene ids.</summary>
    public static IReadOnlyList<T> SelectEligible<T>(IEnumerable<T> storylets, StoryletTags tags,
        EvaluationContext context, SceneId? exclude = null, int? limit = null)
        where T : IStorylet => SelectEligible(storylets, tags, context, exclude, limit, observer: null);

    public static IReadOnlyList<T> SelectEligible<T>(IEnumerable<T> storylets, StoryletTags tags,
        EvaluationContext context, SceneId? exclude, int? limit, Action<RuleDecision>? observer, int? eventIndex = null)
        where T : IStorylet =>
        SelectEligibleCore(storylets, tags, context, exclude, limit, observer, eventIndex, evaluateRequirement: null);

    /// <summary>
    /// Selects eligible storylets using the caller's requirement evaluation, preserving its expression trace in
    /// decision observations.
    /// </summary>
    public static IReadOnlyList<T> SelectEligible<T>(IEnumerable<T> storylets, StoryletTags tags,
        EvaluationContext context, SceneId? exclude, int? limit, Action<RuleDecision>? observer, int? eventIndex,
        Func<T, (bool Available, ExpressionTrace? Trace)> evaluateRequirement)
        where T : IStorylet =>
        SelectEligibleCore(storylets, tags, context, exclude, limit, observer, eventIndex, evaluateRequirement);

    private static IReadOnlyList<T> SelectEligibleCore<T>(IEnumerable<T> storylets, StoryletTags tags,
        EvaluationContext context, SceneId? exclude, int? limit, Action<RuleDecision>? observer, int? eventIndex,
        Func<T, (bool Available, ExpressionTrace? Trace)>? evaluateRequirement)
        where T : IStorylet
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A storylet limit must be positive.");
        }

        var eligible = new List<(T Storylet, int Index, ExpressionTrace? Trace)>();
        var index = 0;
        foreach (var storylet in storylets)
        {
            DecisionReason? excluded = storylet.Id == exclude ? DecisionReason.SelfExcluded
                : !tags.Matches(storylet.Tags) ? DecisionReason.TagMismatch : null;
            ExpressionTrace? trace = null;
            if (excluded is null)
            {
                excluded = EligibilityExclusion(storylet, context, observer is not null, evaluateRequirement, out trace);
            }
            if (excluded is { } reason)
            {
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Excluded, reason, trace ?? (storylet.Requires is { } expr ? ExpressionTrace.NotEvaluated(expr) : null),
                    eventIndex, eventIndex, Priority: storylet.Priority, Limit: limit));
            }
            else
            {
                eligible.Add((storylet, index, trace));
            }
            index++;
        }

        var selected = new List<T>();
        var seen = new HashSet<SceneId>();
        var rank = 0;
        foreach (var (storylet, _, trace) in eligible.OrderByDescending(x => x.Storylet.Priority).ThenBy(x => x.Index))
        {
            var duplicate = !seen.Add(storylet.Id);
            if (!duplicate)
            {
                rank++;
            }
            var reason = duplicate ? DecisionReason.Duplicate
                : limit is { } cap && selected.Count >= cap ? DecisionReason.HubLimit : DecisionReason.PriorityOrder;
            var included = reason == DecisionReason.PriorityOrder;
            if (included)
            {
                selected.Add(storylet);
            }
            observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                included ? DecisionOutcome.Selected : DecisionOutcome.Excluded, reason, trace, eventIndex, eventIndex,
                Priority: storylet.Priority, Rank: duplicate ? null : rank, Limit: limit));
        }
        return selected.AsReadOnly();
    }
}
