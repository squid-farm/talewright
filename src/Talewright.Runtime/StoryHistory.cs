using System.Text.Json;
using System.Text.Json.Serialization;
using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

public sealed record HistoryFrame(StoryFrame Frame, IReadOnlyList<RenderedInline>? Chosen = null);

/// <summary>Deterministic choice history. Restores are built separately before replacing a live session.</summary>
public sealed class StoryHistory
{
    public const int MaxChoices = 10_000;
    public const int MaxSaveBytes = 2 * 1024 * 1024;
    private const int FormatVersion = 1;
    private readonly List<string> _choices = [];
    private StorySession _session;
    private List<HistoryFrame> _frames;

    public StoryHistory(StoryDefinition story, ulong? seed = null, CharacterSelection? character = null)
    {
        Character = CharacterSelection.Validate(story, character);
        Story = story;
        _session = new StorySession(story, seed, character: Character);
        _frames = [new HistoryFrame(_session.Start())];
    }

    public StoryDefinition Story { get; }
    public CharacterSelection? Character { get; }
    public ulong Seed => _session.Seed;
    public IGameStateView State => _session.State;
    public IReadOnlyList<GameEvent> Events => _session.Events;
    public StoryFrame Current => _session.Current!;
    public IReadOnlyList<HistoryFrame> Transcript => _frames.AsReadOnly();
    public int Cursor { get; private set; }
    public bool CanBack => Story.UndoEnabled && Cursor > 0;
    public bool CanForward => Story.UndoEnabled && Cursor < _choices.Count;

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
        _choices.RemoveRange(Cursor, _choices.Count - Cursor);
        _choices.Add(key);
        Cursor++;
    }

    public void Back()
    {
        if (!CanBack)
        {
            throw new InvalidOperationException("There is no previous choice to restore.");
        }

        MoveTo(Cursor - 1);
    }

    public void Forward()
    {
        if (!CanForward)
        {
            throw new InvalidOperationException("There is no next choice to restore.");
        }

        MoveTo(Cursor + 1);
    }

    private void MoveTo(int cursor)
    {
        var (session, frames) = Replay(Story, Seed, _choices.Take(cursor), Character);
        _session = session;
        _frames = frames;
        Cursor = cursor;
    }

    public byte[] Save()
    {
        if (string.IsNullOrEmpty(Story.ContentFingerprint))
        {
            throw new InvalidOperationException("Saving requires a compiled story and an explicit extension compatibility identity.");
        }

        if (_choices.Any(c => c.Length > 512))
        {
            throw new InvalidOperationException("A saved choice key may contain at most 512 characters.");
        }

        var data = JsonSerializer.SerializeToUtf8Bytes(new SavedHistory
        {
            FormatVersion = FormatVersion,
            StoryId = Story.Id,
            StoryVersion = Story.Version,
            ContentFingerprint = Story.ContentFingerprint,
            Seed = Seed,
            Choices = _choices.ToArray(),
            Cursor = Cursor,
            Character = Character,
        }, SaveJsonContext.Default.SavedHistory);
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidOperationException("The save exceeds the 2 MiB size limit.");
        }

        return data;
    }

    public static StoryHistory Restore(StoryDefinition story, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidDataException("The save exceeds the 2 MiB size limit.");
        }

        SavedHistory save;
        try
        {
            save = JsonSerializer.Deserialize(data, SaveJsonContext.Default.SavedHistory)
                ?? throw new InvalidDataException("The save is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The save is malformed: {ex.Message}", ex);
        }

        if (save.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported save format {save.FormatVersion}; expected {FormatVersion}.");
        }

        if (string.IsNullOrEmpty(story.ContentFingerprint) ||
            save.StoryId != story.Id || save.StoryVersion != story.Version ||
            save.ContentFingerprint != story.ContentFingerprint)
        {
            throw new InvalidDataException("This save belongs to a different story or an incompatible story/extension version.");
        }

        if (save.Choices is null || save.Choices.Length > MaxChoices || save.Cursor < 0 || save.Cursor > save.Choices.Length ||
            save.Choices.Any(c => string.IsNullOrEmpty(c) || c.Length > 512))
        {
            throw new InvalidDataException("The save has invalid choice history or cursor bounds.");
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
        Replay(story, save.Seed, save.Choices, character);
        var result = new StoryHistory(story, save.Seed, character);
        result._choices.AddRange(save.Choices);
        result.MoveTo(save.Cursor);
        return result;
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
                throw new InvalidDataException($"Saved choice {frames.Count} ('{key}') is not available.");
            }

            frames[^1] = frames[^1] with { Chosen = choice.Label };
            frames.Add(new HistoryFrame(session.Choose(key)));
        }

        return (session, frames);
    }
}

internal sealed class SavedHistory
{
    public required int FormatVersion { get; init; }
    public required string StoryId { get; init; }
    public required string StoryVersion { get; init; }
    public required string ContentFingerprint { get; init; }
    public required ulong Seed { get; init; }
    public required string[] Choices { get; init; }
    public required int Cursor { get; init; }
    public CharacterSelection? Character { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SavedHistory))]
internal partial class SaveJsonContext : JsonSerializerContext;
