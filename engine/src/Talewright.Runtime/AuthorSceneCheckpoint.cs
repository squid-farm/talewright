using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Diagnostics;
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
    private readonly AuthorSceneEntryReplay[] _entryReplay;
    private readonly AuthorContinuation? _continuation;
    private readonly AuthorCommand[] _entryCommands;
    private readonly RuleDecision[] _decisions;

    internal AuthorSceneCheckpoint(StoryDefinition story, SceneId start, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events,
        bool captureDecisions, AuthorPlayedChoice[] playedChoices, AuthorSceneEntryReplay[] entryReplay,
        AuthorContinuation? continuation, AuthorCommand[] entryCommands, RuleDecision[] decisions)
    {
        _story = story;
        _start = start;
        _setup = setup;
        _character = character;
        _commands = commands;
        _captureDecisions = captureDecisions;
        _triggerEdges = boundary.TriggerEdges;
        _playedChoices = playedChoices;
        _entryReplay = entryReplay;
        _continuation = continuation;
        _entryCommands = entryCommands;
        _decisions = decisions;
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
    public AuthorSession Reenter() => AuthorSession.Replay(_story, _start, _setup, Seed, _character,
        _commands, _captureDecisions, _continuation);

    internal AuthorContinuation ContinueInPlace() => new(Scene, Seed, Events, _triggerEdges, PendingNotes,
        _setup, _character, _entryCommands, _decisions);

    internal void VerifyPlayedChoice(int index, AuthorPlayedChoice choice)
    {
        var matchesReplayIdentity = index < _playedChoices.Length
            && SameReplayIdentity(_playedChoices[index].Choice.ReplayIdentity, choice.Choice.ReplayIdentity);
        AuthorReloadComparison.Require(index < _playedChoices.Length
            && _playedChoices[index].Scene.Id == choice.Scene.Id
            && _playedChoices[index].Choice.Key == choice.Choice.Key
            && matchesReplayIdentity
            && _playedChoices[index].Storylet?.Id == choice.Storylet?.Id,
            $"previously consumed scene/choice meaning changed at '{choice.Scene.Id}'");
    }

    internal void VerifyEntryReplay(int index, SceneId scene, IReadOnlyList<AuthorEntryReplayStep> replay)
    {
        if (index < _entryReplay.Length)
        {
            var expected = _entryReplay[index];
            AuthorReloadComparison.Require(expected.Scene == scene && SameEntryReplay(expected.Steps, replay),
                $"previously entered scene meaning changed at '{scene}'");
            return;
        }

        AuthorReloadComparison.Require(index == _entryReplay.Length && scene == Scene,
            $"the replay no longer reaches the displayed scene '{Scene}'");
    }

    internal void VerifyEntryReplayCount(int count) =>
        AuthorReloadComparison.Require(count == _entryReplay.Length + 1,
            "the replay no longer follows the same scene-entry history");

    private static bool SameEntryReplay(IReadOnlyList<AuthorEntryReplayStep> left,
        IReadOnlyList<AuthorEntryReplayStep> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind
            && AuthorReloadComparison.Equal(pair.First.Value, pair.Second.Value));

    private static bool SameReplayIdentity(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right)
    {
        if (left is not { } leftIdentity || right is not { } rightIdentity)
        {
            return left is null && right is null;
        }

        return leftIdentity.Span.SequenceEqual(rightIdentity.Span);
    }

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

internal sealed record AuthorContinuation(SceneId Scene, ulong Seed, IReadOnlyList<GameEvent> Events,
    IReadOnlyList<string> TriggerEdges, IReadOnlyList<RenderedBlock> PendingNotes, AuthorSetup Setup,
    CharacterSelection? Character, IReadOnlyList<AuthorCommand> EntryCommands, IReadOnlyList<RuleDecision> Decisions);

internal sealed record AuthorEntryReplayStep(string Kind, object? Value);
internal sealed record AuthorSceneEntryReplay(SceneId Scene, IReadOnlyList<AuthorEntryReplayStep> Steps);

internal sealed record AuthorPlayedChoice(SceneDefinition Scene, ChoiceView Choice,
    AuthorStoryletChoice? Storylet)
{
    public static AuthorPlayedChoice Capture(StoryDefinition story, SceneDefinition scene, ChoiceView choice)
    {
        AuthorStoryletChoice? selected = null;
        if (choice.Key.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal))
        {
            var target = story.GetScene(new(choice.Key[StorySession.StoryletKeyPrefix.Length..]));
            selected = new(target.Id);
        }
        return new(scene, choice, selected);
    }
}

internal sealed record AuthorStoryletChoice(SceneId Id);

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
