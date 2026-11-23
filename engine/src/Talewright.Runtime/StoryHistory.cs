using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using Talewright.Core.Randomness;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

public sealed record HistoryFrame(StoryFrame Frame, IReadOnlyList<RenderedInline>? Chosen = null)
{
    /// <summary>Whether this choice was accepted automatically by a bare scene divert.</summary>
    public bool ChosenAutomatically { get; init; }
}

internal enum ReplayTransitionKind
{
    SavedChoice,
    StoredAutomaticTransition,
    InsertedAutomaticTransition,
}

/// <summary>Deterministic choice history. Restores are built separately before replacing a live session.</summary>
public sealed partial class StoryHistory
{
    public const int MaxAutomaticTransitions = 1_000;
    public const int MaxSaveBytes = SaveFormat.MaxSaveBytes;
    private const string AutomaticContinuationErrorMessage =
        "Stopped after 1,000 automatic scene transitions to prevent a runaway chain. Check the story for a loop of bare or trigger diverts.";
    private readonly List<string> _choices = [];
    private readonly List<bool> _automaticChoices = [];
    private List<byte[]>? _evidence;
    private List<byte[]>? _replayChoiceIdentities;
    private StorySession _session;
    private List<HistoryFrame> _frames;
    private ReplayCursor _timeline;

    public StoryHistory(StoryDefinition story, ulong? seed = null, CharacterSelection? character = null)
    {
        var resolvedSeed = DeterministicDice.ResolveSeed(seed, story.Seed);
        Character = CharacterSelection.Validate(story, character, resolvedSeed);
        Story = story;
        _timeline = new ReplayCursor(0, 0);
        _session = new StorySession(story, resolvedSeed, character: Character);
        var initialState = ReplayEvidence.InitialState(story, _session.State);
        var eventStart = _session.Events.Count;
        _frames = [new HistoryFrame(_session.Start())];
        TriggerSaveId = TriggerSaveFiredSince(eventStart);
        _evidence = CanRecordEvidence(story) ? [ReplayEvidence.Start(story, _session, Character, initialState)] : null;
        _replayChoiceIdentities = _evidence is null ? null : [];
        ContinueAutomatically();
    }

    private StoryHistory(StoryDefinition story, CharacterSelection? character, StorySession session,
        List<HistoryFrame> frames, IEnumerable<string> choices, ReplayCursor timeline, List<byte[]>? evidence,
        List<byte[]>? replayChoiceIdentities,
        IEnumerable<bool> automaticChoices, string? restoredProgressKey, string? automaticContinuationError)
    {
        Story = story;
        Character = character;
        _session = session;
        _frames = frames;
        _choices.AddRange(choices);
        _automaticChoices.AddRange(automaticChoices);
        _timeline = timeline;
        _evidence = evidence;
        _replayChoiceIdentities = replayChoiceIdentities;
        RestoredProgressKey = restoredProgressKey;
        AutomaticContinuationError = automaticContinuationError;
    }

    public StoryDefinition Story { get; }
    public CharacterSelection? Character { get; }
    public ulong Seed => _session.Seed;
    public IGameStateView State => _session.State;
    public IReadOnlyList<GameEvent> Events => _session.Events;
    public StoryFrame Current => _session.Current!;
    public IReadOnlyList<HistoryFrame> Transcript => _prefix.Count == 0 ? _frames.AsReadOnly() : [.. _prefix, .. _frames];
    public int Cursor => _timeline.Position;
    public int PlaythroughLength => _prefix.Count + Cursor;
    public bool CanBack => Story.UndoEnabled && PreviousPlayerChoice(_timeline.Position) is not null;
    public bool CanForward => Story.UndoEnabled && NextPlayerChoice(_timeline.Position) is not null;
    public ContinuationReport? Continuation { get; private set; }
    /// <summary>The last saving trigger to fire during the latest start or choice step, if any.</summary>
    public string? TriggerSaveId { get; private set; }
    /// <summary>The original slot's progress after a load, until the timeline changes.</summary>
    public string? RestoredProgressKey { get; private set; }
    /// <summary>Why play stopped when an automatic bare-divert chain exceeded its safety budget.</summary>
    public string? AutomaticContinuationError { get; private set; }

