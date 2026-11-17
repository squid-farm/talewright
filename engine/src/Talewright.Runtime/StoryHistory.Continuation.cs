using Talewright.Runtime.Saves;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

public sealed partial class StoryHistory
{
    private ContinuationCheckpoint? _origin;
    private IReadOnlyList<HistoryFrame> _prefix = [];
    private CheckpointPayload? _retained;

    private SaveContinuation CaptureContinuation()
    {
        var boundary = _session.LastEntry
            ?? throw new InvalidOperationException("This playthrough has no current passage to checkpoint.");
        return new SaveContinuation(new ContinuationPayload
        {
            Current = ContinuationCheckpoint.Capture(Story, Seed, _session.Events, boundary, _retained),
            Transcript = _prefix.Concat(_frames.Take(_frames.Count - 1)).Select(TranscriptCodec.Encode).ToList(),
            Base = _origin?.Payload,
            BaseTranscriptLength = _prefix.Count,
        });
    }

    private SaveContinuation ProgressContinuation() => new(new ContinuationPayload
    {
        Base = _origin?.Payload,
        BaseTranscriptLength = _prefix.Count,
    });

    private static SaveContinuation ProgressContinuation(ContinuationPayload payload) => new(new ContinuationPayload
    {
        Base = payload.Base,
        BaseTranscriptLength = payload.BaseTranscriptLength,
    });

    public static StoryHistory Restore(StoryDefinition story, ReadOnlySpan<byte> data)
    {
        var save = SaveFormat.Read(data);
        RequireSameStory(story, save);
        if (save.Continuation?.Payload is not { } continuation)
        {
            return RestoreByReplay(story, save);
        }

        var changed = save.StoryVersion != story.Version || save.ContentFingerprint != story.ContentFingerprint;
        if (!changed)
        {
            return continuation.Base is null
                ? RestoreByReplay(story, save)
                : ReplayFromBase(story, continuation, save);
        }

        if (continuation.Base is null)
        {
            try
            {
                return RestoreByReplay(story, save);
            }
            catch (InvalidDataException)
            {
                // An incompatible replay can continue from its checkpoint.
            }
        }
        return ResumeFromCheckpoint(story, continuation, save);
    }

    internal static StoryHistory RestoreFromCheckpoint(StoryDefinition story, ReadOnlySpan<byte> data)
    {
        var save = SaveFormat.Read(data);
        RequireSameStory(story, save);
        return ResumeFromCheckpoint(story, save.Continuation?.Payload
            ?? throw new InvalidDataException(SaveFormat.MalformedMessage), save);
    }

    private static void RequireSameStory(StoryDefinition story, SavedGame save)
    {
        if (save.StoryId != story.Id)
        {
            throw new InvalidDataException("This save belongs to a different story. Open it with the story and player version that created it.");
        }
        if (string.IsNullOrEmpty(story.ContentFingerprint))
        {
            throw new InvalidDataException("This save is incompatible with the current story or player. Contact the story author.");
        }
    }

    private static CharacterSelection? ValidateCharacter(StoryDefinition story, SavedGame save, bool changed)
    {
        try
        {
            return CharacterSelection.Validate(story, save.Character, save.Seed);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(changed
                ? $"This save cannot continue in the updated story because its character is no longer valid: {ex.Message} Open it with the story version that created it."
                : $"Invalid saved character: {ex.Message}", ex);
        }
    }

    private static StoryHistory ReplayFromBase(StoryDefinition story, ContinuationPayload saved, SavedGame save)
    {
        try
        {
            var character = ValidateCharacter(story, save, changed: false);
            var origin = ContinuationCheckpoint.Rebuild(story, save.Seed, character, saved.Base!, []);
            var all = ReplayWithAutomaticContinuations(story, save.Seed, save.Choices, null, character,
                origin: origin, baseTranscriptLength: saved.BaseTranscriptLength);
            var cursor = all.CursorPositions[save.Cursor];
            var current = cursor == all.Choices.Count ? all
                : ReplayWithAutomaticContinuations(story, save.Seed, all.Choices.Take(cursor).ToArray(), null,
                    character, origin: origin, baseTranscriptLength: saved.BaseTranscriptLength);
            return new StoryHistory(story, character, current.Session, current.Frames, all.Choices,
                new ReplayCursor(cursor, all.Choices.Count), null, null, all.AutomaticChoices, KeyOf(save),
                current.AutomaticContinuationError)
            {
                _origin = origin,
                _prefix = saved.Transcript!.Take(saved.BaseTranscriptLength).Select(TranscriptCodec.Decode).ToArray(),
                _retained = origin.Retained,
            };
        }
        catch (InvalidDataException)
        {
            throw ReplayFailure();
        }
    }

    private static StoryHistory ResumeFromCheckpoint(StoryDefinition story, ContinuationPayload saved, SavedGame save)
    {
        var character = ValidateCharacter(story, save, changed: true);
        var notes = new List<string>();
        var current = ContinuationCheckpoint.Rebuild(story, save.Seed, character, saved.Current!, notes);
        var session = current.Resume(story, save.Seed, character);
        var history = new StoryHistory(story, character, session, [new HistoryFrame(session.Current!)], [],
            new ReplayCursor(0, 0), null, null, [], KeyOf(save), null)
        {
            _origin = current,
            _prefix = saved.Transcript!.Select(TranscriptCodec.Decode).ToArray(),
            _retained = current.Retained,
            Continuation = new ContinuationReport
            {
                RedoDiscarded = save.Cursor < save.Choices.Count,
                Notes = notes.AsReadOnly(),
            },
        };
        history.TriggerSaveId = history.TriggerSaveFiredSince(current.Events.Count);
        history.ContinueAutomatically();
        return history;
    }
}
