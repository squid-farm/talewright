using System.Security.Cryptography;
using System.Text.Json;
using Talewright.Core.Events;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal static class ReplayEvidence
{
    private const byte Version = 2;
    internal const int Length = 1 + 32;

    public static bool IsValid(byte[] evidence) => evidence.Length == Length && evidence[0] == Version;

    public static byte[] Encode(byte[] digest, int cursor, StoryFrame current)
    {
        var bound = Hash(writer =>
        {
            writer.WriteStringValue("cursor");
            writer.WriteBase64StringValue(digest);
            writer.WriteNumberValue(cursor);
            WriteFrame(writer, current);
        });
        var evidence = new byte[Length];
        evidence[0] = Version;
        bound.CopyTo(evidence, 1);
        return evidence;
    }

    public static bool Matches(byte[] evidence, byte[] digest, int cursor, StoryFrame current) =>
        IsValid(evidence) && CryptographicOperations.FixedTimeEquals(evidence, Encode(digest, cursor, current));

    public static byte[] Start(StoryDefinition story, StorySession session, CharacterSelection? character) =>
        Hash(writer =>
        {
            writer.WriteStringValue("start");
            writer.WriteStringValue(story.Id);
            writer.WriteStringValue(story.ReplayCompatibilityId);
            writer.WriteNumberValue(session.Seed);
            WriteCharacter(writer, character);
            WriteEvents(writer, session.Events, 0);
        });

    public static byte[] Next(byte[] previous, StoryFrame frame, string key,
        IReadOnlyList<GameEvent> events, int eventStart) =>
        Hash(writer =>
        {
            writer.WriteStringValue("choice");
            writer.WriteBase64StringValue(previous);
            WriteFrame(writer, frame);
            writer.WriteStringValue(key);
            WriteEvents(writer, events, eventStart);
        });

    public static bool FramesMatch(StoryFrame left, StoryFrame right) =>
        CryptographicOperations.FixedTimeEquals(
            Hash(writer => WriteFrame(writer, left)), Hash(writer => WriteFrame(writer, right)));

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

    private static void WriteFrame(Utf8JsonWriter writer, StoryFrame frame)
    {
        writer.WriteStringValue(frame.Scene?.Value);
        writer.WriteStartArray();
        foreach (var choice in frame.Choices)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(choice.Key);
            writer.WriteStartArray();
            foreach (var inline in choice.Label)
            {
                WriteTyped(writer, inline);
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteEvents(Utf8JsonWriter writer, IReadOnlyList<GameEvent> events, int start)
    {
        writer.WriteStartArray();
        for (var i = start; i < events.Count; i++)
        {
            WriteTyped(writer, events[i]);
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
