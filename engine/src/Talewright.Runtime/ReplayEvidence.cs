using System.Security.Cryptography;
using System.Text.Json;
using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

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
        Hash(writer =>
        {
            writer.WriteStringValue("initial-state-v1");
            writer.WriteStringValue(story.Randomness.ToString());
            writer.WriteStringValue(state.Ruleset.Id);
            WriteRuleset(writer, state);
            WriteWorldState(writer, state);
        });

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

    public static byte[] Next(byte[] previous, StoryFrame frame, byte[] choiceIdentity,
        IReadOnlyList<GameEvent> events, int eventStart) =>
        Hash(writer =>
        {
            writer.WriteStringValue("choice");
            writer.WriteBase64StringValue(previous);
            writer.WriteStringValue(frame.Scene?.Value);
            writer.WriteBase64StringValue(choiceIdentity);
            WriteEvents(writer, events, eventStart, choiceIdentity);
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

        writer.WriteStringValue(state.Ruleset.SkillCheckRule?.GetType().FullName);
        writer.WriteNumberValue(state.Ruleset.SkillCheckRule?.DieSides ?? 0);
        writer.WriteStringValue(state.Ruleset.LevelingRule?.GetType().FullName);
        writer.WriteStringValue(state.Ruleset.LevelingRule?.LevelStat.Value);
        writer.WriteStartArray();
        foreach (var modifier in state.Ruleset.Modifiers)
        {
            writer.WriteStringValue(modifier.GetType().FullName);
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
        byte[]? choiceIdentity = null)
    {
        writer.WriteStartArray();
        for (var i = start; i < events.Count; i++)
        {
            if (events[i] is ChoiceMade choice && choiceIdentity is not null)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(nameof(ChoiceMade));
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

    private static void WriteTyped(Utf8JsonWriter writer, object value)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.GetType().Name);
        JsonSerializer.Serialize(writer, value, value.GetType(), ScenarioJsonContext.Default);
        writer.WriteEndArray();
    }
}
