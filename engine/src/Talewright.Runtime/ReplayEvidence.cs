using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Rules.Checks;
using Talewright.Rulesets.Generic;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

/// <summary>
/// Owns the replay-evidence hash inputs stored in player saves. Type names and enum values are written as the frozen
/// tokens below, not taken from CLR names; event payloads use the author-scenario serializer and are pinned by
/// <c>ReplayEvidenceEncodingTests</c>. Change <see cref="Version"/> deliberately when the encoding must change.
/// </summary>
internal static class ReplayEvidence
{
    private const byte Version = 3;
    internal const int Length = 1 + 32;

    public static bool IsValid(byte[] evidence) => evidence.Length == Length && evidence[0] == Version;

    public static byte[] Encode(byte[] digest, int cursor)
    {
        var bound = Hash(writer =>
        {
            writer.WriteStringValue("cursor");
            writer.WriteBase64StringValue(digest);
            writer.WriteNumberValue(cursor);
        });
        var evidence = new byte[Length];
        evidence[0] = Version;
        bound.CopyTo(evidence, 1);
        return evidence;
    }

    public static bool Matches(byte[] evidence, byte[] digest, int cursor) =>
        IsValid(evidence) && CryptographicOperations.FixedTimeEquals(evidence, Encode(digest, cursor));

    public static byte[] InitialState(StoryDefinition story, IGameStateView state) =>
        Hash(writer => WriteInitialState(writer, story, state));

    private static void WriteInitialState(Utf8JsonWriter writer, StoryDefinition story, IGameStateView state)
    {
        writer.WriteStringValue("initial-state-v1");
        writer.WriteStringValue(Token(story.Randomness));
        writer.WriteStringValue(state.Ruleset.Id);
        WriteRuleset(writer, state);
        WriteWorldState(writer, state);
    }

    /// <summary>The bytes <see cref="InitialState"/> hashes; exposed so tests can pin them.</summary>
    internal static byte[] InitialStateInput(StoryDefinition story, IGameStateView state) =>
        Input(writer => WriteInitialState(writer, story, state));

    /// <summary>The bytes a recorded event contributes to evidence; exposed so tests can pin them.</summary>
    internal static byte[] EventInput(GameEvent gameEvent) => Input(writer => WriteTyped(writer, gameEvent));

    public static byte[] Start(StoryDefinition story, StorySession session, CharacterSelection? character,
        byte[] initialState) =>
        Hash(writer =>
        {
            writer.WriteStringValue("start");
            writer.WriteStringValue(story.Id);
            writer.WriteStringValue(story.ReplayCompatibilityId);
            writer.WriteNumberValue(session.Seed);
            WriteCharacter(writer, character);
            writer.WriteBase64StringValue(initialState);
            WriteEvents(writer, session.Events, 0);
        });

    public static byte[] Next(byte[] previous, StoryFrame frame, ReadOnlyMemory<byte> choiceIdentity,
        IReadOnlyList<GameEvent> events, int eventStart) =>
        Hash(writer =>
        {
            writer.WriteStringValue("choice");
            writer.WriteBase64StringValue(previous);
            writer.WriteStringValue(frame.Scene?.Value);
            writer.WriteBase64StringValue(choiceIdentity.Span);
            WriteEvents(writer, events, eventStart, choiceIdentity.Span);
        });