    /// <summary>
    /// Forks validated player replay inputs into isolated synthetic author tooling, retaining redo. Each decision
    /// becomes the current choice that replay selected, not the key recorded by an older story revision.
    /// </summary>
    public AuthorSession ForkAuthor(bool captureDecisions = true)
    {
        if (_origin is not null)
        {
            throw new InvalidOperationException("This playthrough continued across a story update, so it can't be opened in the Author Debugger. Use a save made from the start with the current story version.");
        }

        var keys = new List<string?>(_choices.Count);
        ReplayWithAutomaticContinuations(Story, Seed, _choices, _replayChoiceIdentities,
            Character, liveKeys: keys);
        var unavailable = keys.IndexOf(null);
        if (unavailable >= 0)
        {
            throw new InvalidOperationException(
                $"Choice {unavailable + 1} of this playthrough replays a recorded outcome that the current story no longer offers there, so it cannot be forked into an author timeline.");
        }

        return AuthorSession.RestoreTimeline(Story, Story.Start, new AuthorSetup(), Seed, Character,
            keys.Select(key => (AuthorCommand)new AuthorChoice(key!)).ToArray(), Cursor, captureDecisions);
    }

    public void Choose(string key)
    {
        var choice = Current.Choices.FirstOrDefault(c => c.Key == key)
            ?? throw new InvalidOperationException($"'{key}' is not an available choice.");
        var previousFrame = Current;
        var eventStart = _session.Events.Count;
        var frame = _session.Choose(key);
        TriggerSaveId = TriggerSaveFiredSince(eventStart);
        AppendChoice(key, choice, previousFrame, frame, eventStart, automatically: false);
        RestoredProgressKey = null;
        ContinueAutomatically();
    }

    public void Back()
    {
        if (!CanBack)
        {
            throw new InvalidOperationException("There is no previous choice to restore.");
        }

        MoveTo(_timeline.At(PreviousPlayerChoice(_timeline.Position)!.Value));
    }

    public void Forward()
    {
        if (!CanForward)
        {
            throw new InvalidOperationException("There is no next choice to restore.");
        }

        var choice = NextPlayerChoice(_timeline.Position)!.Value;
        var target = choice + 1;
        while (target < _timeline.Length && IsAutomaticChoice(target))
        {
            target++;
        }
        MoveTo(_timeline.At(target));
    }

    private void MoveTo(ReplayCursor timeline)
    {
        var replay = ReplayWithAutomaticContinuations(Story, Seed,
            _choices.Take(timeline.Position).ToArray(),
            _replayChoiceIdentities?.Take(timeline.Position).ToArray(), Character, origin: _origin,
            baseTranscriptLength: _prefix.Count);
        _session = replay.Session;
        _frames = replay.Frames;
        _timeline = new ReplayCursor(replay.Choices.Count, timeline.Length);
        AutomaticContinuationError = replay.AutomaticContinuationError;
        TriggerSaveId = null;
        RestoredProgressKey = null;
    }

    private string? TriggerSaveFiredSince(int eventStart)
    {
        var savingTriggers = Story.Triggers.Where(trigger => trigger.Save)
            .Select(trigger => trigger.Id).ToHashSet(StringComparer.Ordinal);
        return _session.Events.Skip(eventStart).OfType<TriggerFired>()
            .LastOrDefault(trigger => savingTriggers.Contains(trigger.TriggerId))?.TriggerId;
    }

    private void AppendChoice(string key, ChoiceView choice, StoryFrame previousFrame, StoryFrame frame,
        int eventStart, bool automatically)
    {
        if (_evidence is not null && choice.ReplayIdentity is { } identity)
        {
            var next = ReplayEvidence.Next(_evidence[_timeline.Position], previousFrame, identity,
                _session.Events, eventStart);
            _evidence.RemoveRange(_timeline.Position + 1, _evidence.Count - _timeline.Position - 1);
            _replayChoiceIdentities!.RemoveRange(_timeline.Position,
                _replayChoiceIdentities.Count - _timeline.Position);
            _evidence.Add(next);
            _replayChoiceIdentities.Add(identity.ToArray());
        }
        else
        {
            _evidence = null;
            _replayChoiceIdentities = null;
        }

        _frames[^1] = _frames[^1] with { Chosen = choice.Label, ChosenAutomatically = automatically };
        _frames.Add(new HistoryFrame(frame));
        _choices.RemoveRange(_timeline.Position, _timeline.Length - _timeline.Position);
        _automaticChoices.RemoveRange(_timeline.Position, _timeline.Length - _timeline.Position);
        _choices.Add(key);
        _automaticChoices.Add(automatically);
        _timeline = _timeline.AfterAppend();
    }

