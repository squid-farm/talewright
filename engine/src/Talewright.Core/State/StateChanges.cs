using Talewright.Core.Events;
using Talewright.Core.Identity;

namespace Talewright.Core.State;

/// <summary>
/// Turns intentions ("add 3 hp", "give a rope") into the events that record their results, applying clamping
/// and other invariants. Returns nothing when the intention changes nothing.
/// </summary>
public static class StateChanges
{
    public static IEnumerable<GameEvent> SetStat(IGameStateView state, EntityId character, StatId stat, int value)
    {
        var old = state.GetBaseStat(character, stat);
        var clamped = state.Ruleset.StatsById.TryGetValue(stat, out var def) ? def.Clamp(value) : value;
        if (clamped != old)
        {
            yield return new StatChanged(character, stat, old, clamped);
        }
    }

    public static IEnumerable<GameEvent> AdjustStat(IGameStateView state, EntityId character, StatId stat, int delta) =>
        SetStat(state, character, stat, state.GetBaseStat(character, stat) + delta);

    public static IEnumerable<GameEvent> GiveItem(EntityId character, ItemId item, int quantity)
    {
        if (quantity > 0)
        {
            yield return new ItemsGranted(character, item, quantity);
        }
    }

    public static IEnumerable<GameEvent> TakeItem(IGameStateView state, EntityId character, ItemId item, int quantity)
    {
        var removed = Math.Min(quantity, state.GetItemCount(character, item));
        if (removed > 0)
        {
            yield return new ItemsRemoved(character, item, removed);
        }
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
    {
        var old = state.GetVariable(variable);
        if (old != value)
        {
            yield return new VariableChanged(variable, old, value);
        }
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
