using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;

namespace Talewright.Rulesets.Generic;

/// <summary>
/// The default, opt-out ruleset: six d20-style attributes, eighteen skills, hit points, experience and levels,
/// five reputation tiers, d20 skill checks and an XP leveling curve.
/// </summary>
/// <remarks>
/// Stories reference stats by the ids below (<c>player:dex</c>, <c>skill-check athletics dc 14</c>).
/// Remove or replace anything with <see cref="Ruleset.ToBuilder"/>.
/// </remarks>
public static class GenericRuleset
{
    public const string Id = "generic";

    public static readonly StatId Strength = new("str");
    public static readonly StatId Dexterity = new("dex");
    public static readonly StatId Constitution = new("con");
    public static readonly StatId Intelligence = new("int");
    public static readonly StatId Wisdom = new("wis");
    public static readonly StatId Charisma = new("cha");
    public static readonly StatId HitPoints = new("hp");
    public static readonly StatId Experience = new("xp");
    public static readonly StatId Level = new("level");

    /// <summary>Skill id → governing attribute.</summary>
    public static IReadOnlyList<(string Id, string Name, StatId Attribute)> Skills { get; } =
    [
        ("athletics", "Athletics", Strength),
        ("acrobatics", "Acrobatics", Dexterity),
        ("sleight-of-hand", "Sleight of Hand", Dexterity),
        ("stealth", "Stealth", Dexterity),
        ("arcana", "Arcana", Intelligence),
        ("history", "History", Intelligence),
        ("investigation", "Investigation", Intelligence),
        ("nature", "Nature", Intelligence),
        ("religion", "Religion", Intelligence),
        ("animal-handling", "Animal Handling", Wisdom),
        ("insight", "Insight", Wisdom),
        ("medicine", "Medicine", Wisdom),
        ("perception", "Perception", Wisdom),
        ("survival", "Survival", Wisdom),
        ("deception", "Deception", Charisma),
        ("intimidation", "Intimidation", Charisma),
        ("performance", "Performance", Charisma),
        ("persuasion", "Persuasion", Charisma),
    ];

    /// <summary>Builds a fresh copy of the generic ruleset.</summary>
    public static Ruleset Create()
    {
        var builder = new RulesetBuilder(Id)
            .AddStat(Attribute(Strength, "Strength"))
            .AddStat(Attribute(Dexterity, "Dexterity"))
            .AddStat(Attribute(Constitution, "Constitution"))
            .AddStat(Attribute(Intelligence, "Intelligence"))
            .AddStat(Attribute(Wisdom, "Wisdom"))
            .AddStat(Attribute(Charisma, "Charisma"));

        foreach (var (id, name, attribute) in Skills)
        {
            // Skill values are ranks of proficiency added to checks.
            builder.AddStat(new StatDefinition(new StatId(id), name, StatCategory.Skill, 0, 10, 0, attribute));
        }

        return builder
            .AddStat(new StatDefinition(HitPoints, "Hit Points", StatCategory.Resource, 0, 999, 10))
            .AddStat(new StatDefinition(Experience, "Experience", StatCategory.Progression, 0, int.MaxValue, 0))
            .AddStat(new StatDefinition(Level, "Level", StatCategory.Progression, 1, 20, 1))
            .WithTiers(
                new ReputationTier("hostile", "Hostile", int.MinValue),
                new ReputationTier("unfriendly", "Unfriendly", -30),
                new ReputationTier("neutral", "Neutral", -9),
                new ReputationTier("friendly", "Friendly", 10),
                new ReputationTier("allied", "Allied", 50))
            .WithSkillCheckRule(new D20SkillCheckRule())
            .WithLevelingRule(new ExperienceLevelingRule())
            .Build();
    }

    /// <summary>Classic attribute modifier: (score - 10) / 2, rounded down.</summary>
    public static int AttributeModifier(int score) => (int)Math.Floor((score - 10) / 2.0);

    private static StatDefinition Attribute(StatId id, string name) =>
        new(id, name, StatCategory.Attribute, 1, 30, 10);
}

/// <summary>
/// <c>d20 + attribute modifier + skill ranks ≥ DC</c>. With randomness off the die is a fixed 10 ("take 10").
/// When the tested stat is itself an attribute, only its modifier applies.
/// </summary>
public sealed class D20SkillCheckRule : ISkillCheckRule
{
    public int DieSides => 20;

    public SkillCheckResult Resolve(IGameStateView state, SkillCheckRequest request)
    {
        var ruleset = state.Ruleset;
        var definition = ruleset.StatsById[request.Skill];
        int modifier;
        if (definition.Category == StatCategory.Attribute)
        {
            modifier = GenericRuleset.AttributeModifier(state.GetStat(request.Actor, request.Skill));
        }
        else
        {
            modifier = state.GetStat(request.Actor, request.Skill);
            if (definition.GoverningAttribute is { } attribute && ruleset.HasStat(attribute))
            {
                modifier += GenericRuleset.AttributeModifier(state.GetStat(request.Actor, attribute));
            }
        }

        var roll = request.Roll ?? 10;
        var total = roll + modifier;
        return new SkillCheckResult(roll, modifier, total, request.Difficulty, total >= request.Difficulty);
    }
}

/// <summary>Level n requires 100 × n × (n − 1) / 2 experience (0, 100, 300, 600, …).</summary>
public sealed class ExperienceLevelingRule : ILevelingRule
{
    public StatId LevelStat => GenericRuleset.Level;

    public int ComputeLevel(IGameStateView state, EntityId character)
    {
        if (!state.Ruleset.HasStat(GenericRuleset.Experience))
        {
            return state.GetBaseStat(character, LevelStat);
        }

        var xp = state.GetBaseStat(character, GenericRuleset.Experience);
        var level = 1;
        while (level < 20 && xp >= 100 * (level + 1) * level / 2)
        {
            level++;
        }

        // Levels never go down because of XP loss.
        return Math.Max(level, state.GetBaseStat(character, LevelStat));
    }
}
