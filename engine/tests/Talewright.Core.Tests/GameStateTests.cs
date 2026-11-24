using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Rulesets.Generic;

namespace Talewright.Core.Tests;

public class GameStateTests
{
    private static readonly ItemId Rope = new("rope");
    private static readonly ItemId Amulet = new("amulet");
    private static readonly FactionId Guild = new("guild");

    private static WorldDefinition CreateWorld(Ruleset? ruleset = null) => new(
        ruleset ?? GenericRuleset.Create(),
        [new CharacterDefinition(EntityId.Player, "Mara", "Burns", Pronouns.She,
            new Dictionary<StatId, int> { [GenericRuleset.Dexterity] = 14 },
            new Dictionary<ItemId, int> { [Rope] = 1 })],
        [new ItemDefinition(Rope, "rope", new Dictionary<StatId, int>()),
         new ItemDefinition(Amulet, "amulet", new Dictionary<StatId, int> { [GenericRuleset.Wisdom] = 2 })],
        [new FactionDefinition(Guild, "Guild", 0)],
        [],
        new Dictionary<VarId, int>());

    [Fact]
    public void Initial_state_uses_declared_values_and_ruleset_defaults()
    {
        var state = GameState.CreateInitial(CreateWorld(), 1);

        Assert.Equal(14, state.GetStat(EntityId.Player, GenericRuleset.Dexterity));
        Assert.Equal(10, state.GetStat(EntityId.Player, GenericRuleset.Strength));
        Assert.Equal(1, state.GetStat(EntityId.Player, GenericRuleset.Level));
        Assert.Equal(1, state.GetItemCount(EntityId.Player, Rope));
    }

    [Fact]
    public void Stat_changes_are_clamped_to_definition_bounds()
    {
        var log = new EventLog(CreateWorld(), 1);

        log.AppendRange(StateChanges.AdjustStat(log.State, EntityId.Player, GenericRuleset.HitPoints, -50));

        Assert.Equal(0, log.State.GetStat(EntityId.Player, GenericRuleset.HitPoints));
        var e = Assert.IsType<StatChanged>(Assert.Single(log.Events));
        Assert.Equal(10, e.OldValue);
    }

    [Fact]
    public void Exact_resource_state_and_extension_modifiers_survive_replay()
    {
        var ruleset = GenericRuleset.Create().ToBuilder().AddModifier(new FractionalModifier()).Build();
        var world = new WorldDefinition(ruleset,
            [new CharacterDefinition(EntityId.Player, "Mara", "", Pronouns.She,
                new Dictionary<StatId, int>(), new Dictionary<ItemId, int>())
            {
                ExactStats = new Dictionary<StatId, ExactNumber> { [GenericRuleset.HitPoints] = ExactNumber.Parse("20.5") },
            }],
            [], [], [], new Dictionary<VarId, int>(), talemarkVersion: 2);
        var log = new EventLog(world, 1);
        log.AppendRange(StateChanges.AdjustStat(log.State, EntityId.Player, GenericRuleset.HitPoints,
            ExactNumber.FromFraction(-1, 3)));
        var expected = ExactNumber.Parse("20.5") - ExactNumber.FromFraction(1, 3);
        Assert.Equal(expected, log.State.GetExactBaseStat(EntityId.Player, GenericRuleset.HitPoints));
        Assert.Equal(expected + ExactNumber.FromFraction(1, 4), log.State.GetExactStat(EntityId.Player, GenericRuleset.HitPoints));
        Assert.IsType<ExactStatChanged>(Assert.Single(log.Events));
        var replay = EventLog.Replay(world, 1, log.Events);
        Assert.Equal(expected, replay.State.GetExactBaseStat(EntityId.Player, GenericRuleset.HitPoints));
    }

    private sealed class FractionalModifier : IStatModifier
    {
        public int GetModifier(IGameStateView state, EntityId character, StatId stat) => 0;
        public ExactNumber GetExactModifier(IGameStateView state, EntityId character, StatId stat) =>
            stat == GenericRuleset.HitPoints ? ExactNumber.FromFraction(1, 4) : ExactNumber.Zero;
    }

