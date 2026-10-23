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
public sealed record SkillCheck(EntityId Actor, StatId Skill, Expr Difficulty);

/// <summary>
/// Resolves skill checks through the ruleset's <see cref="ISkillCheckRule"/>, rolling seeded dice and recording
/// both the roll and the result as events.
/// </summary>
public static class SkillCheckResolver
{
    public static SkillCheckResult Resolve(SkillCheck check, EventLog log, RandomnessMode randomness, NamedPredicateRegistry predicates)
        => Resolve(check, log, randomness, predicates, observer: null);

    public static SkillCheckResult Resolve(SkillCheck check, EventLog log, RandomnessMode randomness,
        NamedPredicateRegistry predicates, Action<RuleDecision>? observer)
    {
        var start = log.Events.Count;
        var state = log.State;
        var rule = state.Ruleset.SkillCheckRule
            ?? throw new InvalidOperationException($"Ruleset '{state.Ruleset.Id}' has no skill check rule.");

        int? roll = null;
        if (randomness != RandomnessMode.Off && rule.DieSides > 0)
        {
            roll = DeterministicDice.Roll(state.Seed, state.RollCount, rule.DieSides);
            log.Append(new DiceRolled(rule.DieSides, roll.Value));
        }

        var context = new EvaluationContext(log.State, predicates);
        var difficulty = RuleObservation.Evaluate(check.Difficulty, context, observer is not null, out var trace).Number;
        var result = rule.Resolve(log.State, new SkillCheckRequest(check.Actor, check.Skill, difficulty, roll));
        log.Append(new SkillCheckResolved(check.Actor, check.Skill, difficulty, result.Roll, result.Modifier, result.Total, result.Success));
        observer?.Invoke(new RuleDecision(DecisionKind.Check, $"{check.Actor}:{check.Skill}", state.CurrentScene,
            result.Success ? DecisionOutcome.Passed : DecisionOutcome.Failed,
            result.Success ? DecisionReason.CheckSucceeded : DecisionReason.CheckFailed, trace, start, log.Events.Count));
        return result;
    }
}
