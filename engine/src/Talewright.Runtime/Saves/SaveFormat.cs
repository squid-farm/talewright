using System.Buffers;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using MessagePack;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Saves;

/// <summary>The replay inputs stored in a save. Saves never contain the event log or game state.</summary>
public sealed record SavedGame
{
    public required string StoryId { get; init; }
    public required string StoryVersion { get; init; }
    public required string ContentFingerprint { get; init; }
    public required ulong Seed { get; init; }
    public required IReadOnlyList<string> Choices { get; init; }
    public required int Cursor { get; init; }
    public CharacterSelection? Character { get; init; }
    public SaveSummary? Summary { get; init; }
    public byte[]? ReplayEvidence { get; init; }
}

/// <summary>Reads and writes save files: a short header followed by a zlib-compressed MessagePack array.</summary>
public static class SaveFormat
{
    public const int Version = 4;
    /// <summary>File extension for saves on disk and exported backups.</summary>
    public const string FileExtension = ".twsave";
    public const int MaxSaveBytes = 2 * 1024 * 1024;
    public const int MaxDecodedBytes = 8 * 1024 * 1024;
    public const int MaxChoices = 10_000;
    public const int MaxChoiceKeyLength = 512;
    public const int MaxCharacterStats = 1_000;

    private const int HeaderLength = 4 + 1 + sizeof(uint);
    private const int MaxArrayFields = 32;
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    internal const string MalformedMessage = "This saved game is malformed or damaged. Choose another save or start a new game.";
    internal const string UnsupportedMessage = "This saved game uses an unsupported format. Open it with the player version that created it.";
    internal const string TooLargeMessage = "This saved game is too large to load (maximum 2 MiB). Choose another save.";
    internal const string EmptyMessage = "This saved game is empty. Choose another save or start a new game.";
    internal const string HistoryMessage = "This saved game's history is damaged or exceeds the supported length. Choose another save.";

    internal static ReadOnlySpan<byte> Magic => "TWSV"u8;

    /// <summary>Encodes and compresses a save. The result is checked against <see cref="MaxSaveBytes"/> by the caller.</summary>
    public static byte[] Write(SavedGame save)
    {
        var body = EncodeBody(save);
        if (body.Length > MaxDecodedBytes)
        {
            throw new InvalidOperationException("This playthrough is too large to save (maximum 8 MiB uncompressed). You can continue playing, but this save could not be created.");
        }

        return Pack(body);
    }

