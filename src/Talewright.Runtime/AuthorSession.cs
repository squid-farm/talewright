using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Rules.Diagnostics;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

/// <summary>
/// Synthetic, headless scene playtesting. Successful candidates replace the active run atomically.
/// No player save format or compatibility policy is changed.
/// </summary>
public sealed class AuthorSession
{
    private StorySession _session;
    private AuthorCommand[] _commands;
    private readonly List<AuthorPlayedChoice> _playedChoices = [];
    private IReadOnlyList<AuthorCommandResult> _results = [];

    private AuthorSession(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, int cursor, bool captureDecisions, AuthorSceneCheckpoint? verify = null)
    {
        Story = story;
        StartScene = target;
        Setup = setup;
        Character = character;
        _commands = commands;
        Cursor = cursor;
        CaptureDecisions = captureDecisions;
        var decisions = new List<RuleDecision>();
        var results = new List<AuthorCommandResult>();
        var appliedCommands = commands.Take(cursor).ToArray();
        var lastEntryCommand = Array.FindLastIndex(appliedCommands,
            command => command is AuthorChoice or AuthorJump);
        var verifyNextEntry = lastEntryCommand < 0;

        void VerifyEntry(SceneEntryBoundary boundary, IReadOnlyList<GameEvent> events)
        {
            if (verifyNextEntry)
            {
                verify?.VerifyBoundary(boundary, events);
            }
        }

        _session = new StorySession(story, seed, character, setup,
            captureDecisions ? decisions.Add : null, verify is null ? null : VerifyEntry);
        _session.StartAt(target);
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
        _results = results.AsReadOnly();
        Events = Array.AsReadOnly(_session.Events.ToArray());
        State = new AuthorStateView(EventLog.Replay(story.World, seed, Events).State);
        Decisions = decisions.AsReadOnly();
        if (_session.EntryBoundary is { } boundary)
        {
            Checkpoint = new AuthorSceneCheckpoint(story, target, setup, seed, character,
                appliedCommands, boundary, Events, captureDecisions, _playedChoices.ToArray());
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
        var events = Array.AsReadOnly(_session.Events.Skip(eventStart).ToArray());
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
    public IReadOnlyList<AuthorCommand> Commands => Array.AsReadOnly(_commands);
    public int Cursor { get; private set; }
    public bool CanBack => Cursor > 0;
    public bool CanForward => Cursor < _commands.Length;
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
        var selection = CharacterSelection.Validate(story, character);
        var validated = (setup ?? new AuthorSetup()).Validate(story);
        return new AuthorSession(story, target, validated,
            seed ?? story.Seed ?? DeterministicDice.NewSeed(), selection, [], 0, captureDecisions);
    }

    /// <summary>Snapshots declared state and recorded decisions; never reevaluates branch predicates.</summary>
    public AuthorInspection Inspect() => new(Story, State, Events, Decisions, CaptureDecisions);

    /// <summary>
    /// Reconstructs a separate candidate, verifies schema, consumed scenes and the pre-entry boundary,
    /// then enters the displayed scene once. Never changes this session or player save compatibility.
    /// </summary>
    public AuthorSession Reload(StoryDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (CanForward)
        {
            throw new InvalidOperationException("Automatic reload postponed: the author timeline has redo commands. Move Forward to its end and Rescan, or explicitly start a fresh scene. No commands were dropped.");
        }
        var checkpoint = Checkpoint ?? throw new AuthorReloadException("no current scene-entry boundary");
        AuthorReloadComparison.Schema(Story, candidate);
        candidate.GetScene(checkpoint.Scene);
        var result = new AuthorSession(candidate, StartScene, Setup.Validate(candidate), Seed,
            CharacterSelection.Validate(candidate, Character), _commands, Cursor, CaptureDecisions, checkpoint);
        AuthorReloadComparison.Require(result.Checkpoint?.Scene == checkpoint.Scene, "the replay no longer reaches the displayed scene");
        return result;
    }

    /// <summary>Conservative declaration/rule compatibility check, also usable before a run starts.</summary>
    public static void ValidateReloadSchema(StoryDefinition original, StoryDefinition candidate) =>
        AuthorReloadComparison.Schema(original, candidate);

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

    /// <summary>Bypasses target eligibility, preserving state, trigger memory and the RNG cursor.</summary>
    public void Jump(SceneId target)
    {
        Story.GetScene(target);
        Append(new AuthorJump(target));
    }

    public void Back()
    {
        if (!CanBack) { throw new InvalidOperationException("No earlier author command."); }
        MoveTo(Cursor - 1);
    }

    public void Forward()
    {
        if (!CanForward) { throw new InvalidOperationException("No redo author command."); }
        MoveTo(Cursor + 1);
    }

    private void MoveTo(int cursor) => Adopt(new AuthorSession(Story, StartScene, Setup, Seed, Character,
        _commands, cursor, CaptureDecisions));

    private void Append(AuthorCommand command)
    {
        if (Cursor >= StoryHistory.MaxChoices)
        {
            throw new InvalidOperationException($"An author timeline supports at most {StoryHistory.MaxChoices:N0} commands.");
        }
        AuthorCommand[] commands = [.. _commands.Take(Cursor), command];
        Adopt(new AuthorSession(Story, StartScene, Setup, Seed, Character, commands, commands.Length, CaptureDecisions));
    }

    /// <summary>Starts a fresh synthetic candidate, not a preserve-current-state jump.</summary>
    public void JumpFresh(SceneId target, AuthorSetup? setup = null, ulong? seed = null,
        CharacterSelection? character = null)
        => Adopt(Create(Story, target, CaptureDecisions, setup, seed, character));

    internal static AuthorSession Replay(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, AuthorCommand[] commands, bool captureDecisions = false)
        => new(story, target, setup, seed, character, commands, commands.Length, captureDecisions);

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
        Cursor = candidate.Cursor;
        _results = candidate._results;
        _playedChoices.Clear();
        _playedChoices.AddRange(candidate._playedChoices);
    }
}
