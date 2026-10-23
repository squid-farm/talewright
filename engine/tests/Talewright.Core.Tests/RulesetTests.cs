using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Rulesets.Generic;

namespace Talewright.Core.Tests;

public class RulesetTests
{
    [Fact]
    public void Stats_can_be_removed_and_replaced_for_other_genres()
    {
        var charm = new StatDefinition(new StatId("charm"), "Charm", StatCategory.Attribute, 0, 20, 5);

        var ruleset = GenericRuleset.Create().ToBuilder("dating-sim")
            .RemoveStat(GenericRuleset.Strength)
            .RemoveStat(GenericRuleset.Constitution)
            .AddStat(charm)
            .Build();

        Assert.False(ruleset.HasStat(GenericRuleset.Strength));
        Assert.True(ruleset.HasStat(charm.Id));
        Assert.Null(ruleset.StatsById[new StatId("athletics")].GoverningAttribute);
        Assert.Throws<InvalidOperationException>(() => ruleset.ToBuilder().AddStat(charm));
    }

    [Fact]
    public void Removing_level_stat_drops_leveling_rule()
    {
        var ruleset = GenericRuleset.Create().ToBuilder().RemoveStat(GenericRuleset.Level).Build();

        Assert.Null(ruleset.LevelingRule);
    }

    [Fact]
    public void Ruleset_collections_cannot_be_mutated_by_casting_them_back()
    {
        var ruleset = GenericRuleset.Create();
        var stat = new StatDefinition(new StatId("charm"), "Charm", StatCategory.Attribute, 0, 20, 5);
        var stats = Assert.IsAssignableFrom<IList<StatDefinition>>(ruleset.Stats);

        Assert.True(stats.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => stats[0] = stat);
        Assert.IsNotType<StatDefinition[]>(ruleset.Stats);
        Assert.IsNotType<Dictionary<StatId, StatDefinition>>(ruleset.StatsById);
        var statsById = Assert.IsAssignableFrom<IDictionary<StatId, StatDefinition>>(ruleset.StatsById);
        Assert.True(statsById.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => statsById[new StatId("charm")] = stat);
        Assert.IsNotType<ReputationTier[]>(ruleset.Tiers);
        Assert.IsNotType<IStatModifier[]>(ruleset.Modifiers);
        Assert.True(Assert.IsAssignableFrom<IList<ReputationTier>>(ruleset.Tiers).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<IList<IStatModifier>>(ruleset.Modifiers).IsReadOnly);
        Assert.Equal(ruleset.Stats.Count, ruleset.StatsById.Count);
    }

    [Theory]
    [InlineData(-100, "hostile")]
    [InlineData(-30, "unfriendly")]
    [InlineData(0, "neutral")]
    [InlineData(10, "friendly")]
    [InlineData(75, "allied")]
    public void Standing_maps_to_tiers(int standing, string tier)
    {
        Assert.Equal(tier, GenericRuleset.Create().TierFor(standing)!.Id);
    }

    [Theory]
    [InlineData(10, false)] // 10 + str mod(+2) + athletics 1 = 13 < 14
    [InlineData(11, true)]
    [InlineData(null, false)] // take 10
    public void D20_check_adds_attribute_modifier_and_skill_ranks(int? roll, bool success)
    {
        var state = StateWith(new() { [GenericRuleset.Strength] = 14, [new StatId("athletics")] = 1 });

        var result = new D20SkillCheckRule().Resolve(state, new SkillCheckRequest(EntityId.Player, new StatId("athletics"), 14, roll));

        Assert.Equal(3, result.Modifier);
        Assert.Equal(success, result.Success);
    }

    [Fact]
    public void Experience_raises_level()
    {
        var state = StateWith(new() { [GenericRuleset.Experience] = 0 });
        var log = new EventLog(state.World, 1);

        log.AppendRange(StateChanges.AdjustStat(log.State, EntityId.Player, GenericRuleset.Experience, 350));
        log.AppendRange(StateChanges.CheckLevel(log.State, EntityId.Player));

        Assert.Equal(3, log.State.GetStat(EntityId.Player, GenericRuleset.Level));
        Assert.IsType<LevelChanged>(log.Events[^1]);
    }

    private static GameState StateWith(Dictionary<StatId, int> stats) =>
        GameState.CreateInitial(
            new WorldDefinition(
                GenericRuleset.Create(),
                [new CharacterDefinition(EntityId.Player, "Mara", "", Pronouns.She, stats, new Dictionary<ItemId, int>())],
                [], [], [], new Dictionary<VarId, int>()),
            1);
}