    internal static byte[] Pack(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream();
        output.Write(Magic);
        output.WriteByte(Version);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)body.Length));
        output.Write(length);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(body);
        }

        return output.ToArray();
    }

    /// <summary>Decodes a save. Throws <see cref="InvalidDataException"/> with a player-facing message.</summary>
    public static SavedGame Read(ReadOnlySpan<byte> data) => Read(data, out _);

    internal static SavedGame Read(ReadOnlySpan<byte> data, out bool summaryDamaged) =>
        DecodeBody(Decompress(data), out summaryDamaged);

    internal static byte[] EncodeBody(SavedGame save) =>
        EncodeBody(ToPayload(save));

    internal static byte[] EncodeBody(SavePayload payload) =>
        MessagePackSerializer.Serialize(payload, SerializerOptions);

    internal static SavedGame DecodeBody(ReadOnlySpan<byte> body, out bool summaryDamaged)
    {
        var payload = Deserialize<SavePayload>(body, ValidateShape);
        if (string.IsNullOrEmpty(payload.StoryId))
        {
            throw new InvalidDataException(MalformedMessage);
        }

        if (payload.Choices is null || payload.Choices.Count > MaxChoices
            || payload.Choices.Any(key => string.IsNullOrEmpty(key) || key.Length > MaxChoiceKeyLength))
        {
            throw new InvalidDataException(HistoryMessage);
        }

        if (payload.Cursor < 0 || payload.Cursor > payload.Choices.Count)
        {
            throw new InvalidDataException(HistoryMessage);
        }

        if (payload.ReplayEvidence is { } evidence && !ReplayEvidence.IsValid(evidence))
        {
            throw new InvalidDataException(HistoryMessage);
        }

        var character = payload.Character is null ? null : FromPayload(payload.Character);
        SaveSummary? summary = null;
        summaryDamaged = false;
        if (payload.Summary is not null)
        {
            try
            {
                summary = FromPayload(Deserialize<SummaryPayload>(payload.Summary, ValidateSummary));
                summaryDamaged = summary is null;
            }
            catch (InvalidDataException)
            {
                summaryDamaged = true;
            }
        }

        return new SavedGame
        {
            StoryId = payload.StoryId,
            StoryVersion = payload.StoryVersion ?? "",
            ContentFingerprint = payload.ContentFingerprint ?? "",
            Seed = payload.Seed,
            Cursor = payload.Cursor,
            Choices = payload.Choices,
            Character = character,
            Summary = summary,
            ReplayEvidence = payload.ReplayEvidence,
        };
    }

    internal static SavePayload ToPayload(SavedGame save)
    {
        var payload = new SavePayload
        {
            StoryId = save.StoryId,
            StoryVersion = save.StoryVersion,
            ContentFingerprint = save.ContentFingerprint,
            Seed = save.Seed,
            Cursor = save.Cursor,
            Choices = save.Choices.ToList(),
            ReplayEvidence = save.ReplayEvidence,
        };
        if (save.Character is { } character)
        {
            payload.Character = new CharacterPayload
            {
                FirstName = character.FirstName,
                LastName = character.LastName,
                Pronouns = character.Pronouns,
                Preset = character.Preset,
                Stats = character.Stats.OrderBy(stat => stat.Key, StringComparer.Ordinal)
                    .Select(stat => new StatPayload { Id = stat.Key, Value = stat.Value })
                    .ToList(),
            };
        }

        if (save.Summary is { } summary)
        {
            payload.Summary = MessagePackSerializer.Serialize(new SummaryPayload
            {
                SceneTitle = summary.SceneTitle,
                SavedAtUtcTicks = summary.SavedAt.UtcTicks,
                OffsetMinutes = (int)summary.SavedAt.Offset.TotalMinutes,
                CharacterName = summary.CharacterName,
            }, SerializerOptions);
        }

        return payload;
    }

    private static CharacterSelection FromPayload(CharacterPayload payload)
    {
        if (payload.Stats is null || payload.Stats.Count > MaxCharacterStats)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stat in payload.Stats)
        {
            if (stat is null || stat.Id is null)
            {
                throw new InvalidDataException(MalformedMessage);
            }

            stats[stat.Id] = stat.Value;
        }

        return new CharacterSelection
        {
            FirstName = payload.FirstName,
            LastName = payload.LastName,
            Pronouns = payload.Pronouns,
            Preset = payload.Preset,
            Stats = new ReadOnlyDictionary<string, int>(stats),
        };
    }

    private static SaveSummary? FromPayload(SummaryPayload payload)
    {
        const int maxOffsetMinutes = 14 * 60;
        if (string.IsNullOrWhiteSpace(payload.SceneTitle) || payload.SavedAtUtcTicks <= 0
            || payload.SavedAtUtcTicks > DateTime.MaxValue.Ticks
            || payload.OffsetMinutes < -maxOffsetMinutes || payload.OffsetMinutes > maxOffsetMinutes)
        {
            return null;
        }

        var savedAt = new DateTimeOffset(payload.SavedAtUtcTicks, TimeSpan.Zero);
        var offset = TimeSpan.FromMinutes(payload.OffsetMinutes);
        var localTicks = savedAt.UtcTicks + offset.Ticks;
        return localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks
            ? null
            : new SaveSummary(payload.SceneTitle, savedAt.ToOffset(offset), payload.CharacterName);
    }

    private delegate void ValidateMessagePack(ref MessagePackReader reader);

    private static T Deserialize<T>(ReadOnlySpan<byte> data, ValidateMessagePack validate)
    {
        try
        {
            var bytes = data.ToArray();
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
            validate(ref reader);
            reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
            var value = MessagePackSerializer.Deserialize<T>(ref reader, SerializerOptions);
            if (!reader.End)
            {
                throw new InvalidDataException(MalformedMessage);
            }

            return value;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException
            or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException(MalformedMessage, ex);
        }
    }

    private static void ValidateShape(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        for (var field = 0; field < count; field++)
        {
            switch (field)
            {
                case 0:
                case 1:
                case 2:
                    ReadString(ref reader);
                    break;
                case 3:
                    reader.ReadUInt64();
                    break;
                case 4:
                    reader.ReadInt32();
                    break;
                case 5:
                    ValidateCharacter(ref reader);
                    break;
                case 6:
                    ValidateChoices(ref reader);
                    break;
                case 7:
                    if (!reader.TryReadNil())
                    {
                        reader.ReadBytes();
                    }

                    break;
                case 8:
                    if (!reader.TryReadNil() && reader.ReadBytes() is not { Length: ReplayEvidence.Length })
                    {
                        throw new InvalidDataException(HistoryMessage);
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (!reader.End)
        {
            throw new InvalidDataException(MalformedMessage);
        }
    }

    private static void ValidateCharacter(ref MessagePackReader reader)
    {
        if (reader.TryReadNil())
        {
            return;
        }

        var count = reader.ReadArrayHeader();
        if (count > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        for (var field = 0; field < count; field++)
        {
            if (field < 4)
            {
                ReadNullableString(ref reader);
            }
            else if (field == 4)
            {
                var stats = reader.ReadArrayHeader();
                if (stats > MaxCharacterStats)
                {
                    throw new InvalidDataException(MalformedMessage);
                }

                for (var i = 0; i < stats; i++)
                {
                    ValidateStat(ref reader);
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ValidateStat(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        for (var field = 0; field < count; field++)
        {
            switch (field)
            {
                case 0:
                    ReadString(ref reader);
                    break;
                case 1:
                    reader.ReadInt32();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private static void ValidateChoices(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count > MaxChoices)
        {
            throw new InvalidDataException(HistoryMessage);
        }

        for (var i = 0; i < count; i++)
        {
            var key = ReadString(ref reader);
            if (key.Length is 0 or > MaxChoiceKeyLength)
            {
                throw new InvalidDataException(HistoryMessage);
            }
        }
    }

    private static void ValidateSummary(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        for (var field = 0; field < count; field++)
        {
            switch (field)
            {
                case 0:
                    ReadString(ref reader);
                    break;
                case 1:
                    reader.ReadInt64();
                    break;
                case 2:
                    reader.ReadInt32();
                    break;
                case 3:
                    ReadNullableString(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (!reader.End)
        {
            throw new InvalidDataException(MalformedMessage);
        }
    }

    private static string ReadString(ref MessagePackReader reader) =>
        reader.ReadString() ?? throw new InvalidDataException(MalformedMessage);

    private static string? ReadNullableString(ref MessagePackReader reader) =>
        reader.TryReadNil() ? null : ReadString(ref reader);

    private static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException(EmptyMessage);
        }

        if (data.Length > MaxSaveBytes)
        {
            throw new InvalidDataException(TooLargeMessage);
        }

        if (!data.StartsWith(Magic))
        {
            var text = data.TrimStart(" \t\r\n"u8);
            throw new InvalidDataException(text.Length > 0 && text[0] == (byte)'{' ? UnsupportedMessage : MalformedMessage);
        }

        if (data.Length <= Magic.Length || data[Magic.Length] != Version)
        {
            throw new InvalidDataException(data.Length == Magic.Length ? MalformedMessage : UnsupportedMessage);
        }

        if (data.Length < HeaderLength)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(Magic.Length + 1, sizeof(uint)));
        if (declared > MaxDecodedBytes)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        var compressed = data[HeaderLength..].ToArray();
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = zlib.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)declared + 1 - body.Length))) > 0)
            {
                body.Write(chunk, 0, read);
                if (body.Length > (long)declared)
                {
                    throw new InvalidDataException(MalformedMessage);
                }
            }

            var decoded = body.ToArray();
            return decoded.Length == (long)declared && compressed.Length >= 4
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(compressed.Length - 4)) == Adler32(decoded)
                    ? decoded
                    : throw new InvalidDataException(MalformedMessage);
        }
        catch (InvalidDataException ex) when (ex.Message != MalformedMessage)
        {
            throw new InvalidDataException(MalformedMessage, ex);
        }
    }

    /// <summary>The zlib checksum (RFC 1950 section 9).</summary>
    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1, b = 0;
        while (data.Length > 0)
        {
            var chunk = data[..Math.Min(data.Length, 5552)];
            foreach (var value in chunk)
            {
                a += value;
                b += a;
            }

            a %= modulus;
            b %= modulus;
            data = data[chunk.Length..];
        }

        return (b << 16) | a;
    }
}
