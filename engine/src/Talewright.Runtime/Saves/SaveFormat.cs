using System.Buffers;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using MessagePack;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Saves;

/// <summary>The replay inputs and optional continuation stored in a save.</summary>
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
    public IReadOnlyList<byte[]>? ReplayChoiceIdentities { get; init; }
    public SaveContinuation? Continuation { get; init; }
    public int TalemarkVersion { get; init; } = 1;
}

/// <summary>Reads and writes save files: a short header followed by a zlib-compressed MessagePack array.</summary>
public static class SaveFormat
{
    public const int Version = 6;
    /// <summary>File extension for saves on disk and exported backups.</summary>
    public const string FileExtension = ".twsave";
    // Damaged or hostile-file guards, not playthrough limits. The decoded bound is within 2x of the largest
    // .NET array; a 1 GiB body holds roughly 1.5 million choices of a prose-heavy story.
    public const int MaxSaveBytes = 512 * 1024 * 1024;
    public const int MaxDecodedBytes = 1024 * 1024 * 1024;
    public const int MaxChoiceKeyLength = 512;

    private const int HeaderLength = 4 + 1 + sizeof(uint);
    private const int MaxArrayFields = 32;
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    internal const string MalformedMessage = "This saved game is malformed or damaged. Choose another save or start a new game.";
    internal const string UnsupportedMessage = "This saved game uses an unsupported format. Open it with the player version that created it.";
    internal static readonly string TooLargeMessage =
        $"This saved game is too large to load (maximum {MaxSaveBytes / 1024 / 1024} MiB). Choose another save.";
    internal const string EmptyMessage = "This saved game is empty. Choose another save or start a new game.";
    internal const string HistoryMessage = "This saved game's history is damaged. Choose another save.";

    internal static ReadOnlySpan<byte> Magic => "TWSV"u8;

    /// <summary>Encodes and compresses a save. The result is checked against <see cref="MaxSaveBytes"/> by the caller.</summary>
    public static byte[] Write(SavedGame save) => Write(save, MaxDecodedBytes);

