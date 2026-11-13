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

    /// <summary>
    /// Selects available storylets first, then shown-but-unavailable storylets for any remaining limited slots.
    /// </summary>
    public static IReadOnlyList<(T Storylet, bool IsAvailable)> SelectMenuCandidates<T>(
        IEnumerable<T> storylets, StoryletTags tags, EvaluationContext context, SceneId? exclude, int? limit,
        Action<RuleDecision>? observer, int? eventIndex,
        Func<T, (bool Available, bool ShowWhenUnavailable, ExpressionTrace? Trace)> evaluateRequirement)
        where T : IStorylet =>
        SelectMenuCore(storylets, tags, context, exclude, limit, observer, eventIndex, evaluateRequirement);

    private static IReadOnlyList<T> SelectEligibleCore<T>(IEnumerable<T> storylets, StoryletTags tags,
        EvaluationContext context, SceneId? exclude, int? limit, Action<RuleDecision>? observer, int? eventIndex,
        Func<T, (bool Available, ExpressionTrace? Trace)>? evaluateRequirement)
        where T : IStorylet
    {
        Func<T, (bool Available, bool ShowWhenUnavailable, ExpressionTrace? Trace)>? evaluate = null;
        if (evaluateRequirement is not null)
        {
            evaluate = storylet =>
            {
                var result = evaluateRequirement(storylet);
                return (result.Available, ShowWhenUnavailable: false, result.Trace);
            };
        }

        var available = SelectMenuCore(storylets, tags, context, exclude, limit, observer, eventIndex, evaluate)
            .Where(candidate => candidate.IsAvailable)
            .Select(candidate => candidate.Storylet)
            .ToArray();
        return Array.AsReadOnly(available);
    }

    private static IReadOnlyList<(T Storylet, bool IsAvailable)> SelectMenuCore<T>(
        IEnumerable<T> storylets, StoryletTags tags, EvaluationContext context, SceneId? exclude, int? limit,
        Action<RuleDecision>? observer, int? eventIndex,
        Func<T, (bool Available, bool ShowWhenUnavailable, ExpressionTrace? Trace)>? evaluateRequirement)
        where T : IStorylet
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A storylet limit must be positive.");
        }

        var candidates = new List<(T Storylet, int Index)>();
        var metadataExcluded = new List<(T Storylet, DecisionReason Reason)>();
        var index = 0;
        foreach (var storylet in storylets)
        {
            DecisionReason? excludedReason = storylet.Id == exclude ? DecisionReason.SelfExcluded
                : !tags.Matches(storylet.Tags) ? DecisionReason.TagMismatch : null;
            if (excludedReason is null && storylet.Once && context.State.VisitCount(storylet.Id) > 0)
            {
                excludedReason = DecisionReason.AlreadyVisited;
            }
            if (excludedReason is { } reason)
            {
                metadataExcluded.Add((storylet, reason));
            }
            else
            {
                candidates.Add((storylet, index));
            }
            index++;
        }

        foreach (var (storylet, reason) in metadataExcluded)
            observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                DecisionOutcome.Excluded, reason,
                storylet.Requires is { } expr ? ExpressionTrace.NotEvaluated(expr) : null,
                eventIndex, eventIndex, Priority: storylet.Priority, Limit: limit));

        var prioritized = candidates.OrderByDescending(x => x.Storylet.Priority).ThenBy(x => x.Index).ToArray();
        var evaluated = new List<(T Storylet, int Index, bool Available, bool ShowWhenUnavailable,
            ExpressionTrace? Trace, bool WasEvaluated)>(prioritized.Length);
        var availableIds = new HashSet<SceneId>();
        var availableCount = 0;
        var reachedLimit = false;
        foreach (var (storylet, sourceIndex) in prioritized)
        {
            if (reachedLimit)
            {
                evaluated.Add((storylet, sourceIndex, false, false, null, false));
                continue;
            }

            bool available;
            bool showWhenUnavailable;
            ExpressionTrace? trace;
            if (evaluateRequirement is null)
            {
                available = RuleObservation.Test(storylet.Requires, context, observer is not null, out trace);
                showWhenUnavailable = false;
            }
            else
            {
                var result = evaluateRequirement(storylet);
                available = result.Available;
                showWhenUnavailable = result.ShowWhenUnavailable;
                trace = result.Trace;
            }

            evaluated.Add((storylet, sourceIndex, available, showWhenUnavailable, trace, true));
            if (available && availableIds.Add(storylet.Id))
            {
                availableCount++;
                reachedLimit = limit is { } cap && availableCount >= cap;
            }
        }

        foreach (var (storylet, _, available, showWhenUnavailable, trace, wasEvaluated) in evaluated)
        {
            if (wasEvaluated && !available && !showWhenUnavailable)
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Excluded, DecisionReason.ConditionFalse,
                    trace ?? (storylet.Requires is { } expr ? ExpressionTrace.NotEvaluated(expr) : null),
                    eventIndex, eventIndex, Priority: storylet.Priority, Limit: limit));
        }

        foreach (var (storylet, _, _, _, _, wasEvaluated) in evaluated)
        {
            if (!wasEvaluated)
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Excluded, DecisionReason.HubLimit,
                    storylet.Requires is { } expr ? ExpressionTrace.NotEvaluated(expr) : null,
                    eventIndex, eventIndex, Priority: storylet.Priority, Limit: limit));
        }

        var selected = new List<(T Storylet, bool IsAvailable)>();
        var seen = new HashSet<SceneId>();
        var rank = 0;
        foreach (var (storylet, _, available, _, trace, wasEvaluated) in evaluated
                     .Where(x => x.Available)
                     .OrderByDescending(x => x.Storylet.Priority)
                     .ThenBy(x => x.Index)
                     .Concat(evaluated.Where(x => !x.Available && x.ShowWhenUnavailable)
                         .OrderByDescending(x => x.Storylet.Priority)
                         .ThenBy(x => x.Index)))
        {
            var duplicate = !seen.Add(storylet.Id);
            if (!duplicate && available)
            {
                rank++;
            }
            if (available)
            {
                DecisionReason reason;
                if (duplicate)
                {
                    reason = DecisionReason.Duplicate;
                }
                else if (limit is { } cap && selected.Count >= cap)
                {
                    reason = DecisionReason.HubLimit;
                }
                else
                {
                    reason = DecisionReason.PriorityOrder;
                }
                var included = reason == DecisionReason.PriorityOrder;
                if (included)
                {
                    selected.Add((storylet, IsAvailable: true));
                }
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    included ? DecisionOutcome.Selected : DecisionOutcome.Excluded, reason, trace, eventIndex, eventIndex,
                    Priority: storylet.Priority, Rank: duplicate ? null : rank, Limit: limit));
            }
            else if (!wasEvaluated)
            {
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Excluded, DecisionReason.HubLimit,
                    storylet.Requires is { } expr ? ExpressionTrace.NotEvaluated(expr) : null,
                    eventIndex, eventIndex, Priority: storylet.Priority, Limit: limit));
            }
            else if (!duplicate && (limit is null || selected.Count < limit))
            {
                selected.Add((storylet, IsAvailable: false));
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Offered, DecisionReason.ConditionFalse, trace, eventIndex, eventIndex,
                    Priority: storylet.Priority, Limit: limit));
            }
            else
            {
                var reason = duplicate ? DecisionReason.Duplicate : DecisionReason.HubLimit;
                observer?.Invoke(new RuleDecision(DecisionKind.Storylet, storylet.Id.Value, context.State.CurrentScene,
                    DecisionOutcome.Excluded, reason, trace, eventIndex, eventIndex,
                    Priority: storylet.Priority, Limit: limit));
            }
        }
        return selected.AsReadOnly();
    }
}
