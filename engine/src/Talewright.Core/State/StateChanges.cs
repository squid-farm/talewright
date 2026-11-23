using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core;

namespace Talewright.Core.State;

/// <summary>
/// Turns intentions ("add 3 hp", "give a rope") into the events that record their results, applying clamping
/// and other invariants. Returns nothing when the intention changes nothing.
/// </summary>
public static class StateChanges
{
    public static IEnumerable<GameEvent> SetStat(IGameStateView state, EntityId character, StatId stat, int value)
    {
        if (state.World.TalemarkVersion == 2 && state.Ruleset.IsFractionalResource(stat))
        {
            foreach (var change in SetStat(state, character, stat, ExactNumber.FromInteger(value)))
                yield return change;
            yield break;
        }
        var old = state.GetBaseStat(character, stat);
        var clamped = state.Ruleset.StatsById.TryGetValue(stat, out var def) ? def.Clamp(value) : value;
        if (clamped != old)
        {
            yield return new StatChanged(character, stat, old, clamped);
        }
    }

    public static IEnumerable<GameEvent> AdjustStat(IGameStateView state, EntityId character, StatId stat, int delta) =>
        state.World.TalemarkVersion == 2 && state.Ruleset.IsFractionalResource(stat)
            ? SetStat(state, character, stat, state.GetExactBaseStat(character, stat) + delta)
            : SetStat(state, character, stat, state.World.TalemarkVersion == 2
                ? (int)Math.Clamp((long)state.GetBaseStat(character, stat) + delta, int.MinValue, int.MaxValue)
                : state.GetBaseStat(character, stat) + delta);

    public static IEnumerable<GameEvent> SetStat(IGameStateView state, EntityId character, StatId stat, ExactNumber value)
    {
        if (state.World.TalemarkVersion == 1 || !state.Ruleset.IsFractionalResource(stat))
            return SetStat(state, character, stat, value.ToInt32RoundedAwayFromZero());
        var def = state.Ruleset.StatsById[stat];
        var old = state.GetExactBaseStat(character, stat);
        var clamped = def.ClampExact(value);
        return clamped == old ? [] : [new ExactStatChanged(character, stat, old.ToCanonicalString(), clamped.ToCanonicalString())];
    }

    public static IEnumerable<GameEvent> AdjustStat(IGameStateView state, EntityId character, StatId stat, ExactNumber delta) =>
        state.World.TalemarkVersion == 2 && state.Ruleset.IsFractionalResource(stat)
            ? SetStat(state, character, stat, state.GetExactBaseStat(character, stat) + delta)
            : AdjustStat(state, character, stat, delta.ToInt32RoundedAwayFromZero());

    public static IEnumerable<GameEvent> GiveItem(EntityId character, ItemId item, int quantity)
    {
        if (quantity > 0)
        {
            yield return new ItemsGranted(character, item, quantity);
        }
    }

    public static IEnumerable<GameEvent> TakeItem(IGameStateView state, EntityId character, ItemId item, int quantity)
    {
        if (state.World.TalemarkVersion == 2 && state.World.Items.TryGetValue(item, out var definition) && definition.Divisible)
        {
            foreach (var change in TakeItem(state, character, item, ExactNumber.FromInteger(quantity)))
                yield return change;
            yield break;
        }
        var removed = Math.Min(quantity, state.GetItemCount(character, item));
        if (removed > 0)
        {
            yield return new ItemsRemoved(character, item, removed);
        }
    }

    public static IEnumerable<GameEvent> GiveItem(IGameStateView state, EntityId character, ItemId item, ExactNumber quantity)
    {
        if (state.World.TalemarkVersion == 1 || !state.World.Items[item].Divisible)
            return GiveItem(character, item, quantity.ToInt32RoundedAwayFromZero());
        return quantity > 0 ? [new ExactItemsGranted(character, item, quantity.ToCanonicalString())] : [];
    }

    public static IEnumerable<GameEvent> TakeItem(IGameStateView state, EntityId character, ItemId item, ExactNumber quantity)
    {
        if (state.World.TalemarkVersion == 1 || !state.World.Items[item].Divisible)
            return TakeItem(state, character, item, quantity.ToInt32RoundedAwayFromZero());
        var held = state.GetExactItemCount(character, item);
        var removed = quantity < held ? quantity : held;
        return removed > 0 ? [new ExactItemsRemoved(character, item, removed.ToCanonicalString())] : [];
    }

    public static IEnumerable<GameEvent> SetStanding(IGameStateView state, FactionId faction, int value)
    {
        var old = state.GetStanding(faction);
        if (old != value)
        {
            yield return new StandingChanged(faction, old, value);
        }
    }

    public static IEnumerable<GameEvent> SetFlag(IGameStateView state, FlagId flag, bool value)
    {
        if (state.GetFlag(flag) != value)
        {
            yield return new FlagChanged(flag, value);
        }
    }

    public static IEnumerable<GameEvent> SetVariable(IGameStateView state, VarId variable, int value)
        => state.World.TalemarkVersion == 1
            ? SetIntegerVariable(state, variable, value)
            : SetExactVariable(state, variable, ExactNumber.FromInteger(value));

    public static IEnumerable<GameEvent> SetVariable(IGameStateView state, VarId variable, ExactNumber value)
        => state.World.TalemarkVersion == 1
            ? SetIntegerVariable(state, variable, value.ToInt32RoundedAwayFromZero())
            : SetExactVariable(state, variable, value);

    private static IEnumerable<GameEvent> SetIntegerVariable(IGameStateView state, VarId variable, int value)
    {
        var old = state.GetVariable(variable);
        if (old != value)
            yield return new VariableChanged(variable, old, value);
    }

    private static IEnumerable<GameEvent> SetExactVariable(IGameStateView state, VarId variable, ExactNumber value)
    {
        var old = state.GetExactVariable(variable);
        if (old != value)
            yield return new ExactVariableChanged(variable, old.ToCanonicalString(), value.ToCanonicalString());
    }

    public static IEnumerable<GameEvent> SetTextVariable(IGameStateView state, VarId variable, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var old = state.GetTextVariable(variable);
        if (!string.Equals(old, value, StringComparison.Ordinal))
        {
            yield return new TextVariableChanged(variable, old, value);
        }
    }

    /// <summary>Checks the ruleset's leveling rule and emits a level change if one is due.</summary>
    public static IEnumerable<GameEvent> CheckLevel(IGameStateView state, EntityId character)
    {
        if (state.Ruleset.LevelingRule is not { } rule)
        {
            yield break;
        }

        var current = state.GetBaseStat(character, rule.LevelStat);
        var target = rule.ComputeLevel(state, character);
        if (target != current)
        {
            yield return new LevelChanged(character, current, target);
        }
    }
}
