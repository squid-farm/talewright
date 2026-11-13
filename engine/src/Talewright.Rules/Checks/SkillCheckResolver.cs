using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;

namespace Talewright.Rules.Checks;

/// <summary>Whether a story uses dice. Configured per story (<c>randomness:</c> in story.talewright.yaml).</summary>
public enum RandomnessMode
{
    /// <summary>No dice; checks resolve deterministically (the ruleset decides how, e.g. "take 10").</summary>
    Off,

    /// <summary>Dice are rolled only by explicit <c>skill-check</c> directives.</summary>
    ChecksOnly,
}

/// <summary>A skill check as declared in a story.</summary>
public sealed record SkillCheck(EntityId Actor, StatId Skill, Expr? Difficulty);

/// <summary>
/// Resolves skill checks through the ruleset's <see cref="ISkillCheckRule"/>, rolling seeded dice and recording
/// both the roll and the result as events.
/// </summary>
public static class SkillCheckResolver
{
    public static SkillCheckResult Resolve(SkillCheck check, SkillCheckIdentity identity, EventLog log,
        RandomnessMode randomness, NamedPredicateRegistry predicates, Action<RuleDecision>? observer = null)
    {
        var state = log.State;
        if (identity.Actor != check.Actor || identity.Skill != check.Skill)
            throw new ArgumentException("The check identity must match the check's actor and skill.", nameof(identity));

        var executionScene = state.CurrentScene ?? identity.Scene;
        var visit = state.VisitCount(executionScene);
        if (FindInCurrentVisit(log.Events, identity) is { } existing)
        {
            var cached = new SkillCheckResult(existing.Roll, existing.Modifier, existing.Total,
                existing.Difficulty, existing.Success);
            Observe(cached, check, executionScene, observer, log.Events.Count, log.Events.Count, null);
            return cached;
        }

        var start = log.Events.Count;
        var rule = state.Ruleset.SkillCheckRule
            ?? throw new InvalidOperationException($"Ruleset '{state.Ruleset.Id}' has no skill check rule.");

        int? roll = null;
        if (randomness != RandomnessMode.Off && rule.DieSides > 0)
        {
            roll = DeterministicDice.Roll(state.Seed, identity.ToKey(),
                ExecutionKey(executionScene, visit), rule.DieSides);
            log.Append(new DiceRolled(rule.DieSides, roll.Value));
        }

        var total = rule.CalculateTotal(log.State, new SkillCheckRequest(check.Actor, check.Skill, roll));
        int? difficulty = null;
        bool? success = null;
        ExpressionTrace? trace = null;
        if (check.Difficulty is { } expression)
        {
            difficulty = RuleObservation.Evaluate(expression, new EvaluationContext(log.State, predicates),
                observer is not null, out trace).Number;
            success = rule.IsSuccess(log.State, total, difficulty.Value);
        }
        log.Append(new SkillCheckResolved(check.Actor, check.Skill, identity, executionScene, visit, difficulty,
            total.Roll, total.Modifier, total.Total, success));
        var result = new SkillCheckResult(total.Roll, total.Modifier, total.Total, difficulty, success);
        Observe(result, check, executionScene, observer, start, log.Events.Count, trace);
        return result;
    }

    // Every event of the current visit follows its SceneEntered, so the search stops there instead of scanning the log.
    private static SkillCheckResolved? FindInCurrentVisit(IReadOnlyList<GameEvent> events, SkillCheckIdentity identity)
    {
        for (var i = events.Count - 1; i >= 0; i--)
        {
            switch (events[i])
            {
                case SceneEntered:
                    return null;
                case SkillCheckResolved resolved when resolved.Identity == identity:
                    return resolved;
            }
        }
        return null;
    }

    private static void Observe(SkillCheckResult result, SkillCheck check, SceneId executionScene,
        Action<RuleDecision>? observer, int start, int end, ExpressionTrace? trace)
    {
        if (result.Success is not { } success) return;
        observer?.Invoke(new RuleDecision(DecisionKind.Check, $"{check.Actor}:{check.Skill}", executionScene,
            success ? DecisionOutcome.Passed : DecisionOutcome.Failed,
            success ? DecisionReason.CheckSucceeded : DecisionReason.CheckFailed, trace, start, end));
    }

    private static string ExecutionKey(SceneId scene, int visit) =>
        $"{scene.Value.Length}:{scene.Value}:{visit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
