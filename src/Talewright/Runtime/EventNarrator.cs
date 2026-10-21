using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;

namespace Talewright.Runtime;

/// <summary>
/// Turns engine events into short, presentation-agnostic notes for the player ("Athletics check: 12 + 2 = 14 vs
/// DC 14 — success", "Gained: Relic of Ysh", "Explorers' Guild −5"). Flags, variables, scene entries and other
/// bookkeeping events are deliberately silent.
/// </summary>
public static class EventNarrator
{
    /// <summary>Describes <paramref name="events"/>; <paramref name="state"/> supplies display names.</summary>
    public static IEnumerable<RenderedNote> Describe(IEnumerable<GameEvent> events, IGameStateView state)
    {
        var levelStat = state.Ruleset.LevelingRule?.LevelStat;
        foreach (var e in events)
        {
            var note = e switch
            {
                SkillCheckResolved c => new RenderedNote(NoteKind.Check, DescribeCheck(c, state)),
                StatChanged s when s.Stat != levelStat => new RenderedNote(NoteKind.Change,
                    $"{Who(s.Character, state)}{StatName(s.Stat, state)} {Signed(s.NewValue - s.OldValue)} ({s.NewValue})"),
                ItemsGranted g => new RenderedNote(NoteKind.Change, $"{Who(g.Character, state)}Gained: {Items(g.Item, g.Quantity, state)}"),
                ItemsRemoved r => new RenderedNote(NoteKind.Change, $"{Who(r.Character, state)}Lost: {Items(r.Item, r.Quantity, state)}"),
                StandingChanged f => new RenderedNote(NoteKind.Change, DescribeStanding(f, state)),
                LevelChanged l => new RenderedNote(NoteKind.Change, $"{Who(l.Character, state)}Level {l.NewLevel}!"),
                _ => null,
            };
            if (note is not null)
            {
                yield return note;
            }
        }
    }

    private static string DescribeCheck(SkillCheckResolved c, IGameStateView state)
    {
        var outcome = c.Success ? "success" : "failure";
        return $"{Who(c.Actor, state)}{StatName(c.Skill, state)} check: {c.Roll} {Signed(c.Modifier)} = {c.Total} vs DC {c.Difficulty} — {outcome}";
    }

    private static string DescribeStanding(StandingChanged f, IGameStateView state)
    {
        var name = state.World.Factions.TryGetValue(f.Faction, out var faction) ? faction.Name : f.Faction.Value;
        var text = $"{name} {Signed(f.NewValue - f.OldValue)}";
        var before = state.Ruleset.TierFor(f.OldValue);
        var after = state.Ruleset.TierFor(f.NewValue);
        return before != after && after is not null ? $"{text} (now {after.Name})" : text;
    }

    private static string Who(EntityId character, IGameStateView state) =>
        character == EntityId.Player ? string.Empty : $"{state.GetCharacterDefinition(character).FirstName}: ";

    private static string StatName(StatId stat, IGameStateView state) =>
        state.Ruleset.StatsById.TryGetValue(stat, out var definition) ? definition.Name : stat.Value;

    private static string Items(ItemId item, int quantity, IGameStateView state)
    {
        var name = state.World.Items.TryGetValue(item, out var definition) ? definition.Name : item.Value;
        return quantity == 1 ? name : $"{name} ×{quantity}";
    }

    private static string Signed(int value) => value < 0 ? $"−{-value}" : $"+{value}";
}
