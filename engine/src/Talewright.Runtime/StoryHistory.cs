using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
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
    private StorySession _session;
    private List<HistoryFrame> _frames;
    private ReplayCursor _timeline;

    public StoryHistory(StoryDefinition story, ulong? seed = null, CharacterSelection? character = null)
    {
        Character = CharacterSelection.Validate(story, character);
        Story = story;
        _timeline = new ReplayCursor(0, 0);
        _session = new StorySession(story, seed, character: Character);
        _frames = [new HistoryFrame(_session.Start())];
    }

    private StoryHistory(StoryDefinition story, CharacterSelection? character, StorySession session,
        List<HistoryFrame> frames, IEnumerable<string> choices, ReplayCursor timeline)
    {
        Story = story;
        Character = character;
        _session = session;
        _frames = frames;
        _choices.AddRange(choices);
        _timeline = timeline;
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

    /// <summary>Forks validated player replay inputs into isolated synthetic author tooling, retaining redo.</summary>
    public AuthorSession ForkAuthor(bool captureDecisions = true) =>
        AuthorSession.RestoreTimeline(Story, Story.Start, new AuthorSetup(), Seed, Character,
            _choices.Select(key => (AuthorCommand)new AuthorChoice(key)).ToArray(), Cursor, captureDecisions);

    public void Choose(string key)
    {
        if (Cursor >= MaxChoices)
        {
            throw new InvalidOperationException($"A playthrough supports at most {MaxChoices:N0} choices.");
        }

        var choice = Current.Choices.FirstOrDefault(c => c.Key == key)
            ?? throw new InvalidOperationException($"'{key}' is not an available choice.");
        var frame = _session.Choose(key);
        _frames[^1] = _frames[^1] with { Chosen = choice.Label };
        _frames.Add(new HistoryFrame(frame));
        _choices.RemoveRange(_timeline.Position, _timeline.Length - _timeline.Position);
        _choices.Add(key);
        _timeline = _timeline.AfterAppend();
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
        var (session, frames) = Replay(Story, Seed, _choices.Take(timeline.Position), Character);
        _session = session;
        _frames = frames;
        _timeline = timeline;
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

        if (save.StoryVersion != story.Version || save.ContentFingerprint != story.ContentFingerprint)
        {
            var version = save.StoryVersion;
            var subject = !string.IsNullOrWhiteSpace(version) && version.Length <= 64 && !version.Any(char.IsControl)
                ? $"Version {version} of {story.Title}"
                : story.Title;
            throw new InvalidDataException($"{subject} has changed, and this save is no longer compatible. Open it with the story and player version that created it.");
        }

        // Validate redo choices too, not just the visible prefix.
        CharacterSelection? character;
        try
        {
            character = CharacterSelection.Validate(story, save.Character);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Invalid saved character: {ex.Message}", ex);
        }
        var timeline = new ReplayCursor(save.Cursor, save.Choices.Count);
        var (session, frames) = Replay(story, save.Seed, save.Choices, character);
        if (timeline.CanForward)
        {
            (session, frames) = Replay(story, save.Seed, save.Choices.Take(timeline.Position), character);
        }

        return new StoryHistory(story, character, session, frames, save.Choices, timeline);
    }

    private static (StorySession Session, List<HistoryFrame> Frames) Replay(
        StoryDefinition story, ulong seed, IEnumerable<string> choices, CharacterSelection? character)
    {
        var session = new StorySession(story, seed, character: character);
        var frames = new List<HistoryFrame> { new(session.Start()) };
        foreach (var key in choices)
        {
            var choice = session.Current!.Choices.FirstOrDefault(c => c.Key == key);
            if (choice is null)
            {
                throw new InvalidDataException("This saved game cannot continue because the story has changed or the save is damaged. It is no longer compatible.");
            }

            frames[^1] = frames[^1] with { Chosen = choice.Label };
            frames.Add(new HistoryFrame(session.Choose(key)));
        }

        return (session, frames);
    }
}
