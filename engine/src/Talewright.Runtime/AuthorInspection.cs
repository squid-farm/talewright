using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

public sealed record AuthorStatInspection(StatId Id, string Name, int BaseValue, int EffectiveValue);
public sealed record AuthorItemInspection(ItemId Id, string Name, int Quantity);
public sealed record AuthorCharacterInspection(EntityId Id, string FirstName, string LastName, Pronouns Pronouns,
    IReadOnlyList<AuthorStatInspection> Stats, IReadOnlyList<AuthorItemInspection> Inventory);
public sealed record AuthorFactionInspection(FactionId Id, string Name, int Standing, string? Tier);
public sealed record AuthorSceneInspection(SceneId Id, string? Title, int Visits);
public sealed record AuthorTriggerInspection(string Id, bool HasFired);

/// <summary>
/// A stable read-only author snapshot. Decisions describe past evaluations at event offsets,
/// not hypothetical evaluations against the snapshot's final state.
/// </summary>
public sealed class AuthorInspection
{
    internal AuthorInspection(StoryDefinition story, IGameStateView state, IReadOnlyList<GameEvent> events,
        IReadOnlyList<RuleDecision> decisions, bool decisionsCaptured)
    {
        Seed = state.Seed;
        RollCount = state.RollCount;
        CurrentScene = state.CurrentScene;
        IsEnded = state.IsEnded;
        DecisionsCaptured = decisionsCaptured;
        Events = events;
        Decisions = decisions;
        Characters = Array.AsReadOnly(story.World.Characters.Keys.Select(id =>
        {
            var identity = state.GetCharacterDefinition(id);
            var stats = story.World.Ruleset.Stats.Select(stat => new AuthorStatInspection(stat.Id, stat.Name,
                state.GetBaseStat(id, stat.Id), state.GetStat(id, stat.Id))).ToArray();
            var inventory = state.GetInventory(id).Select(item => new AuthorItemInspection(item.Key,
                story.World.Items[item.Key].Name, item.Value)).ToArray();
            return new AuthorCharacterInspection(id, identity.FirstName, identity.LastName, identity.Pronouns,
                Array.AsReadOnly(stats), Array.AsReadOnly(inventory));
        }).ToArray());
        Factions = Array.AsReadOnly(story.World.Factions.Values.Select(faction =>
        {
            var standing = state.GetStanding(faction.Id);
            return new AuthorFactionInspection(faction.Id, faction.Name, standing, state.Ruleset.TierFor(standing)?.Name);
        }).ToArray());
        Flags = Array.AsReadOnly(story.World.Flags.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new KeyValuePair<FlagId, bool>(id, state.GetFlag(id))).ToArray());
        Variables = Array.AsReadOnly(story.World.Variables.Keys.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new KeyValuePair<VarId, int>(id, state.GetVariable(id))).ToArray());
        Scenes = Array.AsReadOnly(story.Scenes.Select(scene =>
            new AuthorSceneInspection(scene.Id, scene.Title, state.VisitCount(scene.Id))).ToArray());
        Triggers = Array.AsReadOnly(story.Triggers.Select(trigger =>
            new AuthorTriggerInspection(trigger.Id, state.HasFiredTrigger(trigger.Id))).ToArray());
    }

    public ulong Seed { get; }
    public int RollCount { get; }
    public SceneId? CurrentScene { get; }
    public bool IsEnded { get; }
    public bool DecisionsCaptured { get; }
    public IReadOnlyList<GameEvent> Events { get; }
    public IReadOnlyList<RuleDecision> Decisions { get; }
    public IReadOnlyList<AuthorCharacterInspection> Characters { get; }
    public IReadOnlyList<AuthorFactionInspection> Factions { get; }
    public IReadOnlyList<KeyValuePair<FlagId, bool>> Flags { get; }
    public IReadOnlyList<KeyValuePair<VarId, int>> Variables { get; }
    public IReadOnlyList<AuthorSceneInspection> Scenes { get; }
    public IReadOnlyList<AuthorTriggerInspection> Triggers { get; }

    public IEnumerable<RuleDecision> FindDecisions(string query = "", DecisionKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Decisions.Where(decision => (kind is null || decision.Kind == kind) &&
            (decision.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             (decision.Scene?.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    public IEnumerable<AuthorCharacterInspection> FindCharacters(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Characters.Where(character => character.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            character.FirstName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            character.LastName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
