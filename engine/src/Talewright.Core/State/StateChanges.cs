using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core;
using Talewright.Core.World;

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

    /// <summary>
    /// Takes copies from the held group a reference names. Removal reports the taken quantity; when the group is
    /// not at the front, the remaining groups are then restored so only the referenced group shrinks.
    /// </summary>
    public static IEnumerable<GameEvent> TakeItemGroup(IGameStateView state, EntityId character,
        ItemReference reference, ExactNumber quantity)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.IsGroup)
            return TakeItem(state, character, reference.Item, quantity);
        if (reference.Holder != character || reference.Resolve(state) is null)
            return [];

        var groups = state.GetItemGroups(character, reference.Item);
        var index = reference.FindGroup(groups);
        if (index < 0)
            return [];
        var requested = ExactNumber.FromInteger(quantity.ToInt32RoundedAwayFromZero());
        var removed = requested < groups[index].Quantity ? requested : groups[index].Quantity;
        if (removed <= 0)
            return [];

        var events = new List<GameEvent> { new ItemsRemoved(character, reference.Item, removed.ToInt32RoundedAwayFromZero()) };
        if (index > 0)
        {
            var remaining = groups.Select((group, position) => position == index
                    ? group with { Quantity = group.Quantity - removed }
                    : group)
                .Where(group => group.Quantity > 0)
                .Select(group => group.ToSnapshot())
                .ToArray();
            events.Add(new ItemGroupsRestored(character, reference.Item, Array.AsReadOnly(remaining)));
        }

        return events;
    }

    public static IEnumerable<GameEvent> SetItemProperty(IGameStateView state, EntityId character, ItemId item,
        string property, ItemPropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!state.World.Items.TryGetValue(item, out var definition))
            throw new KeyNotFoundException($"Unknown item '{item}'.");
        if (definition.Divisible)
            throw new InvalidOperationException($"Divisible item '{item}' has no individual copies.");
        if (!definition.CustomProperties.TryGetValue(property, out var declaredValue))
            throw new KeyNotFoundException($"Item '{item}' has no declared property '{property}'.");

        var snapshot = ItemPropertySnapshot.From(property, value);
        if (ItemPropertySnapshot.KindOf(declaredValue) != snapshot.Kind)
            throw new ArgumentException($"Item property '{property}' must keep its declared type.", nameof(value));
        var groups = state.GetItemGroups(character, item);
        if (groups.Count == 0)
            return [];
        var current = groups[0].Properties.GetValueOrDefault(property, declaredValue);
        return current == value ? [] :
            [new ItemPropertyChanged(character, item, snapshot.Key, snapshot.Kind, snapshot.Value)];
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