    private static byte[] Hash(Action<Utf8JsonWriter> write)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            write(writer);
            writer.WriteEndArray();
        }
        stream.FlushFinalBlock();
        return hash.Hash ?? throw new InvalidOperationException("Replay evidence could not be calculated.");
    }

    private static byte[] Input(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            write(writer);
            writer.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCharacter(Utf8JsonWriter writer, CharacterSelection? character)
    {
        if (character is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        writer.WriteStringValue(character.FirstName);
        writer.WriteStringValue(character.LastName);
        writer.WriteStringValue(character.Pronouns);
        writer.WriteStringValue(character.Preset);
        writer.WriteStartArray();
        foreach (var (id, value) in character.Stats.OrderBy(stat => stat.Key, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(id);
            writer.WriteNumberValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndArray();
    }

    private static void WriteRuleset(Utf8JsonWriter writer, IGameStateView state)
    {
        writer.WriteStartArray();
        foreach (var stat in state.Ruleset.Stats.OrderBy(stat => stat.Id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(stat.Id.Value);
            writer.WriteNumberValue(stat.Min);
            writer.WriteNumberValue(stat.Max);
            writer.WriteNumberValue(stat.Default);
            writer.WriteStringValue(stat.GoverningAttribute?.Value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var tier in state.Ruleset.Tiers)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(tier.Id);
            writer.WriteNumberValue(tier.Minimum);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStringValue(RuleToken(state.Ruleset.SkillCheckRule));
        writer.WriteNumberValue(state.Ruleset.SkillCheckRule?.DieSides ?? 0);
        writer.WriteStringValue(RuleToken(state.Ruleset.LevelingRule));
        writer.WriteStringValue(state.Ruleset.LevelingRule?.LevelStat.Value);
        writer.WriteStartArray();
        foreach (var modifier in state.Ruleset.Modifiers)
        {
            writer.WriteStringValue(RuleToken(modifier));
        }
        writer.WriteEndArray();
    }

    private static void WriteWorldState(Utf8JsonWriter writer, IGameStateView state)
    {
        writer.WriteStartArray();
        foreach (var id in state.World.Characters.Keys.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(id.Value);
            foreach (var stat in state.Ruleset.Stats.OrderBy(stat => stat.Id.Value, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(stat.Id.Value);
                writer.WriteNumberValue(state.GetBaseStat(id, stat.Id));
                writer.WriteNumberValue(state.GetStat(id, stat.Id));
                writer.WriteEndArray();
            }
            writer.WriteStartArray();
            foreach (var item in state.World.Items.Keys.OrderBy(item => item.Value, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(item.Value);
                writer.WriteNumberValue(state.GetItemCount(id, item));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var (id, item) in state.World.Items.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(id.Value);
            foreach (var (stat, modifier) in item.Modifiers.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(stat.Value);
                writer.WriteNumberValue(modifier);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var faction in state.World.Factions.Keys.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(faction.Value);
            writer.WriteNumberValue(state.GetStanding(faction));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var flag in state.World.Flags.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(flag.Value);
            writer.WriteBooleanValue(state.GetFlag(flag));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var variable in state.World.Variables.Keys.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(variable.Value);
            writer.WriteNumberValue(state.GetVariable(variable));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteStartArray();
        foreach (var variable in state.World.TextVariables.Keys.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(variable.Value);
            writer.WriteStringValue(state.GetTextVariable(variable));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteEvents(Utf8JsonWriter writer, IReadOnlyList<GameEvent> events, int start,
        ReadOnlySpan<byte> choiceIdentity = default)
    {
        writer.WriteStartArray();
        for (var i = start; i < events.Count; i++)
        {
            if (events[i] is ChoiceMade choice && !choiceIdentity.IsEmpty)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(EventToken(choice));
                writer.WriteStringValue(choice.Scene.Value);
                writer.WriteBase64StringValue(choiceIdentity);
                writer.WriteEndArray();
            }
            else
            {
                WriteTyped(writer, events[i]);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteTyped(Utf8JsonWriter writer, GameEvent value)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(EventToken(value));
        JsonSerializer.Serialize(writer, value, value.GetType(), ScenarioJsonContext.Default);
        writer.WriteEndArray();
    }

    internal static string Token(RandomnessMode randomness) => randomness switch
    {
        RandomnessMode.Off => "Off",
        RandomnessMode.ChecksOnly => "ChecksOnly",
        _ => throw new NotSupportedException($"Randomness mode '{randomness}' has no replay evidence token."),
    };

    // Built-in rules keep the names they were first encoded with. Extension types have no frozen token, so their CLR
    // full name is part of the evidence.
    internal static string? RuleToken(object? rule) => rule switch
    {
        null => null,
        D20SkillCheckRule => "Talewright.Rulesets.Generic.D20SkillCheckRule",
        ExperienceLevelingRule => "Talewright.Rulesets.Generic.ExperienceLevelingRule",
        _ => rule.GetType().FullName,
    };

    internal static string EventToken(GameEvent gameEvent) => gameEvent switch
    {
        CharacterIdentityChanged => "CharacterIdentityChanged",
        StatChanged => "StatChanged",
        ItemsGranted => "ItemsGranted",
        ItemsRemoved => "ItemsRemoved",
        StandingChanged => "StandingChanged",
        FlagChanged => "FlagChanged",
        VariableChanged => "VariableChanged",
        TextVariableChanged => "TextVariableChanged",
        SceneEntered => "SceneEntered",
        ChoiceMade => "ChoiceMade",
        DiceRolled => "DiceRolled",
        SkillCheckResolved => "SkillCheckResolved",
        LevelChanged => "LevelChanged",
        TriggerFired => "TriggerFired",
        StoryEnded => "StoryEnded",
        _ => throw new NotSupportedException($"Game event '{gameEvent.GetType().Name}' has no replay evidence token."),
    };
}
