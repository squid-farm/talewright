using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Runtime.Saves;
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

    public byte[] Save(SaveSummary? summary = null)
    {
        if (string.IsNullOrEmpty(Story.ContentFingerprint))
        {
            throw new InvalidOperationException("Saving is unavailable for this story. Ask the author to enable save compatibility for their custom extensions.");
        }

        if (_choices.Any(c => c.Length > 512))
        {
            throw new InvalidOperationException("This story contains a choice identifier that is too long to save. Contact the story author.");
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
            Summary = summary is null ? null : JsonSerializer.SerializeToElement(summary, SaveJsonContext.Default.SaveSummary),
        }, SaveJsonContext.Default.SavedHistory);
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidOperationException("This playthrough is too large to save (maximum 2 MiB). You can continue playing, but this save could not be created.");
        }

        return data;
    }

    public string ProgressKey => Convert.ToHexString(SHA256.HashData(Save()));

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
            var save = JsonSerializer.Deserialize(bytes, SaveJsonContext.Default.SavedHistory)
                ?? throw new JsonException("Empty saved game.");
            SaveSummary? summary = null;
            string? warning = null;
            if (save.Summary is { } metadata && metadata.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    summary = metadata.Deserialize(SaveJsonContext.Default.SaveSummary);
                    if (summary is null || string.IsNullOrWhiteSpace(summary.SceneTitle) || summary.SavedAt == default)
                    {
                        summary = null;
                        warning = "Save details are unreadable.";
                    }
                }
                catch (JsonException)
                {
                    warning = "Save details are unreadable.";
                }
            }
            save.Summary = null;
            var key = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(save, SaveJsonContext.Default.SavedHistory)));
            return new(slot, summary, key, warning);
        }
        catch (JsonException)
        {
            return new(slot, null, null, "Saved game is malformed; loading will show the error.");
        }
    }

    public static StoryHistory Restore(StoryDefinition story, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidDataException("This saved game is too large to load (maximum 2 MiB). Choose another save.");
        }

        SavedHistory save;
        try
        {
            save = JsonSerializer.Deserialize(data, SaveJsonContext.Default.SavedHistory)
                ?? throw new InvalidDataException("This saved game is empty. Choose another save or start a new game.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This saved game is malformed or damaged. Choose another save or start a new game.", ex);
        }

        if (save.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException("This saved game uses an unsupported format. Open it with the player version that created it.");
        }

        if (string.IsNullOrEmpty(story.ContentFingerprint) ||
            save.StoryId != story.Id || save.StoryVersion != story.Version ||
            save.ContentFingerprint != story.ContentFingerprint)
        {
            throw new InvalidDataException("This save belongs to a different story or an incompatible story/extension version. Open it with the story and player version that created it.");
        }

        if (save.Choices is null || save.Choices.Length > MaxChoices || save.Cursor < 0 || save.Cursor > save.Choices.Length ||
            save.Choices.Any(c => string.IsNullOrEmpty(c) || c.Length > 512))
        {
            throw new InvalidDataException("This saved game's history is damaged or exceeds the supported length. Choose another save.");
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
                throw new InvalidDataException($"This saved game cannot continue at choice {frames.Count}. It may be damaged or belong to a different story version.");
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Summary { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SavedHistory))]
[JsonSerializable(typeof(SaveSummary))]
internal partial class SaveJsonContext : JsonSerializerContext;
