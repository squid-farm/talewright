using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

/// <summary>
/// Synthetic, headless scene playtesting. Successful candidates replace the active run atomically.
/// No player save format or compatibility policy is changed.
/// </summary>
public sealed class AuthorSession
{
    private StorySession _session;
    private AuthorCommand[] _commands;
    private ReplayCursor _timeline;
    private readonly List<AuthorPlayedChoice> _playedChoices = [];
    private IReadOnlyList<AuthorCommandResult> _results = [];
    private AuthorContinuation? _continuation;

    private AuthorSession(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, ReplayCursor timeline, bool captureDecisions,
        AuthorSceneCheckpoint? verify = null, AuthorContinuation? continuation = null)
    {
        Story = story;
        StartScene = continuation?.Scene ?? target;
        Setup = (continuation?.Setup ?? setup).Validate(story);
        Character = CharacterSelection.Validate(story, continuation?.Character ?? character, seed);
        _continuation = continuation;
        _commands = commands;
        if (timeline.Length != commands.Length)
        {
            throw new ArgumentException("The author timeline length must match its command history.", nameof(timeline));
        }
        _timeline = timeline;
        CaptureDecisions = captureDecisions;
        var decisions = new List<RuleDecision>(continuation?.Decisions ?? []);
        var boundaryDecisionCount = 0;
        var results = new List<AuthorCommandResult>();
        var entryReplay = new List<AuthorSceneEntryReplay>();
        var appliedCommands = commands.Take(_timeline.Position).ToArray();
        var lastEntryCommand = Array.FindLastIndex(appliedCommands,
            command => command is AuthorChoice or AuthorJump);
        var verifyNextEntry = lastEntryCommand < 0;

        void VerifyEntry(SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events)
        {
            boundaryDecisionCount = decisions.Count;
            if (verifyNextEntry)
            {
                verify?.VerifyBoundary(boundary, events);
            }
        }

        void CaptureEntryReplay(SceneId scene, IReadOnlyList<AuthorEntryReplayStep> replay)
        {
            verify?.VerifyEntryReplay(entryReplay.Count, scene, replay);
            entryReplay.Add(new(scene, replay.ToArray()));
        }

        if (continuation is null)
        {
            _session = new StorySession(story, seed, Character, Setup,
                captureDecisions ? decisions.Add : null, VerifyEntry, CaptureEntryReplay);
            _session.StartAt(target);
        }
        else
        {
            _session = StorySession.Continue(story, seed, Character, continuation.Events, continuation.TriggerEdges,
                captureDecisions ? decisions.Add : null, VerifyEntry, CaptureEntryReplay);
            _session.ContinueAt(continuation.Scene, continuation.PendingNotes);
            foreach (var command in continuation.EntryCommands)
            {
                ReplayCommand(command);
            }
        }
        for (var i = 0; i < appliedCommands.Length; i++)
        {
            var command = appliedCommands[i];
            if (i <= lastEntryCommand)
            {
                VerifyConsumedCommand(i, command, verify);
            }
            verifyNextEntry = i == lastEntryCommand;
            results.Add(ReplayCommand(command));
        }
        verify?.VerifyEntryReplayCount(entryReplay.Count);
        _results = results.AsReadOnly();
        Events = Array.AsReadOnly(_session.Events.ToArray());
        State = new AuthorStateView(EventLog.Replay(story.World, seed, Events).State);
        Decisions = decisions.AsReadOnly();
        if (_session.EntryBoundary is { } boundary)
        {
            if (entryReplay.Count == 0 || entryReplay[^1].Scene != boundary.Scene)
            {
                throw new InvalidOperationException("The current scene entry has no replay evidence.");
            }
            var entryCommands = lastEntryCommand < 0
                ? (continuation?.EntryCommands ?? []).Concat(appliedCommands).ToArray()
                : appliedCommands.Skip(lastEntryCommand + 1).ToArray();
            Checkpoint = new AuthorSceneCheckpoint(story, StartScene, Setup, seed, Character,
                appliedCommands, boundary, Events, captureDecisions, _playedChoices.ToArray(),
                entryReplay.Take(entryReplay.Count - 1).ToArray(), continuation, entryCommands,
                decisions.Take(boundaryDecisionCount).ToArray());
        }
    }

    private void VerifyConsumedCommand(int index, AuthorCommand command, AuthorSceneCheckpoint? checkpoint)
    {
        var scene = Story.GetScene(Current.Scene!.Value);
        ChoiceView selected;
        switch (command)
        {
            case AuthorChoice choice:
                selected = Current.Choices.FirstOrDefault(view => view.Key == choice.Key)
                    ?? throw new AuthorReloadException($"previously consumed choice '{choice.Key}' is unavailable at '{scene.Id}'");
                break;
            case AuthorJump jump:
                AuthorReloadComparison.Require(Story.Scenes.Any(candidate => candidate.Id == jump.Target),
                    $"previously jumped-to scene '{jump.Target}' was removed");
                selected = new ChoiceView($"author-jump:{jump.Target}", []);
                break;
            case AuthorEdit:
                selected = new ChoiceView("author-edit", []);
                break;
            default:
                throw new NotSupportedException($"Unknown author command '{command.GetType().Name}'.");
        }
        var played = AuthorPlayedChoice.Capture(Story, scene, selected);
        checkpoint?.VerifyPlayedChoice(index, played);
        _playedChoices.Add(played);
    }