    private void ContinueAutomatically()
    {
        var transitions = 0;
        while (_session.Current?.AutomaticallyContinue == true)
        {
            if (transitions == MaxAutomaticTransitions)
            {
                AutomaticContinuationError = AutomaticContinuationErrorMessage;
                _session.StopAutomaticContinuation();
                _frames[^1] = _frames[^1] with { Frame = _session.Current! };
                return;
            }

            var previousFrame = Current;
            var choice = previousFrame.Choices.Single(choice => choice.Key == StorySession.ContinueKey);
            var eventStart = _session.Events.Count;
            var frame = _session.Choose(StorySession.ContinueKey);
            TriggerSaveId = TriggerSaveFiredSince(eventStart) ?? TriggerSaveId;
            AppendChoice(StorySession.ContinueKey, choice, previousFrame, frame, eventStart, automatically: true);
            transitions++;
        }
    }

    private int? PreviousPlayerChoice(int position)
    {
        for (var index = position - 1; index >= 0; index--)
        {
            if (!IsAutomaticChoice(index))
            {
                return index;
            }
        }
        return null;
    }

    private int? NextPlayerChoice(int position)
    {
        for (var index = position; index < _timeline.Length; index++)
        {
            if (!IsAutomaticChoice(index))
            {
                return index;
            }
        }
        return null;
    }

    private bool IsAutomaticChoice(int index) => _automaticChoices[index];

    public byte[] Save(SaveSummary? summary = null)
    {
        var data = SaveFormat.Write(ToSavedGame(summary));
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidOperationException($"This playthrough is too large to save (maximum {MaxSaveBytes / 1024 / 1024} MiB). You can continue playing, but this save could not be created.");
        }

