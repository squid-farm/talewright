using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

/// <summary>
/// Immutable pre-entry evidence plus a replay recipe. Replaying the prefix reconstructs trigger-edge
/// memory and pending actions as well as event-backed state; folding Events alone does not.
/// </summary>
public sealed class AuthorSceneCheckpoint
{
    private readonly StoryDefinition _story;
    private readonly SceneId _start;
    private readonly AuthorSetup _setup;
    private readonly CharacterSelection? _character;
    private readonly string[] _choices;

    internal AuthorSceneCheckpoint(StoryDefinition story, SceneId start, AuthorSetup setup, ulong seed,
        CharacterSelection? character, string[] choices, SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events)
    {
        _story = story;
        _start = start;
        _setup = setup;
        _character = character;
        _choices = choices;
        Scene = boundary.Scene;
        Seed = seed;
        Events = Array.AsReadOnly(events.Take(boundary.EventCount).ToArray());
        PendingNotes = Array.AsReadOnly(boundary.Pending.ToArray());
        State = new AuthorStateView(EventLog.Replay(story.World, seed, Events).State);
    }

    public SceneId Scene { get; }
    public ulong Seed { get; }
    public IReadOnlyList<GameEvent> Events { get; }
    public IGameStateView State { get; }
    public IReadOnlyList<RenderedBlock> PendingNotes { get; }

    /// <summary>
    /// Rebuilds the captured run and enters this scene once, returning an independent candidate.
    /// Requires the same unchanged story and deterministic extensions; this is not cross-source reload.
    /// </summary>
    public AuthorSession Reenter() => AuthorSession.Replay(_story, _start, _setup, Seed, _character, _choices);
}

internal sealed class AuthorStateView(GameState state) : IGameStateView
{
    public WorldDefinition World => state.World;
    public Ruleset Ruleset => state.Ruleset;
    public ulong Seed => state.Seed;
    public int RollCount => state.RollCount;
    public SceneId? CurrentScene => state.CurrentScene;
    public bool IsEnded => state.IsEnded;
    public bool HasCharacter(EntityId id) => state.HasCharacter(id);
    public CharacterDefinition GetCharacterDefinition(EntityId id) => state.GetCharacterDefinition(id);
    public int GetBaseStat(EntityId character, StatId stat) => state.GetBaseStat(character, stat);
    public int GetStat(EntityId character, StatId stat) => state.GetStat(character, stat);
    public int GetItemCount(EntityId character, ItemId item) => state.GetItemCount(character, item);
    public IEnumerable<KeyValuePair<ItemId, int>> GetInventory(EntityId character) => state.GetInventory(character);
    public int GetStanding(FactionId faction) => state.GetStanding(faction);
    public bool GetFlag(FlagId flag) => state.GetFlag(flag);
    public int GetVariable(VarId variable) => state.GetVariable(variable);
    public int VisitCount(SceneId scene) => state.VisitCount(scene);
    public bool HasFiredTrigger(string triggerId) => state.HasFiredTrigger(triggerId);
}
