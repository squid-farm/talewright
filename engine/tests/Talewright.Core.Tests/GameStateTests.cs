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

    [Fact]
    public void Dice_are_deterministic_and_in_range()
    {
        var rolls = Enumerable.Range(0, 2000).Select(i => DeterministicDice.Roll(99, i, 20)).ToArray();

        Assert.Equal(rolls, Enumerable.Range(0, 2000).Select(i => DeterministicDice.Roll(99, i, 20)));
        Assert.All(rolls, r => Assert.InRange(r, 1, 20));
        Assert.Equal(20, rolls.Distinct().Count());
        Assert.NotEqual(rolls.Take(20), Enumerable.Range(0, 20).Select(i => DeterministicDice.Roll(100, i, 20)));
    }

    [Fact]
    public void Dice_sequence_never_changes_between_versions()
    {
        // Golden values computed from the reference SplitMix64. If this fails, saved games would replay with
        // different rolls: don't "fix" the expected values, fix the dice.
        Assert.Equal([10, 7, 8, 17, 13, 18, 6, 12, 15, 9, 12, 7], Enumerable.Range(0, 12).Select(i => DeterministicDice.Roll(42, i, 20)));
        Assert.Equal([1, 6, 2, 3, 1, 5, 4, 3, 5, 1, 3, 3], Enumerable.Range(0, 12).Select(i => DeterministicDice.Roll(0xDEADBEEF, i, 6)));
    }
}