    [Fact]
    public void Held_items_modify_effective_but_not_base_stats()
    {
        var log = new EventLog(CreateWorld(), 1);

        log.AppendRange(StateChanges.GiveItem(EntityId.Player, Amulet, 1));

        Assert.Equal(12, log.State.GetStat(EntityId.Player, GenericRuleset.Wisdom));
        Assert.Equal(10, log.State.GetBaseStat(EntityId.Player, GenericRuleset.Wisdom));

        log.AppendRange(StateChanges.TakeItem(log.State, EntityId.Player, Amulet, 5));
        Assert.Equal(10, log.State.GetStat(EntityId.Player, GenericRuleset.Wisdom));
        Assert.Equal(1, ((ItemsRemoved)log.Events[^1]).Quantity);
    }

    [Fact]
    public void No_op_changes_emit_no_events()
    {
        var log = new EventLog(CreateWorld(), 1);

        log.AppendRange(StateChanges.SetStanding(log.State, Guild, 0));
        log.AppendRange(StateChanges.TakeItem(log.State, EntityId.Player, Amulet, 1));

        Assert.Empty(log.Events);
    }

    [Fact]
    public void Version_one_variable_changes_retain_integer_events_and_no_op_changes_emit_none()
    {
        var log = new EventLog(CreateWorld(), 1);
        var variable = new VarId("counter");

        log.AppendRange(StateChanges.SetVariable(log.State, variable, 10));
        log.AppendRange(StateChanges.SetVariable(log.State, variable, 10));

        Assert.Equal(10, log.State.GetVariable(variable));
        Assert.Equal(new VariableChanged(variable, 0, 10), Assert.Single(log.Events));

        log.AppendRange(StateChanges.SetVariable(log.State, variable, 11));
        Assert.Equal(new VariableChanged(variable, 10, 11), log.Events[^1]);
    }

    [Fact]
    public void Version_one_item_grants_wrap_and_removal_to_zero_deletes_entries()
    {
        var log = new EventLog(CreateWorld(), 1);

        log.Append(new ItemsGranted(EntityId.Player, Rope, int.MaxValue));

        Assert.Equal(unchecked(1 + int.MaxValue), log.State.GetItemCount(EntityId.Player, Rope));

        var removalLog = new EventLog(CreateWorld(), 1);
        removalLog.AppendRange(StateChanges.GiveItem(EntityId.Player, Rope, 2));
        removalLog.AppendRange(StateChanges.TakeItem(removalLog.State, EntityId.Player, Rope, 5));

        Assert.Equal(0, removalLog.State.GetItemCount(EntityId.Player, Rope));
        Assert.DoesNotContain(removalLog.State.GetInventory(EntityId.Player),
            item => item.Key == Rope);
    }

    [Fact]
    public void Stored_negative_version_one_item_counts_stay_queryable_but_are_hidden_from_enumeration()
    {
        var log = new EventLog(CreateWorld(), 1);

        log.Append(new ItemsGranted(EntityId.Player, Rope, -3));

        Assert.Equal(-2, log.State.GetItemCount(EntityId.Player, Rope));
        Assert.Equal(-2, log.State.GetExactItemCount(EntityId.Player, Rope).ToInt32RoundedAwayFromZero());
        Assert.DoesNotContain(log.State.GetInventory(EntityId.Player),
            item => item.Key == Rope);
    }

    [Fact]
    public void Held_item_modifiers_wrap_before_version_one_bounds()
    {
        var stat = new StatId("overflow");
        var ruleset = GenericRuleset.Create().ToBuilder()
            .AddStat(new StatDefinition(stat, "Overflow", StatCategory.Custom, 0, 20, 0))
            .Build();
        var world = new WorldDefinition(ruleset,
            [new CharacterDefinition(EntityId.Player, "Mara", "", Pronouns.She,
                new Dictionary<StatId, int> { [stat] = 20 },
                new Dictionary<ItemId, int> { [Amulet] = 1 })],
            [new ItemDefinition(Amulet, "amulet", new Dictionary<StatId, int> { [stat] = int.MaxValue })],
            [], [], new Dictionary<VarId, int>());
        var state = GameState.CreateInitial(world, 1);

        Assert.Equal(0, state.GetStat(EntityId.Player, stat));
    }

