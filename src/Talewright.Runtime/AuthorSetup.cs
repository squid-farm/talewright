using System.Collections.ObjectModel;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

public sealed record AuthorStatValue(EntityId Character, StatId Stat, int Value);
public sealed record AuthorItemValue(EntityId Character, ItemId Item, int Quantity);

/// <summary>Fresh-world prerequisites, applied before trigger priming and target scene entry.</summary>
public sealed record AuthorSetup
{
    public IReadOnlyDictionary<FlagId, bool> Flags { get; init; } = new Dictionary<FlagId, bool>();
    public IReadOnlyDictionary<VarId, int> Variables { get; init; } = new Dictionary<VarId, int>();
    public IReadOnlyDictionary<FactionId, int> Standings { get; init; } = new Dictionary<FactionId, int>();
    public IReadOnlyList<AuthorStatValue> Stats { get; init; } = [];
    public IReadOnlyList<AuthorItemValue> Inventory { get; init; } = [];

    internal AuthorSetup Validate(StoryDefinition story)
    {
        ArgumentNullException.ThrowIfNull(Flags);
        ArgumentNullException.ThrowIfNull(Variables);
        ArgumentNullException.ThrowIfNull(Standings);
        ArgumentNullException.ThrowIfNull(Stats);
        ArgumentNullException.ThrowIfNull(Inventory);
        var world = story.World;
        foreach (var id in Flags.Keys)
        {
            if (!world.Flags.Contains(id))
            {
                throw new ArgumentException($"Unknown setup flag '{id}'.");
            }
        }
        foreach (var id in Variables.Keys)
        {
            if (!world.Variables.ContainsKey(id))
            {
                throw new ArgumentException($"Unknown setup variable '{id}'.");
            }
        }
        foreach (var id in Standings.Keys)
        {
            if (!world.Factions.ContainsKey(id))
            {
                throw new ArgumentException($"Unknown setup faction '{id}'.");
            }
        }
        var stats = new HashSet<(EntityId, StatId)>();
        foreach (var value in Stats)
        {
            if (value is null || !world.Characters.ContainsKey(value.Character) ||
                !world.Ruleset.StatsById.ContainsKey(value.Stat))
            {
                throw new ArgumentException("Every setup stat must name a declared character and ruleset stat.");
            }
            if (!stats.Add((value.Character, value.Stat)))
            {
                throw new ArgumentException($"Duplicate setup stat '{value.Character}:{value.Stat}'.");
            }
        }
        var items = new HashSet<(EntityId, ItemId)>();
        foreach (var value in Inventory)
        {
            if (value is null || !world.Characters.ContainsKey(value.Character) ||
                !world.Items.ContainsKey(value.Item) || value.Quantity < 0)
            {
                throw new ArgumentException("Every setup item must name a declared character and item with a nonnegative quantity.");
            }
            if (!items.Add((value.Character, value.Item)))
            {
                throw new ArgumentException($"Duplicate setup item '{value.Character}:{value.Item}'.");
            }
        }
        return this with
        {
            Flags = new ReadOnlyDictionary<FlagId, bool>(Flags.ToDictionary()),
            Variables = new ReadOnlyDictionary<VarId, int>(Variables.ToDictionary()),
            Standings = new ReadOnlyDictionary<FactionId, int>(Standings.ToDictionary()),
            Stats = Array.AsReadOnly(Stats.OrderBy(v => v.Character.Value, StringComparer.Ordinal)
                .ThenBy(v => v.Stat.Value, StringComparer.Ordinal).ToArray()),
            Inventory = Array.AsReadOnly(Inventory.OrderBy(v => v.Character.Value, StringComparer.Ordinal)
                .ThenBy(v => v.Item.Value, StringComparer.Ordinal).ToArray()),
        };
    }

    internal void Apply(EventLog log)
    {
        foreach (var value in Stats)
        {
            log.AppendRange(StateChanges.SetStat(log.State, value.Character, value.Stat, value.Value));
        }
        foreach (var value in Inventory)
        {
            var current = log.State.GetItemCount(value.Character, value.Item);
            log.AppendRange(value.Quantity >= current
                ? StateChanges.GiveItem(value.Character, value.Item, value.Quantity - current)
                : StateChanges.TakeItem(log.State, value.Character, value.Item, current - value.Quantity));
        }
        foreach (var (id, value) in Standings.OrderBy(v => v.Key.Value, StringComparer.Ordinal))
        {
            log.AppendRange(StateChanges.SetStanding(log.State, id, value));
        }
        foreach (var (id, value) in Flags.OrderBy(v => v.Key.Value, StringComparer.Ordinal))
        {
            log.AppendRange(StateChanges.SetFlag(log.State, id, value));
        }
        foreach (var (id, value) in Variables.OrderBy(v => v.Key.Value, StringComparer.Ordinal))
        {
            log.AppendRange(StateChanges.SetVariable(log.State, id, value));
        }
    }
}