    internal static byte[] Write(SavedGame save, int maxDecodedBytes)
    {
        var body = EncodeBody(save);
        if (body.Length > maxDecodedBytes)
        {
            throw new InvalidOperationException($"This playthrough is too large to save (maximum {maxDecodedBytes / 1024 / 1024} MiB uncompressed). You can continue playing, but this save could not be created.");
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

    internal static SavedGame Read(ReadOnlySpan<byte> data, out bool summaryDamaged)
    {
        var body = Decompress(data);
        var version = data[Magic.Length];
        return DecodeBody(version < 6 ? StripLegacyFields(body, version == 4 ? 10 : 11) : body,
            out summaryDamaged, requireVersion: version == 6);
    }

    internal static int DecodedBodyLength(ReadOnlySpan<byte> data) => Decompress(data).Length;

    internal static byte[] EncodeBody(SavedGame save) =>
        EncodeBody(ToPayload(save));

    internal static byte[] EncodeBody(SavePayload payload) =>
        MessagePackSerializer.Serialize(payload, SerializerOptions);

    internal static SavedGame DecodeBody(ReadOnlySpan<byte> body, out bool summaryDamaged, bool requireVersion = false)
    {
        var payload = Deserialize<SavePayload>(body, requireVersion ? ValidateCurrentShape : ValidateShape);
        if (payload.TalemarkVersion is not (1 or 2))
        {
            throw new InvalidDataException(MalformedMessage);
        }
        if (string.IsNullOrEmpty(payload.StoryId))
        {
            throw new InvalidDataException(MalformedMessage);
        }

        if (payload.Choices is null
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

        if (payload.ReplayChoiceIdentities is { } identities
            && (identities.Count != payload.Choices.Count
                || identities.Any(identity => identity is null
                    || identity.Length is not (ChoiceReplayIdentity.CompactLength or ChoiceReplayIdentity.FullLength))))
        {
            throw new InvalidDataException(HistoryMessage);
        }

        if ((payload.ReplayEvidence is null) != (payload.ReplayChoiceIdentities is null))
        {
            throw new InvalidDataException(HistoryMessage);
        }

        if (payload.Continuation is { } continuation)
        {
            ContinuationCheckpoint.ValidatePayload(continuation);
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
            ReplayChoiceIdentities = payload.ReplayChoiceIdentities,
            Continuation = payload.Continuation is null ? null : new SaveContinuation(payload.Continuation),
            TalemarkVersion = payload.TalemarkVersion,
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
            ReplayChoiceIdentities = save.ReplayChoiceIdentities?.Select(identity => identity.ToArray()).ToList(),
            Continuation = save.Continuation?.Payload,
            TalemarkVersion = save.TalemarkVersion,
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
        if (payload.Stats is null)
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

        var hasEvidence = false;
        var hasIdentities = false;
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
                    hasEvidence = !reader.TryReadNil();
                    if (hasEvidence && reader.ReadBytes() is not { Length: ReplayEvidence.Length })
                    {
                        throw new InvalidDataException(HistoryMessage);
                    }

                    break;
                case 9:
                    hasIdentities = !reader.TryReadNil();
                    if (hasIdentities)
                    {
                        var identityCount = reader.ReadArrayHeader();
                        for (var i = 0; i < identityCount; i++)
                        {
                            var identity = reader.ReadBytes();
                            if (identity is not { Length: ChoiceReplayIdentity.CompactLength or ChoiceReplayIdentity.FullLength })
                            {
                                throw new InvalidDataException(HistoryMessage);
                            }
                        }
                    }

                    break;
                case 10:
                    ValidateContinuation(ref reader, hasEvidence || hasIdentities);
                    break;
                case 11:
                    reader.ReadInt32();
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

    private static void ValidateCurrentShape(ref MessagePackReader reader)
    {
        var copy = reader;
        if (copy.ReadArrayHeader() < 12)
        {
            throw new InvalidDataException(MalformedMessage);
        }
        ValidateShape(ref reader);
    }

    private static void ValidateContinuation(ref MessagePackReader reader, bool hasEvidence)
    {
        if (reader.TryReadNil())
        {
            return;
        }

        var tree = reader;
        ValidateContinuationTree(ref tree, 0);

        var fields = reader.ReadArrayHeader();
        if (fields is < 4 or > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        ValidateCheckpoint(ref reader);
        var frames = reader.ReadArrayHeader();
        for (var i = 0; i < frames; i++)
        {
            reader.Skip();
        }

        var hasBase = !reader.TryReadNil();
        if (hasBase)
        {
            ValidateCheckpoint(ref reader);
        }
        var baseLength = reader.ReadInt32();
        if (baseLength < 0 || baseLength > frames
            || (hasBase && hasEvidence) || (!hasBase && baseLength != 0))
        {
            throw new InvalidDataException(MalformedMessage);
        }
        for (var field = 4; field < fields; field++)
        {
            reader.Skip();
        }
    }

    private static void ValidateContinuationTree(ref MessagePackReader reader, int depth)
    {
        if (depth >= SerializerOptions.Security.MaximumObjectGraphDepth)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                throw new InvalidDataException(MalformedMessage);
            case MessagePackType.Array:
                var count = reader.ReadArrayHeader();
                if (count > reader.Sequence.Length - reader.Consumed)
                {
                    throw new InvalidDataException(MalformedMessage);
                }
                for (var i = 0; i < count; i++)
                {
                    ValidateContinuationTree(ref reader, depth + 1);
                }
                break;
            default:
                reader.Skip();
                break;
        }
    }

    private static void ValidateCheckpoint(ref MessagePackReader reader)
    {
        var fields = reader.ReadArrayHeader();
        if (fields is < 13 or > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }
        for (var field = 0; field < fields; field++)
        {
            if (field == 2)
            {
                var characters = reader.ReadArrayHeader();
                for (var i = 0; i < characters; i++)
                {
                    ValidateCheckpointCharacter(ref reader);
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ValidateCheckpointCharacter(ref MessagePackReader reader)
    {
        var fields = reader.ReadArrayHeader();
        if (fields is < 6 or > MaxArrayFields)
        {
            throw new InvalidDataException(MalformedMessage);
        }
        for (var field = 0; field < fields; field++)
        {
            if (field == 4)
            {
                var stats = reader.ReadArrayHeader();
                for (var i = 0; i < stats; i++)
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static byte[] StripLegacyFields(ReadOnlySpan<byte> body, int retainedFields)
    {
        var bytes = body.ToArray();
        try
        {
            var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
            var fields = reader.ReadArrayHeader();
            if (fields > MaxArrayFields)
            {
                throw new InvalidDataException(MalformedMessage);
            }
            if (fields <= retainedFields && retainedFields == 10)
            {
                return bytes;
            }

            var output = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(output);
            writer.WriteArrayHeader(Math.Min(fields, retainedFields));
            for (var field = 0; field < fields; field++)
            {
                if (field == 10 && retainedFields == 11)
                {
                    StripLegacyContinuation(ref reader, ref writer);
                }
                else
                {
                    var raw = reader.ReadRaw();
                    if (field < retainedFields) writer.WriteRaw(raw);
                }
            }

            static void StripLegacyContinuation(ref MessagePackReader reader, ref MessagePackWriter writer)
            {
                var tree = reader;
                ValidateContinuationTree(ref tree, 0);
                if (reader.TryReadNil())
                {
                    writer.WriteNil();
                    return;
                }
                var fields = reader.ReadArrayHeader();
                if (fields > MaxArrayFields) throw new InvalidDataException(MalformedMessage);
                writer.WriteArrayHeader(fields);
                for (var field = 0; field < fields; field++)
                {
                    if (field is 0 or 2) StripLegacyCheckpoint(ref reader, ref writer);
                    else writer.WriteRaw(reader.ReadRaw());
                }
            }

            static void StripLegacyCheckpoint(ref MessagePackReader reader, ref MessagePackWriter writer)
            {
                if (reader.TryReadNil())
                {
                    writer.WriteNil();
                    return;
                }
                var fields = reader.ReadArrayHeader();
                if (fields > MaxArrayFields) throw new InvalidDataException(MalformedMessage);
                writer.WriteArrayHeader(fields);
                for (var field = 0; field < fields; field++)
                {
                    if (field == 2)
                    {
                        var characters = reader.ReadArrayHeader();
                        writer.WriteArrayHeader(characters);
                        for (var i = 0; i < characters; i++) StripLegacyCharacter(ref reader, ref writer);
                    }
                    else if (field is 3 or 4 or 5 or 7) StripLegacyPairs(ref reader, ref writer);
                    else writer.WriteRaw(reader.ReadRaw());
                }
            }

            static void StripLegacyCharacter(ref MessagePackReader reader, ref MessagePackWriter writer)
            {
                var fields = reader.ReadArrayHeader();
                if (fields > MaxArrayFields) throw new InvalidDataException(MalformedMessage);
                writer.WriteArrayHeader(fields);
                for (var field = 0; field < fields; field++)
                {
                    if (field is 4 or 5) StripLegacyPairs(ref reader, ref writer);
                    else writer.WriteRaw(reader.ReadRaw());
                }
            }

            static void StripLegacyPairs(ref MessagePackReader reader, ref MessagePackWriter writer)
            {
                var count = reader.ReadArrayHeader();
                writer.WriteArrayHeader(count);
                for (var i = 0; i < count; i++)
                {
                    var fields = reader.ReadArrayHeader();
                    writer.WriteArrayHeader(Math.Min(fields, 2));
                    for (var field = 0; field < fields; field++)
                    {
                        var raw = reader.ReadRaw();
                        if (field < 2) writer.WriteRaw(raw);
                    }
                }
            }
            writer.Flush();
            if (!reader.End)
            {
                throw new InvalidDataException(MalformedMessage);
            }
            return output.WrittenSpan.ToArray();
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

        if (data.Length <= Magic.Length || data[Magic.Length] is not (4 or 5 or Version))
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
