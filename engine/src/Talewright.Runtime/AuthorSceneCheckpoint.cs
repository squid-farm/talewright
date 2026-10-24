using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Model;

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
    private readonly AuthorCommand[] _commands;
    private readonly bool _captureDecisions;
    private readonly IReadOnlyList<string> _triggerEdges;
    private readonly AuthorPlayedChoice[] _playedChoices;

    internal AuthorSceneCheckpoint(StoryDefinition story, SceneId start, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events,
        bool captureDecisions, AuthorPlayedChoice[] playedChoices)
    {
        _story = story;
        _start = start;
        _setup = setup;
        _character = character;
        _commands = commands;
        _captureDecisions = captureDecisions;
        _triggerEdges = boundary.TriggerEdges;
        _playedChoices = playedChoices;
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
    public AuthorSession Reenter() => AuthorSession.Replay(_story, _start, _setup, Seed, _character, _commands, _captureDecisions);

    internal void VerifyPlayedChoice(int index, AuthorPlayedChoice choice) =>
        AuthorReloadComparison.Require(index < _playedChoices.Length
            && AuthorReloadComparison.Equal(_playedChoices[index].Scene, choice.Scene)
            && AuthorReloadComparison.Equal(_playedChoices[index].Choice.Label, choice.Choice.Label)
            && _playedChoices[index].Choice.Key == choice.Choice.Key
            && AuthorReloadComparison.Equal(_playedChoices[index].Storylet, choice.Storylet),
            $"previously consumed scene/choice meaning changed at '{choice.Scene.Id}'");

    internal void VerifyBoundary(SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events)
    {
        AuthorReloadComparison.Require(boundary.Scene == Scene && boundary.EventCount == Events.Count
            && AuthorReloadComparison.Equal(Events, events), "pre-entry history/state or RNG position diverged");
        AuthorReloadComparison.Require(AuthorReloadComparison.Equal(_triggerEdges, boundary.TriggerEdges),
            "pre-entry trigger-edge memory diverged");
        AuthorReloadComparison.Require(AuthorReloadComparison.Equal(PendingNotes, boundary.Pending),
            "pre-entry pending consequences diverged");
    }
}

internal sealed record AuthorPlayedChoice(SceneDefinition Scene, ChoiceView Choice, AuthorStoryletChoice? Storylet)
{
    public static AuthorPlayedChoice Capture(StoryDefinition story, SceneDefinition scene, ChoiceView choice)
    {
        AuthorStoryletChoice? selected = null;
        if (choice.Key.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal))
        {
            var target = story.GetScene(new(choice.Key[StorySession.StoryletKeyPrefix.Length..]));
            selected = new(target.Id, target.Title, target.Requires, target.Tags.ToArray(), target.Priority, target.Once);
        }
        return new(scene, choice, selected);
    }
}

internal sealed record AuthorStoryletChoice(SceneId Id, string? Title, Talewright.Rules.Expressions.Expr? Requires,
    IReadOnlyList<string> Tags, int Priority, bool Once);

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
    public string GetTextVariable(VarId variable) => state.GetTextVariable(variable);
    public int VisitCount(SceneId scene) => state.VisitCount(scene);
    public bool HasFiredTrigger(string triggerId) => state.HasFiredTrigger(triggerId);
}
