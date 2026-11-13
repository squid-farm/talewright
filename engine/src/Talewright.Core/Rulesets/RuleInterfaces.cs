using Talewright.Core.Identity;
using Talewright.Core.State;

namespace Talewright.Core.Rulesets;

/// <summary>Input for a skill check.</summary>
/// <param name="Actor">Who is attempting it.</param>
/// <param name="Skill">Stat being tested (usually a skill, may be an attribute).</param>
/// <param name="Roll">
/// Die result supplied by the runtime, or <c>null</c> when randomness is off
/// (the rule must then resolve deterministically, e.g. "take 10").
/// </param>
public sealed record SkillCheckRequest(EntityId Actor, StatId Skill, int? Roll);

/// <summary>The die and modifier contribution to a skill check's total.</summary>
public sealed record SkillCheckTotal(int Roll, int Modifier, int Total);

/// <summary>Outcome of a skill check, recorded verbatim in a <c>SkillCheckResolved</c> event.</summary>
public sealed record SkillCheckResult(int Roll, int Modifier, int Total, int? Difficulty, bool? Success);

/// <summary>
/// Ruleset-defined skill check resolution. Swapping this is how a story changes from d20 to 2d6, percentile, etc.
/// </summary>
public interface ISkillCheckRule
{
    /// <summary>Number of sides of the die the runtime should roll (e.g. 20). Return 0 for no die.</summary>
    int DieSides { get; }

    SkillCheckTotal CalculateTotal(IGameStateView state, SkillCheckRequest request);

    /// <summary>Decides whether a total meets a difficulty, including any ruleset-specific critical outcomes.</summary>
    bool IsSuccess(IGameStateView state, SkillCheckTotal total, int difficulty);
}

/// <summary>Ruleset-defined leveling. The runtime asks after each change whether a character's level should rise.</summary>
public interface ILevelingRule
{
    /// <summary>Stat that holds the level.</summary>
    StatId LevelStat { get; }

    /// <summary>The level the character should be at given their current state.</summary>
    int ComputeLevel(IGameStateView state, EntityId character);
}

/// <summary>
/// Contributes a bonus/penalty to a character's effective stat (equipment, conditions, auras…).
/// Implementations must read <see cref="IGameStateView.GetBaseStat"/>, never <see cref="IGameStateView.GetStat"/>,
/// to avoid infinite recursion.
/// </summary>
public interface IStatModifier
{
    int GetModifier(IGameStateView state, EntityId character, StatId stat);
}