    private AuthorCommandResult ReplayCommand(AuthorCommand command)
    {
        var eventStart = _session.Events.Count;
        IReadOnlyList<AuthorValueChange> changes = [];
        switch (command)
        {
            case AuthorChoice choice:
                _session.Choose(choice.Key);
                break;
            case AuthorEdit edit:
                changes = ReplayEdit(edit);
                break;
            case AuthorJump jump:
                _session.JumpAuthor(jump.Target);
                break;
            default:
                throw new NotSupportedException($"Unknown author command '{command.GetType().Name}'.");
        }
        var rawEvents = _session.Events.Skip(eventStart).ToArray();
        var before = EventLog.Replay(Story.World, _session.Seed, _session.Events.Take(eventStart)).State;
        var events = Array.AsReadOnly(AuthorInspection.VisibleEvents(Story, before, rawEvents)
            .Select(entry => entry.Event).ToArray());
        return new AuthorCommandResult(eventStart, events, changes);
    }

    private IReadOnlyList<AuthorValueChange> ReplayEdit(AuthorEdit edit)
    {
        var values = edit.Values.Validate(Story);
        var before = values.ReadValues(_session.State).ToDictionary();
        _session.EditAuthorState(values);
        var changes = values.ReadValues(_session.State)
            .Select(value => new AuthorValueChange(value.Key, before[value.Key], value.Value))
            .ToArray();
        return Array.AsReadOnly(changes);
    }

    public StoryDefinition Story { get; }
    public SceneId StartScene { get; private set; }
    /// <summary>Requested setup; inspect Checkpoint.State for actual clamped values before entry.</summary>
    public AuthorSetup Setup { get; private set; }
    public CharacterSelection? Character { get; private set; }
    public bool IsSynthetic => true;
    public bool CaptureDecisions { get; }
    internal bool HasContinuation => _continuation is not null;
    public IReadOnlyList<AuthorCommand> Commands => Array.AsReadOnly(_commands);
    public int Cursor => _timeline.Position;
    public bool CanBack => _timeline.CanBack;
    public bool CanForward => _timeline.CanForward;
    /// <summary>Applied command deltas, including all trigger/leveling consequences; redo commands have no result yet.</summary>
    public IReadOnlyList<AuthorCommandResult> Results => _results;
    /// <summary>Historical decisions at their event offsets; empty when observation was not enabled.</summary>
    public IReadOnlyList<RuleDecision> Decisions { get; private set; }
    public ulong Seed => _session.Seed;
    public IGameStateView State { get; private set; }
    public IReadOnlyList<GameEvent> Events { get; private set; }
    public StoryFrame Current => _session.Current!;
    /// <summary>Null after a transition that ends without entering another scene.</summary>
    public AuthorSceneCheckpoint? Checkpoint { get; private set; }

    public static AuthorSession Create(StoryDefinition story, SceneId target, AuthorSetup? setup = null,
        ulong? seed = null, CharacterSelection? character = null)
        => Create(story, target, captureDecisions: false, setup, seed, character);

    public static AuthorSession Create(StoryDefinition story, SceneId target, bool captureDecisions,
        AuthorSetup? setup = null, ulong? seed = null, CharacterSelection? character = null)
    {
        ArgumentNullException.ThrowIfNull(story);
        story.GetScene(target);
        var resolvedSeed = DeterministicDice.ResolveSeed(seed, story.Seed);
        var selection = CharacterSelection.Validate(story, character, resolvedSeed);
        var validated = (setup ?? new AuthorSetup()).Validate(story);
        return new AuthorSession(story, target, validated,
            resolvedSeed, selection, [], new ReplayCursor(0, 0), captureDecisions);
    }

    /// <summary>Snapshots declared state and recorded decisions; never reevaluates branch predicates.</summary>
    public AuthorInspection Inspect() => new(Story, State, Events, Decisions, CaptureDecisions);

    /// <summary>
    /// Reconstructs a separate candidate, verifies schema, consumed scenes and the pre-entry boundary,
    /// then enters the displayed scene once. Never changes this session or player save compatibility.
    /// </summary>
    public AuthorSession Reload(StoryDefinition candidate) => Reload(candidate, out _);