        return data;
    }

    /// <summary>Identifies replay progress. Equal for every save of the same playthrough, whatever its details.</summary>
    public string ProgressKey => KeyOf(ToSavedGame(null, progressKeyOnly: true));

    private SavedGame ToSavedGame(SaveSummary? summary, bool progressKeyOnly = false)
    {
        if (string.IsNullOrEmpty(Story.ContentFingerprint))
        {
            throw new InvalidOperationException("Saving is unavailable for this story. Ask the author to enable save compatibility for their custom extensions.");
        }

        if (_choices.Any(c => c.Length > SaveFormat.MaxChoiceKeyLength))
        {
            throw new InvalidOperationException("This story contains a choice identifier that is too long to save. Contact the story author.");
        }

        return new SavedGame
        {
            StoryId = Story.Id,
            StoryVersion = Story.Version,
            ContentFingerprint = Story.ContentFingerprint,
            Seed = Seed,
            Choices = _choices.ToArray(),
            Cursor = Cursor,
            Character = Character,
            Summary = summary,
            ReplayEvidence = _evidence is null ? null : ReplayEvidence.Encode(_evidence[^1], Cursor),
            ReplayChoiceIdentities = _replayChoiceIdentities?.Select(identity => identity.ToArray()).ToArray(),
            Continuation = progressKeyOnly ? ProgressContinuation() : CaptureContinuation(),
            TalemarkVersion = Story.TalemarkVersion,
        };
    }

    private static string KeyOf(SavedGame save) =>
        Convert.ToHexString(SHA256.HashData(SaveFormat.EncodeBody(save with
        {
            Summary = null,
            Continuation = save.Continuation is null ? null : ProgressContinuation(save.Continuation.Payload),
        })));

    public bool TryProgressKey([NotNullWhen(true)] out string? progressKey)
    {
        try
        {
            progressKey = ProgressKey;
            return true;
        }
        catch (InvalidOperationException)
        {
            progressKey = null;
            return false;
        }
    }

    public SaveSummary CaptureSummary(DateTimeOffset savedAt)
    {
        var player = State.GetCharacterDefinition(EntityId.Player);
        var title = string.IsNullOrWhiteSpace(Current.Title)
            ? Current.Scene?.Value ?? (Current.IsEnded ? "The end" : "Untitled scene")
            : Current.Title;
        return new(title,
            savedAt, player.FullName);
    }

    /// <summary>Reads display metadata and replay identity without replaying a saved history.</summary>
    public static SaveEntry DescribeSave(string slot, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxSaveBytes)
        {
            return new(slot, null, null, $"Saved game is empty or exceeds {MaxSaveBytes / 1024 / 1024} MiB.");
        }

        try
        {
            var save = SaveFormat.Read(bytes, out var summaryDamaged);
            return new(slot, save.Summary, KeyOf(save), summaryDamaged ? "Save details are unreadable." : null);
        }
        catch (InvalidDataException)
        {
            return new(slot, null, null, "Saved game is malformed; loading will show the error.");
        }
    }

    private static StoryHistory RestoreByReplay(StoryDefinition story, SavedGame save)
        => RestoreByReplay(story, save, replayTransitionObserved: null);

    private static StoryHistory RestoreByReplay(StoryDefinition story, SavedGame save,
        Action<ReplayTransitionKind>? replayTransitionObserved)
    {
        if (save.StoryId != story.Id)
        {
            throw new InvalidDataException("This save belongs to a different story. Open it with the story and player version that created it.");
        }

        if (string.IsNullOrEmpty(story.ContentFingerprint))
        {
            throw new InvalidDataException("This save is incompatible with the current story or player. Contact the story author.");
        }

        var changed = save.StoryVersion != story.Version || save.ContentFingerprint != story.ContentFingerprint;
        var semanticsChanged = save.TalemarkVersion != story.TalemarkVersion;
        if (semanticsChanged && (save.ReplayEvidence is null || save.ReplayChoiceIdentities is null))
        {
            throw Incompatible(story, save);
        }
        if (changed && (save.ReplayEvidence is null || save.ReplayChoiceIdentities is null
            || save.ReplayChoiceIdentities.Count != save.Choices.Count || !CanRecordEvidence(story)
            || !IsFingerprint(save.ContentFingerprint) || !IsFingerprint(story.ContentFingerprint)
            || save.ContentFingerprint == story.ContentFingerprint))
        {
            throw Incompatible(story, save);
        }

        // Validate redo choices too, not just the visible prefix.
        CharacterSelection? character;
        try
        {
            character = CharacterSelection.Validate(story, save.Character, save.Seed);
        }
        catch (ArgumentException ex)
        {
            if (changed) throw Incompatible(story, save, ex);
            throw new InvalidDataException($"Invalid saved character: {ex.Message}", ex);
        }
        AutomaticReplay replay;
        try
        {
            replay = ReplayWithAutomaticContinuations(story, save.Seed, save.Choices,
                save.ReplayChoiceIdentities, character, recordEvidence: true,
                requestedCursor: save.Cursor, replayTransitionObserved: replayTransitionObserved);
        }
        catch (InvalidDataException ex) when (changed)
        {
            throw Incompatible(story, save, ex);
        }

        if (save.ReplayEvidence is { } savedEvidence
            && (replay.SavedEvidence is null
                || !ReplayEvidence.Matches(savedEvidence, replay.SavedEvidence, save.Cursor)))
        {
            throw ReplayMismatch(story, save, changed);
        }

        var normalizedCursor = replay.CursorPositions[save.Cursor];
        return new StoryHistory(story, character, replay.CursorSession, replay.CursorFrames, replay.Choices,
            new ReplayCursor(normalizedCursor, replay.Choices.Count), replay.Evidence,
            replay.ReplayChoiceIdentities, replay.AutomaticChoices, KeyOf(save),
            replay.CursorAutomaticContinuationError);
    }

    private static bool CanRecordEvidence(StoryDefinition story) =>
        !string.IsNullOrEmpty(story.ContentFingerprint) && !string.IsNullOrEmpty(story.ReplayCompatibilityId);

    private static bool IsFingerprint(string fingerprint) =>
        fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit);

    private static InvalidDataException Incompatible(StoryDefinition story, SavedGame save, Exception? inner = null)
    {
        var version = save.StoryVersion;
        var subject = !string.IsNullOrWhiteSpace(version) && version.Length <= 64 && !version.Any(char.IsControl)
            ? $"Version {version} of {story.Title}" : story.Title;
        return new InvalidDataException($"{subject} has changed, and this save is no longer compatible. Open it with the story and player version that created it.", inner);
    }

    private static InvalidDataException ReplayMismatch(StoryDefinition story, SavedGame save, bool changed) =>
        changed ? Incompatible(story, save) : ReplayFailure();

    private static InvalidDataException ReplayFailure() =>
        new("This saved game cannot continue because the story has changed or the save is damaged. It is no longer compatible.");

    private sealed record AutomaticReplay(
        StorySession Session,
        List<HistoryFrame> Frames,
        List<string> Choices,
        List<bool> AutomaticChoices,
        List<byte[]>? Evidence,
        List<byte[]>? ReplayChoiceIdentities,
        int[] CursorPositions,
        string? AutomaticContinuationError,
        StorySession CursorSession,
        List<HistoryFrame> CursorFrames,
        string? CursorAutomaticContinuationError,
        byte[]? SavedEvidence);

    private static AutomaticReplay ReplayWithAutomaticContinuations(StoryDefinition story, ulong seed,
        IReadOnlyList<string> choices, IReadOnlyList<byte[]>? replayChoiceIdentities,
        CharacterSelection? character, bool recordEvidence = false, List<string?>? liveKeys = null,
        ContinuationCheckpoint? origin = null, int baseTranscriptLength = 0, int? requestedCursor = null,
        Action<ReplayTransitionKind>? replayTransitionObserved = null)
    {
        var session = origin is null ? new StorySession(story, seed, character: character)
            : origin.Resume(story, seed, character);
        var initialState = origin is null ? ReplayEvidence.InitialState(story, session.State) : [];
        var frames = new List<HistoryFrame>(choices.Count + 1) { new(origin is null ? session.Start() : session.Current!) };
        var evidence = recordEvidence && origin is null && CanRecordEvidence(story)
            ? new List<byte[]>(choices.Count + 1) { ReplayEvidence.Start(story, session, character, initialState) } : null;
        byte[]? savedEvidence = evidence is null ? null : evidence[0];
        var replayedIdentities = evidence is null ? null : new List<byte[]>(choices.Count);
        using var evidenceHashContext = evidence is null ? null : ReplayEvidence.CreateHashContext();
        var replayedChoices = new List<string>(choices.Count);
        var automaticChoices = new List<bool>(choices.Count);
        var cursorPositions = new int[choices.Count + 1];
        var savedIndex = 0;
        string? automaticContinuationError = null;
        StorySession? cursorSession = null;
        List<HistoryFrame>? cursorFrames = null;
        string? cursorAutomaticContinuationError = null;

        void AdvanceEvidence(StoryFrame previousFrame, ReadOnlyMemory<byte>? identity,
            int eventStart, bool fromSavedTimeline)
        {
            var previousEvidence = evidence is { Count: > 0 } ? evidence[^1] : null;
            var evidenceMatchesSaved = previousEvidence is not null
                && ReferenceEquals(previousEvidence, savedEvidence);
            byte[]? nextEvidence = null;
            if (identity is { } replayIdentity && evidence is not null && replayedIdentities is not null)
            {
                nextEvidence = ReplayEvidence.Next(evidenceHashContext!, previousEvidence!, previousFrame,
                    replayIdentity, session.Events, eventStart);
                evidence.Add(nextEvidence);
                replayedIdentities.Add(replayIdentity.ToArray());
            }
            else
            {
                evidence = null;
                replayedIdentities = null;
            }

            if (fromSavedTimeline && savedEvidence is not null && identity is { } savedIdentity)
            {
                savedEvidence = evidenceMatchesSaved && nextEvidence is not null
                    ? nextEvidence
                    : ReplayEvidence.Next(savedEvidence, previousFrame, savedIdentity, session.Events, eventStart);
            }
            else if (fromSavedTimeline)
            {
                savedEvidence = null;
            }
        }

        void Record(string key, ChoiceView choice, StoryFrame previousFrame, StoryFrame nextFrame,
            int eventStart, ReadOnlyMemory<byte>? identity, bool automatically,
            bool fromSavedTimeline, ReplayTransitionKind transitionKind)
        {
            AdvanceEvidence(previousFrame, identity, eventStart, fromSavedTimeline);

            frames[^1] = frames[^1] with { Chosen = choice.Label, ChosenAutomatically = automatically };
            frames.Add(new HistoryFrame(nextFrame));
            replayedChoices.Add(key);
            automaticChoices.Add(automatically);
            liveKeys?.Add(key);
            replayTransitionObserved?.Invoke(transitionKind);
        }

        void CaptureRequestedCursor()
        {
            if (requestedCursor is { } cursor && cursor < choices.Count && cursor <= savedIndex
                && cursorPositions[cursor] == replayedChoices.Count && cursorSession is null)
            {
                cursorSession = session.ForkForReplayCursor();
                cursorFrames = [.. frames];
                cursorAutomaticContinuationError = automaticContinuationError;
            }
        }

        bool IsMatchingStoredAutomaticChoice(int index, ReadOnlyMemory<byte>? identity)
        {
            if (choices[index] != StorySession.ContinueKey)
            {
                return false;
            }
            return replayChoiceIdentities is null
                || (identity is { } replayIdentity
                    && replayChoiceIdentities[index].AsSpan().SequenceEqual(replayIdentity.Span));
        }

        void ContinueAutomatically()
        {
            var boundaryStart = savedIndex;
            var transitions = 0;
            while (session.Current?.AutomaticallyContinue == true)
            {
                if (transitions == MaxAutomaticTransitions)
                {
                    if (savedIndex < choices.Count)
                    {
                        throw ReplayFailure();
                    }
                    automaticContinuationError = AutomaticContinuationErrorMessage;
                    session.StopAutomaticContinuation();
                    frames[^1] = frames[^1] with { Frame = session.Current! };
                    return;
                }

                var previousFrame = session.Current!;
                var choice = previousFrame.Choices.Single(choice => choice.Key == StorySession.ContinueKey);
                var storedAutomaticChoice = savedIndex < choices.Count
                    && IsMatchingStoredAutomaticChoice(savedIndex, choice.ReplayIdentity);
                var eventStart = session.Events.Count;
                var nextFrame = session.Choose(StorySession.ContinueKey);
                Record(StorySession.ContinueKey, choice, previousFrame, nextFrame, eventStart,
                    choice.ReplayIdentity, automatically: true, fromSavedTimeline: storedAutomaticChoice,
                    storedAutomaticChoice
                        ? ReplayTransitionKind.StoredAutomaticTransition
                        : ReplayTransitionKind.InsertedAutomaticTransition);
                transitions++;

                if (storedAutomaticChoice)
                {
                    savedIndex++;
                }
                for (var index = boundaryStart; index <= savedIndex; index++)
                {
                    cursorPositions[index] = replayedChoices.Count;
                }
            }
        }

        ContinueAutomatically();
        cursorPositions[savedIndex] = replayedChoices.Count;
        CaptureRequestedCursor();
        while (savedIndex < choices.Count && automaticContinuationError is null)
        {
            ContinueAutomatically();
            cursorPositions[savedIndex] = replayedChoices.Count;
            CaptureRequestedCursor();
            if (automaticContinuationError is not null || savedIndex >= choices.Count)
            {
                break;
            }
            var key = choices[savedIndex];
            var previousFrame = session.Current!;
            var eventStart = session.Events.Count;
            ChoiceView? choice;
            ReadOnlyMemory<byte>? identity;
            if (replayChoiceIdentities is not null)
            {
                if (!session.ChooseReplay(replayChoiceIdentities[savedIndex], out choice) || choice is null)
                {
                    throw ReplayFailure();
                }
                identity = replayChoiceIdentities[savedIndex];
                var selectedKey = choice.Key;
                liveKeys?.Add(previousFrame.Choices.Any(offered => offered.Key == selectedKey) ? selectedKey : null);
            }
            else
            {
                choice = previousFrame.Choices.FirstOrDefault(candidate => candidate.Key == key);
                if (choice is null)
                {
                    throw ReplayFailure();
                }
                identity = choice.ReplayIdentity;
                session.Choose(key);
                liveKeys?.Add(key);
            }

            var next = session.Current!;
            AdvanceEvidence(previousFrame, identity, eventStart, fromSavedTimeline: true);

            frames[^1] = frames[^1] with { Chosen = choice.Label };
            frames.Add(new HistoryFrame(next));
            replayedChoices.Add(key);
            automaticChoices.Add(false);
            replayTransitionObserved?.Invoke(ReplayTransitionKind.SavedChoice);
            savedIndex++;
            cursorPositions[savedIndex] = replayedChoices.Count;
        }

        if (automaticContinuationError is null)
        {
            ContinueAutomatically();
            cursorPositions[savedIndex] = replayedChoices.Count;
        }

        cursorPositions[savedIndex] = replayedChoices.Count;
        CaptureRequestedCursor();
        cursorSession ??= session;
        cursorFrames ??= frames;

        return new AutomaticReplay(session, frames, replayedChoices, automaticChoices,
            evidence, replayedIdentities, cursorPositions, automaticContinuationError,
            cursorSession, cursorFrames,
            requestedCursor == choices.Count ? automaticContinuationError : cursorAutomaticContinuationError,
            savedEvidence);
    }
}