    [Fact]
    public void Ruleset_modifiers_wrap_before_version_one_bounds()
    {
        var stat = new StatId("overflow");
        var ruleset = GenericRuleset.Create().ToBuilder()
            .AddStat(new StatDefinition(stat, "Overflow", StatCategory.Custom, 0, 20, 0))
            .AddModifier(new IntOverflowModifier(stat))
            .Build();
        var world = new WorldDefinition(ruleset,
            [new CharacterDefinition(EntityId.Player, "Mara", "", Pronouns.She,
                new Dictionary<StatId, int> { [stat] = 20 },
                new Dictionary<ItemId, int>())],
            [], [], [], new Dictionary<VarId, int>());
        var state = GameState.CreateInitial(world, 1);

        Assert.Equal(0, state.GetStat(EntityId.Player, stat));
    }

    private sealed record IntOverflowModifier(StatId Stat) : IStatModifier
    {
        public int GetModifier(IGameStateView state, EntityId character, StatId stat) =>
            stat == Stat ? int.MaxValue : 0;
    }

    [Fact]
    public void Replay_reproduces_state_and_truncate_rewinds()
    {
        var world = CreateWorld();
        var log = new EventLog(world, 42);
        log.Append(new SceneEntered(new SceneId("a")));
        log.AppendRange(StateChanges.SetStanding(log.State, Guild, 15));
        log.Append(new DiceRolled(20, 7));
        log.Append(new SceneEntered(new SceneId("b")));

        var replayed = EventLog.Replay(world, 42, log.Events);

        Assert.Equal(new SceneId("b"), replayed.State.CurrentScene);
        Assert.Equal(15, replayed.State.GetStanding(Guild));
        Assert.Equal(1, replayed.State.RollCount);

        log.Truncate(1);
        Assert.Equal(new SceneId("a"), log.State.CurrentScene);
        Assert.Equal(0, log.State.GetStanding(Guild));
        Assert.Equal(0, log.State.VisitCount(new SceneId("b")));
    }

    [Theory]
    [InlineData(0UL, 2UL, 3UL, 0UL)]
    [InlineData(null, 2UL, 3UL, 2UL)]
    [InlineData(null, null, 0UL, 0UL)]
    public void Seed_resolution_prefers_supplied_values_in_order(
        ulong? primary, ulong? secondary, ulong? fallback, ulong expected)
    {
        Assert.Equal(expected, DeterministicDice.ResolveSeed(primary, secondary, fallback));
    }

    [Fact]
    public void Dice_are_deterministic_and_in_range()
    {
        var rolls = Enumerable.Range(0, 2000)
            .Select(i => DeterministicDice.Roll(99, $"random-site:{i}", "scene:start:visit:1", 20))
            .ToArray();

        Assert.Equal(rolls, Enumerable.Range(0, 2000)
            .Select(i => DeterministicDice.Roll(99, $"random-site:{i}", "scene:start:visit:1", 20)));
        Assert.All(rolls, r => Assert.InRange(r, 1, 20));
        Assert.Equal(20, rolls.Distinct().Count());
        Assert.NotEqual(rolls.Take(20), Enumerable.Range(0, 20)
            .Select(i => DeterministicDice.Roll(100, $"random-site:{i}", "scene:start:visit:1", 20)));
    }

    [Fact]
    public void Keyed_dice_vectors_are_stable()
    {
        Assert.Equal(19, DeterministicDice.Roll(42, "cave:player:athletics:0", "scene:cave:visit:1", 20));
        Assert.Equal(6, DeterministicDice.Roll(0xDEADBEEF, "hall:guide:survival:2", "scene:hall:visit:4", 6));
    }

    [Fact]
    public void Other_random_sites_do_not_shift_a_roll()
    {
        var expected = DeterministicDice.Roll(42, "skill-check:cave:player:athletics:0", "scene:cave:visit:1", 20);
        _ = DeterministicDice.Roll(42, "voice-line:3", "scene:cave:visit:1", 20);

        Assert.Equal(expected,
            DeterministicDice.Roll(42, "skill-check:cave:player:athletics:0", "scene:cave:visit:1", 20));
    }
}
