using Talewright.Core.Identity;

namespace Talewright.Core.Rulesets;

/// <summary>
/// The game system a story runs on: which stats exist, how checks resolve, how leveling works and how
/// faction standing is tiered. Rulesets are immutable; build or adapt one with <see cref="RulesetBuilder"/>.
/// </summary>
/// <remarks>
/// Nothing in the engine assumes a particular stat exists. A dating sim can start from
/// <see cref="Empty"/> or remove <c>str</c>/<c>con</c> from the generic ruleset and add <c>charm</c>.
/// </remarks>
public sealed class Ruleset
{
    internal Ruleset(
        string id,
        IReadOnlyList<StatDefinition> stats,
        IReadOnlyList<ReputationTier> tiers,
        ISkillCheckRule? skillCheckRule,
        ILevelingRule? levelingRule,
        IReadOnlyList<IStatModifier> modifiers)
    {
        Id = id;
        Stats = stats;
        StatsById = stats.ToDictionary(s => s.Id);
        Tiers = tiers.OrderBy(t => t.Minimum).ToArray();
        SkillCheckRule = skillCheckRule;
        LevelingRule = levelingRule;
        Modifiers = modifiers;
    }

    /// <summary>A ruleset with no stats, no tiers and no rules.</summary>
    public static Ruleset Empty { get; } = new RulesetBuilder("empty").Build();

    public string Id { get; }

    /// <summary>Stats in declaration order (the character sheet uses this order).</summary>
    public IReadOnlyList<StatDefinition> Stats { get; }

    public IReadOnlyDictionary<StatId, StatDefinition> StatsById { get; }

    /// <summary>Reputation tiers sorted by ascending minimum.</summary>
    public IReadOnlyList<ReputationTier> Tiers { get; }

    /// <summary>Null when the ruleset has no notion of checks; stories using <c>skill-check</c> then fail to compile.</summary>
    public ISkillCheckRule? SkillCheckRule { get; }

    public ILevelingRule? LevelingRule { get; }

    public IReadOnlyList<IStatModifier> Modifiers { get; }

    public bool HasStat(StatId id) => StatsById.ContainsKey(id);

    /// <summary>The tier a standing value falls in, or null if the ruleset defines no tiers.</summary>
    public ReputationTier? TierFor(int standing)
    {
        ReputationTier? result = null;
        foreach (var tier in Tiers)
        {
            if (standing >= tier.Minimum)
            {
                result = tier;
            }
        }

        if (result is null && Tiers.Count > 0)
        {
            return Tiers[0];
        }
        else if (Tiers.Count == 0)
        {
            return null;
        }

        return result;
    }

    public ReputationTier? FindTier(string id) => Tiers.FirstOrDefault(t => t.Id == id);

    /// <summary>Index of a tier in ascending order, used for tier comparisons.</summary>
    public int TierIndex(ReputationTier tier) => Tiers.ToList().IndexOf(tier);

    /// <summary>Start a builder pre-populated with this ruleset, to remove/replace/add pieces.</summary>
    public RulesetBuilder ToBuilder(string? newId = null) => new(newId ?? Id, this);
}
