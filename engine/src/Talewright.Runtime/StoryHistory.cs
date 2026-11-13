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

public sealed record HistoryFrame(StoryFrame Frame, IReadOnlyList<RenderedInline>? Chosen = null);

/// <summary>Deterministic choice history. Restores are built separately before replacing a live session.</summary>
public sealed class StoryHistory
{
    public const int MaxChoices = SaveFormat.MaxChoices;
    public const int MaxSaveBytes = SaveFormat.MaxSaveBytes;
    private readonly List<string> _choices = [];
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
    }

    private StoryHistory(StoryDefinition story, CharacterSelection? character, StorySession session,
        List<HistoryFrame> frames, IEnumerable<string> choices, ReplayCursor timeline, List<byte[]>? evidence,
        List<byte[]>? replayChoiceIdentities,
        string? restoredProgressKey)
    {
        Story = story;
        Character = character;
        _session = session;
        _frames = frames;
        _choices.AddRange(choices);
        _timeline = timeline;
        _evidence = evidence;
        _replayChoiceIdentities = replayChoiceIdentities;
        RestoredProgressKey = restoredProgressKey;
    }

    public StoryDefinition Story { get; }
    public CharacterSelection? Character { get; }
    public ulong Seed => _session.Seed;
    public IGameStateView State => _session.State;
    public IReadOnlyList<GameEvent> Events => _session.Events;
    public StoryFrame Current => _session.Current!;
    public IReadOnlyList<HistoryFrame> Transcript => _frames.AsReadOnly();
    public int Cursor => _timeline.Position;
    public bool CanBack => Story.UndoEnabled && _timeline.CanBack;
    public bool CanForward => Story.UndoEnabled && _timeline.CanForward;
    /// <summary>The last saving trigger to fire during the latest start or choice step, if any.</summary>
    public string? TriggerSaveId { get; private set; }
    /// <summary>The original slot's progress after a load, until the timeline changes.</summary>
    public string? RestoredProgressKey { get; private set; }

    /// <summary>
    /// Forks validated player replay inputs into isolated synthetic author tooling, retaining redo. Each decision
    /// becomes the current choice that replay selected, not the key recorded by an older story revision.
    /// </summary>
    public AuthorSession ForkAuthor(bool captureDecisions = true)
    {
        var keys = new List<string?>(_choices.Count);
        Replay(Story, Seed, _choices, _replayChoiceIdentities, Character, liveKeys: keys);
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
        if (Cursor >= MaxChoices)
        {
            throw new InvalidOperationException($"A playthrough supports at most {MaxChoices:N0} choices.");
        }

        var choice = Current.Choices.FirstOrDefault(c => c.Key == key)
            ?? throw new InvalidOperationException($"'{key}' is not an available choice.");
        var previousFrame = Current;
        var eventStart = _session.Events.Count;
        var frame = _session.Choose(key);
        TriggerSaveId = TriggerSaveFiredSince(eventStart);
        if (_evidence is not null && choice.ReplayIdentity is { } identity)
        {
            var next = ReplayEvidence.Next(_evidence[_timeline.Position], previousFrame, identity, _session.Events, eventStart);
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
        _frames[^1] = _frames[^1] with { Chosen = choice.Label };
        _frames.Add(new HistoryFrame(frame));
        _choices.RemoveRange(_timeline.Position, _timeline.Length - _timeline.Position);
        _choices.Add(key);
        _timeline = _timeline.AfterAppend();
        RestoredProgressKey = null;
    }

    public void Back()
    {
        if (!CanBack)
        {
            throw new InvalidOperationException("There is no previous choice to restore.");
        }

        MoveTo(_timeline.Back());
    }

    public void Forward()
    {
        if (!CanForward)
        {
            throw new InvalidOperationException("There is no next choice to restore.");
        }

        MoveTo(_timeline.Forward());
    }

    private void MoveTo(ReplayCursor timeline)
    {
        var (session, frames, _, _) = Replay(Story, Seed, _choices.Take(timeline.Position).ToArray(),
            _replayChoiceIdentities?.Take(timeline.Position).ToArray(), Character);
        _session = session;
        _frames = frames;
        _timeline = timeline;
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

    public byte[] Save(SaveSummary? summary = null)
    {
        var data = SaveFormat.Write(ToSavedGame(summary));
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidOperationException("This playthrough is too large to save (maximum 2 MiB). You can continue playing, but this save could not be created.");
        }

        return data;
    }

    /// <summary>Identifies replay progress. Equal for every save of the same playthrough, whatever its details.</summary>
    public string ProgressKey => KeyOf(ToSavedGame(null));

    private SavedGame ToSavedGame(SaveSummary? summary)
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
        };
    }

    private static string KeyOf(SavedGame save) =>
        Convert.ToHexString(SHA256.HashData(SaveFormat.EncodeBody(save with { Summary = null })));

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
            return new(slot, null, null, "Saved game is empty or exceeds 2 MiB.");
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

    public static StoryHistory Restore(StoryDefinition story, ReadOnlySpan<byte> data)
    {
        var save = SaveFormat.Read(data);
        if (save.StoryId != story.Id)
        {
            throw new InvalidDataException("This save belongs to a different story. Open it with the story and player version that created it.");
        }

        if (string.IsNullOrEmpty(story.ContentFingerprint))
        {
            throw new InvalidDataException("This save is incompatible with the current story or player. Contact the story author.");
        }

        var changed = save.StoryVersion != story.Version || save.ContentFingerprint != story.ContentFingerprint;
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
        var timeline = new ReplayCursor(save.Cursor, save.Choices.Count);
        StorySession session;
        List<HistoryFrame> frames;
        List<byte[]>? evidence;
        List<byte[]>? replayChoiceIdentities;
        try
        {
            (session, frames, evidence, replayChoiceIdentities) = Replay(story, save.Seed,
                save.Choices, save.ReplayChoiceIdentities, character, recordEvidence: true);
        }
        catch (InvalidDataException ex) when (changed)
        {
            throw Incompatible(story, save, ex);
        }

        if (timeline.CanForward)
        {
            List<byte[]>? prefixEvidence;
            try
            {
                (session, frames, prefixEvidence, _) = Replay(story, save.Seed,
                    save.Choices.Take(timeline.Position).ToArray(),
                    save.ReplayChoiceIdentities?.Take(timeline.Position).ToArray(),
                    character, recordEvidence: evidence is not null);
            }
            catch (InvalidDataException ex) when (changed)
            {
                throw Incompatible(story, save, ex);
            }

            if (evidence is not null && (prefixEvidence is null
                || !CryptographicOperations.FixedTimeEquals(evidence[timeline.Position], prefixEvidence[^1])))
            {
                throw ReplayMismatch(story, save, changed);
            }
        }

        if (save.ReplayEvidence is { } savedEvidence
            && (evidence is null || !ReplayEvidence.Matches(savedEvidence, evidence[^1], save.Cursor)))
        {
            throw ReplayMismatch(story, save, changed);
        }

        return new StoryHistory(story, character, session, frames, save.Choices, timeline, evidence,
            replayChoiceIdentities, KeyOf(save));
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

    private static (StorySession Session, List<HistoryFrame> Frames, List<byte[]>? Evidence,
        List<byte[]>? ReplayChoiceIdentities) Replay(StoryDefinition story, ulong seed,
        IReadOnlyList<string> choices, IReadOnlyList<byte[]>? replayChoiceIdentities, CharacterSelection? character,
        bool recordEvidence = false, List<string?>? liveKeys = null)
    {
        var session = new StorySession(story, seed, character: character);
        var initialState = ReplayEvidence.InitialState(story, session.State);
        var frames = new List<HistoryFrame> { new(session.Start()) };
        var evidence = recordEvidence && CanRecordEvidence(story)
            ? new List<byte[]> { ReplayEvidence.Start(story, session, character, initialState) } : null;
        var replayedIdentities = evidence is null ? null : new List<byte[]>();
        for (var index = 0; index < choices.Count; index++)
        {
            var key = choices[index];
            var previousFrame = session.Current!;
            var eventStart = session.Events.Count;
            ChoiceView? choice;
            ReadOnlyMemory<byte>? identity;
            if (replayChoiceIdentities is not null)
            {
                if (!session.ChooseReplay(replayChoiceIdentities[index], out choice) || choice is null)
                {
                    throw ReplayFailure();
                }
                identity = replayChoiceIdentities[index];
                // Replay may select an equivalent choice that live play would not offer here.
                var selectedKey = choice.Key;
                liveKeys?.Add(previousFrame.Choices.Any(offered => offered.Key == selectedKey) ? selectedKey : null);
            }
            else
            {
                choice = previousFrame.Choices.FirstOrDefault(c => c.Key == key);
                if (choice is null)
                {
                    throw ReplayFailure();
                }
                identity = choice.ReplayIdentity;
                session.Choose(key);
                liveKeys?.Add(key);
            }

            frames[^1] = frames[^1] with { Chosen = choice.Label };
            frames.Add(new HistoryFrame(session.Current!));
            if (identity is not null && evidence is not null && replayedIdentities is not null)
            {
                evidence.Add(ReplayEvidence.Next(evidence[^1], previousFrame, identity.Value, session.Events, eventStart));
                replayedIdentities.Add(identity.Value.ToArray());
            }
            else
            {
                evidence = null;
                replayedIdentities = null;
            }
        }

        return (session, frames, evidence, replayedIdentities);
    }
}
