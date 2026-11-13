using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Talewright.Core.Randomness;

/// <summary>
/// Deterministic dice: each roll is a pure function of its seed, check identity, execution key and die sides. Player
/// saves reuse the seed and choice history to regenerate the same rolls. Folding an existing event log uses recorded
/// results.
/// </summary>
public static class DeterministicDice
{
    /// <summary>Rolls a die with <paramref name="sides"/> faces (1..sides) for a stable execution key.</summary>
    public static int Roll(ulong seed, string identity, string executionIdentity, int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 1);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(executionIdentity);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(seed);
            writer.WriteStringValue(identity);
            writer.WriteStringValue(executionIdentity);
            writer.WriteNumberValue(sides);
            writer.WriteEndArray();
        }
        var value = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(buffer.WrittenSpan));
        return (int)(value % (ulong)sides) + 1;
    }

    /// <summary>Creates a fresh, non-deterministic seed for a new playthrough.</summary>
    public static ulong NewSeed() => (ulong)Random.Shared.NextInt64();

    /// <summary>Uses the first supplied seed, or creates a fresh seed if none is supplied.</summary>
    public static ulong ResolveSeed(ulong? primarySeed, ulong? secondarySeed, ulong? fallbackSeed = null) =>
        primarySeed ?? secondarySeed ?? fallbackSeed ?? NewSeed();
}
