using System.Collections.ObjectModel;
using Talewright.Core.Identity;
using Talewright.Core.World;

namespace Talewright.Tale.Model;

public sealed record CharacterPreset(string Id, string Label, IReadOnlyDictionary<StatId, int> Stats);

/// <summary>Bounded author-defined choices; no point-buy or implicit class/race system.</summary>
public sealed class CharacterCreation
{
    public bool EditName { get; init; }
    public IReadOnlyList<string> Pronouns { get; init; } = [];
    public IReadOnlyList<CharacterPreset> Presets { get; init; } = [];
    public IReadOnlyDictionary<StatId, IReadOnlyList<int>> Stats { get; init; } = new Dictionary<StatId, IReadOnlyList<int>>();

    public CharacterSelection Default(CharacterDefinition player, string? presetId = null)
    {
        var preset = presetId is null ? Presets.FirstOrDefault()
            : Presets.Single(p => p.Id == presetId);
        return new CharacterSelection
        {
            FirstName = EditName ? player.FirstName : null,
            LastName = EditName ? player.LastName : null,
            Pronouns = Pronouns.FirstOrDefault(),
            Preset = preset?.Id,
            Stats = Stats.ToDictionary(kv => kv.Key.Value, kv =>
            {
                var value = preset is not null && preset.Stats.TryGetValue(kv.Key, out var presetValue)
                    ? presetValue : player.Stats.GetValueOrDefault(kv.Key);
                return kv.Value.Contains(value) ? value : kv.Value[0];
            }),
        };
    }
}

/// <summary>Replay input; copied and validated before a session starts or a saved session restores.</summary>
public sealed record CharacterSelection
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Pronouns { get; init; }
    public string? Preset { get; init; }
    public IReadOnlyDictionary<string, int> Stats { get; init; } = new Dictionary<string, int>();

    public static CharacterSelection? Validate(StoryDefinition story, CharacterSelection? selection)
    {
        if (story.Creation is not { } creation)
        {
            if (selection is not null)
            {
                throw new ArgumentException("This story has a predefined protagonist.");
            }

            return null;
        }

        if (selection is null)
        {
            throw new ArgumentException("Create a character before starting this story.");
        }

        static bool Name(string? text, bool required) => text is not null && (!required || text.Length > 0)
            && text.Length <= 80 && text.Trim() == text && !text.Any(char.IsControl);
        if (creation.EditName
            ? !Name(selection.FirstName, true) || !Name(selection.LastName, false)
            : selection.FirstName is not null || selection.LastName is not null)
        {
            throw new ArgumentException("Names must be trimmed, contain no control characters and have at most 80 characters; a first name is required.");
        }

        if (creation.Pronouns.Count == 0 ? selection.Pronouns is not null
            : selection.Pronouns is null || !creation.Pronouns.Contains(selection.Pronouns))
        {
            throw new ArgumentException("Select one of the story's pronoun options.");
        }

        if (creation.Presets.Count == 0 ? selection.Preset is not null : !creation.Presets.Any(p => p.Id == selection.Preset))
        {
            throw new ArgumentException("Select one of the story's character presets.");
        }

        if (selection.Stats is null || selection.Stats.Count != creation.Stats.Count ||
            creation.Stats.Any(kv => !selection.Stats.TryGetValue(kv.Key.Value, out var value) || !kv.Value.Contains(value)))
        {
            throw new ArgumentException("Select a legal value for every configurable stat.");
        }

        return selection with { Stats = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(selection.Stats)) };
    }
}