    public AuthorSession Reload(StoryDefinition candidate, out bool historyChanged)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        historyChanged = false;
        var checkpoint = Checkpoint ?? throw new AuthorReloadException("no current scene-entry boundary");
        var commands = _commands.Take(_timeline.Position).ToArray();
        var timeline = new ReplayCursor(commands.Length, commands.Length);
        try
        {
            AuthorReloadComparison.Schema(Story, candidate);
            AuthorReloadComparison.Require(candidate.Scenes.Any(scene => scene.Id == checkpoint.Scene),
                $"the current scene '{checkpoint.Scene}' was removed");
            AuthorReloadComparison.Require(candidate.Scenes.Any(scene => scene.Id == StartScene),
                $"the replay starting scene '{StartScene}' was removed");
            var result = new AuthorSession(candidate, StartScene, Setup.Validate(candidate), Seed,
                CharacterSelection.Validate(candidate, Character, Seed), commands, timeline, CaptureDecisions,
                checkpoint, _continuation);
            AuthorReloadComparison.Require(result.Checkpoint?.Scene == checkpoint.Scene,
                "the replay no longer reaches the displayed scene");
            return result;
        }
        catch (AuthorReloadException)
        {
            AuthorReloadComparison.Continuation(Story, candidate, State, checkpoint.Scene);
            var continuation = checkpoint.ContinueInPlace(candidate);
            var result = new AuthorSession(candidate, continuation.Scene, continuation.Setup.Validate(candidate),
                continuation.Seed, CharacterSelection.Validate(candidate, continuation.Character, continuation.Seed),
                [], new ReplayCursor(0, 0), CaptureDecisions, continuation: continuation);
            historyChanged = true;
            return result;
        }
    }

    /// <summary>Conservative declaration/rule compatibility check, also usable before a run starts.</summary>
    public static void ValidateReloadSchema(StoryDefinition original, StoryDefinition candidate) =>
        AuthorReloadComparison.Schema(original, candidate);

    /// <summary>Validates that only state declarations changed, which is safe before a playtest is active.</summary>
    public static void ValidateSetupReloadSchema(StoryDefinition original, StoryDefinition candidate) =>
        AuthorReloadComparison.SetupSchema(original, candidate);

    /// <summary>Rebuilds a separate candidate; failed choices leave the active run unchanged.</summary>
    public void Choose(string key)
    {
        if (!Current.Choices.Any(c => c.Key == key))
        {
            throw new InvalidOperationException($"'{key}' is not an available choice.");
        }
        Append(new AuthorChoice(key));
    }

    /// <summary>Applies a validated absolute-value batch, then triggers/leveling and safe presentation refresh.</summary>
    public void Edit(AuthorSetup values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Append(new AuthorEdit(values.Validate(Story)));
    }

    /// <summary>Bypasses target eligibility while preserving state and trigger memory.</summary>
    public void Jump(SceneId target)
    {
        Story.GetScene(target);
        Append(new AuthorJump(target));
    }

    public void Back()
    {
        if (!CanBack) { throw new InvalidOperationException("No earlier author command."); }
        MoveTo(_timeline.Back());
    }

    public void Forward()
    {
        if (!CanForward) { throw new InvalidOperationException("No redo author command."); }
        MoveTo(_timeline.Forward());
    }

    private void MoveTo(ReplayCursor timeline) => Adopt(new AuthorSession(Story, StartScene, Setup, Seed, Character,
        _commands, timeline, CaptureDecisions, continuation: _continuation));

    private void Append(AuthorCommand command)
    {
        if (Cursor >= StoryHistory.MaxChoices)
        {
            throw new InvalidOperationException($"An author timeline supports at most {StoryHistory.MaxChoices:N0} commands.");
        }
        AuthorCommand[] commands = [.. _commands.Take(_timeline.Position), command];
        Adopt(new AuthorSession(Story, StartScene, Setup, Seed, Character, commands,
            _timeline.AfterAppend(), CaptureDecisions, continuation: _continuation));
    }

    /// <summary>Starts a fresh synthetic candidate, not a preserve-current-state jump.</summary>
    public void JumpFresh(SceneId target, AuthorSetup? setup = null, ulong? seed = null,
        CharacterSelection? character = null)
        => Adopt(Create(Story, target, CaptureDecisions, setup, seed, character));

    internal static AuthorSession Replay(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, bool captureDecisions = false,
        AuthorContinuation? continuation = null)
        => new(story, target, setup, seed, character, commands,
            new ReplayCursor(commands.Length, commands.Length), captureDecisions, continuation: continuation);

    internal static AuthorSession RestoreTimeline(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, int cursor, bool captureDecisions)
        => new(story, target, setup, seed, character, commands,
            new ReplayCursor(cursor, commands.Length), captureDecisions);

    private void Adopt(AuthorSession candidate)
    {
        _session = candidate._session;
        StartScene = candidate.StartScene;
        Setup = candidate.Setup;
        Character = candidate.Character;
        State = candidate.State;
        Events = candidate.Events;
        Checkpoint = candidate.Checkpoint;
        Decisions = candidate.Decisions;
        _commands = candidate._commands;
        _timeline = candidate._timeline;
        _results = candidate._results;
        _continuation = candidate._continuation;
        _playedChoices.Clear();
        _playedChoices.AddRange(candidate._playedChoices);
    }
}
