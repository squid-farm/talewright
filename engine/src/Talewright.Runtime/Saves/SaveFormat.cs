using System.Collections.ObjectModel;
using System.IO.Compression;
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
}

/// <summary>
/// Reads and writes save files: a short header followed by a zlib-compressed protobuf-wire message.
/// The field layout is documented in docs/developer/save-format.md and save-format.proto.
/// </summary>
public static class SaveFormat
{
    public const int Version = 3;
    /// <summary>File extension for saves on disk and exported backups.</summary>
    public const string FileExtension = ".twsave";
    public const int MaxSaveBytes = 2 * 1024 * 1024;
    public const int MaxDecodedBytes = 8 * 1024 * 1024;
    public const int MaxChoices = 10_000;
    public const int MaxChoiceKeyLength = 512;
    public const int MaxCharacterStats = 1_000;

    internal const string MalformedMessage = "This saved game is malformed or damaged. Choose another save or start a new game.";
    internal const string UnsupportedMessage = "This saved game uses an unsupported format. Open it with the player version that created it.";
    internal const string TooLargeMessage = "This saved game is too large to load (maximum 2 MiB). Choose another save.";
    internal const string EmptyMessage = "This saved game is empty. Choose another save or start a new game.";
    internal const string HistoryMessage = "This saved game's history is damaged or exceeds the supported length. Choose another save.";

    internal static ReadOnlySpan<byte> Magic => "TWSV"u8;

    /// <summary>Encodes and compresses a save. The result is checked against <see cref="MaxSaveBytes"/> by the caller.</summary>
    public static byte[] Write(SavedGame save) => Pack(EncodeBody(save));

    internal static byte[] Pack(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream();
        output.Write(Magic);
        output.WriteByte(Version);
        var length = new ProtoWriter();
        length.WriteVarint((uint)body.Length);
        output.Write(length.WrittenSpan);
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

    internal static byte[] EncodeBody(SavedGame save)
    {
        var writer = new ProtoWriter();
        writer.WriteString(SaveFields.StoryId, save.StoryId);
        writer.WriteString(SaveFields.StoryVersion, save.StoryVersion);
        writer.WriteString(SaveFields.ContentFingerprint, save.ContentFingerprint);
        writer.WriteFixed64(SaveFields.Seed, save.Seed);
        writer.WriteUInt64(SaveFields.Cursor, (uint)save.Cursor);
        if (save.Character is { } character)
        {
            writer.WriteMessage(SaveFields.Character, w => WriteCharacter(w, character));
        }

        var keys = new Dictionary<string, uint>(StringComparer.Ordinal);
        var indices = new uint[save.Choices.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            var key = save.Choices[i];
            if (!keys.TryGetValue(key, out var index))
            {
                index = (uint)keys.Count;
                keys.Add(key, index);
                writer.WriteString(SaveFields.ChoiceKeys, key);
            }

            indices[i] = index;
        }

        writer.WritePackedUInt32(SaveFields.Choices, indices);
        if (save.Summary is { } summary)
        {
            writer.WriteMessage(SaveFields.Summary, w => WriteSummary(w, summary));
        }

        return writer.WrittenSpan.ToArray();
    }

    internal static SavedGame DecodeBody(ReadOnlySpan<byte> body, out bool summaryDamaged)
    {
        var reader = new ProtoReader(body);
        string storyId = "", storyVersion = "", fingerprint = "";
        ulong seed = 0;
        uint cursor = 0;
        CharacterBuilder? character = null;
        SummaryBuilder? summary = null;
        summaryDamaged = false;
        var keys = new List<string>();
        var indices = new List<uint>();
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field, wireType)
            {
                case (SaveFields.StoryId, ProtoWriter.LengthDelimited):
                    storyId = reader.ReadString();
                    break;
                case (SaveFields.StoryVersion, ProtoWriter.LengthDelimited):
                    storyVersion = reader.ReadString();
                    break;
                case (SaveFields.ContentFingerprint, ProtoWriter.LengthDelimited):
                    fingerprint = reader.ReadString();
                    break;
                case (SaveFields.Seed, ProtoWriter.Fixed64):
                    seed = reader.ReadFixed64();
                    break;
                case (SaveFields.Cursor, ProtoWriter.Varint):
                    cursor = reader.ReadUInt32();
                    break;
                case (SaveFields.Character, ProtoWriter.LengthDelimited):
                    // Repeated embedded messages merge, as the protobuf specification requires.
                    character ??= new CharacterBuilder();
                    character.Merge(reader.ReadBytes());
                    break;
                case (SaveFields.ChoiceKeys, ProtoWriter.LengthDelimited):
                    var key = reader.ReadString();
                    if (keys.Count >= MaxChoices || key.Length is 0 or > MaxChoiceKeyLength)
                    {
                        throw new InvalidDataException(HistoryMessage);
                    }

                    keys.Add(key);
                    break;
                case (SaveFields.Choices, ProtoWriter.LengthDelimited):
                    var packed = new ProtoReader(reader.ReadBytes());
                    while (!packed.End)
                    {
                        AddChoice(indices, packed.ReadUInt32());
                    }

                    break;
                case (SaveFields.Choices, ProtoWriter.Varint):
                    // Parsers must accept unpacked encodings of packable repeated fields.
                    AddChoice(indices, reader.ReadUInt32());
                    break;
                case (SaveFields.Summary, ProtoWriter.LengthDelimited):
                    var bytes = reader.ReadBytes();
                    if (!summaryDamaged)
                    {
                        try
                        {
                            summary ??= new SummaryBuilder();
                            summary.Merge(bytes);
                        }
                        catch (InvalidDataException)
                        {
                            summaryDamaged = true;
                        }
                    }

                    break;
                default:
                    // Unknown fields, and known fields with an unexpected wire type, are skipped like Google's parsers.
                    reader.Skip(wireType);
                    break;
            }
        }

        if (storyId.Length == 0)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        var choices = new string[indices.Count];
        for (var i = 0; i < choices.Length; i++)
        {
            choices[i] = indices[i] < keys.Count ? keys[(int)indices[i]] : throw new InvalidDataException(HistoryMessage);
        }

        if (cursor > choices.Length)
        {
            throw new InvalidDataException(HistoryMessage);
        }

        SaveSummary? saveSummary = null;
        if (summary is not null && !summaryDamaged)
        {
            saveSummary = summary.Build();
            summaryDamaged = saveSummary is null;
        }

        return new SavedGame
        {
            StoryId = storyId,
            StoryVersion = storyVersion,
            ContentFingerprint = fingerprint,
            Seed = seed,
            Cursor = (int)cursor,
            Choices = choices,
            Character = character?.Build(),
            Summary = saveSummary,
        };
    }

    private static void AddChoice(List<uint> indices, uint index)
    {
        if (indices.Count >= MaxChoices)
        {
            throw new InvalidDataException(HistoryMessage);
        }

        indices.Add(index);
    }

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
            // Earlier players wrote JSON saves; report them as an older format rather than as damage.
            var text = data.TrimStart(" \t\r\n"u8);
            throw new InvalidDataException(text.Length > 0 && text[0] == (byte)'{' ? UnsupportedMessage : MalformedMessage);
        }

        if (data.Length == Magic.Length || data[Magic.Length] != Version)
        {
            throw new InvalidDataException(data.Length == Magic.Length ? MalformedMessage : UnsupportedMessage);
        }

        var header = new ProtoReader(data[(Magic.Length + 1)..]);
        var declared = header.ReadVarint();
        if (declared > MaxDecodedBytes)
        {
            throw new InvalidDataException(MalformedMessage);
        }

        var compressed = data[(Magic.Length + 1 + header.Position)..].ToArray();
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
            using var body = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            // Read at most one byte past the declared length so a decompression bomb stops early.
            while ((read = zlib.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)declared + 1 - body.Length))) > 0)
            {
                body.Write(chunk, 0, read);
                if (body.Length > (long)declared)
                {
                    throw new InvalidDataException(MalformedMessage);
                }
            }

            // .NET accepts a truncated zlib stream, even one missing its Adler-32 trailer. The declared length catches
            // truncated data, and requiring the file to end with the body's checksum catches a missing or extra trailer.
            var decoded = body.ToArray();
            return decoded.Length == (long)declared
                && compressed.Length >= 4
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
            // 5552 is the most bytes that can be summed before b could overflow 32 bits.
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

    private static void WriteCharacter(ProtoWriter writer, CharacterSelection character)
    {
        WriteOptional(writer, SaveFields.CharacterFirstName, character.FirstName);
        WriteOptional(writer, SaveFields.CharacterLastName, character.LastName);
        WriteOptional(writer, SaveFields.CharacterPronouns, character.Pronouns);
        WriteOptional(writer, SaveFields.CharacterPreset, character.Preset);
        foreach (var (id, value) in character.Stats.OrderBy(stat => stat.Key, StringComparer.Ordinal))
        {
            writer.WriteMessage(SaveFields.CharacterStats, stat =>
            {
                stat.WriteString(SaveFields.StatId, id);
                stat.WriteSInt32(SaveFields.StatValue, value);
            });
        }
    }

    private static void WriteSummary(ProtoWriter writer, SaveSummary summary)
    {
        writer.WriteString(SaveFields.SummarySceneTitle, summary.SceneTitle);
        writer.WriteUInt64(SaveFields.SummarySavedAtUtcTicks, (ulong)summary.SavedAt.UtcTicks);
        writer.WriteSInt32(SaveFields.SummaryOffsetMinutes, (int)summary.SavedAt.Offset.TotalMinutes);
        WriteOptional(writer, SaveFields.SummaryCharacterName, summary.CharacterName);
    }

    private static void WriteOptional(ProtoWriter writer, int field, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(field, value);
        }
    }

    private sealed class CharacterBuilder
    {
        private string? _firstName, _lastName, _pronouns, _preset;
        private readonly Dictionary<string, int> _stats = new(StringComparer.Ordinal);

        public void Merge(ReadOnlySpan<byte> data)
        {
            var reader = new ProtoReader(data);
            while (!reader.End)
            {
                var (field, wireType) = reader.ReadTag();
                switch (field, wireType)
                {
                    case (SaveFields.CharacterFirstName, ProtoWriter.LengthDelimited):
                        _firstName = reader.ReadString();
                        break;
                    case (SaveFields.CharacterLastName, ProtoWriter.LengthDelimited):
                        _lastName = reader.ReadString();
                        break;
                    case (SaveFields.CharacterPronouns, ProtoWriter.LengthDelimited):
                        _pronouns = reader.ReadString();
                        break;
                    case (SaveFields.CharacterPreset, ProtoWriter.LengthDelimited):
                        _preset = reader.ReadString();
                        break;
                    case (SaveFields.CharacterStats, ProtoWriter.LengthDelimited):
                        MergeStat(reader.ReadBytes());
                        break;
                    default:
                        reader.Skip(wireType);
                        break;
                }
            }
        }

        private void MergeStat(ReadOnlySpan<byte> data)
        {
            var reader = new ProtoReader(data);
            string id = "";
            var value = 0;
            while (!reader.End)
            {
                var (field, wireType) = reader.ReadTag();
                switch (field, wireType)
                {
                    case (SaveFields.StatId, ProtoWriter.LengthDelimited):
                        id = reader.ReadString();
                        break;
                    case (SaveFields.StatValue, ProtoWriter.Varint):
                        value = reader.ReadSInt32();
                        break;
                    default:
                        reader.Skip(wireType);
                        break;
                }
            }

            if (!_stats.ContainsKey(id) && _stats.Count >= MaxCharacterStats)
            {
                throw new InvalidDataException(MalformedMessage);
            }

            // Like protobuf maps, a repeated stat id keeps the last value.
            _stats[id] = value;
        }

        public CharacterSelection Build() => new()
        {
            FirstName = _firstName,
            LastName = _lastName,
            Pronouns = _pronouns,
            Preset = _preset,
            Stats = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_stats, StringComparer.Ordinal)),
        };
    }

    private sealed class SummaryBuilder
    {
        private string _sceneTitle = "";
        private ulong _savedAtUtcTicks;
        private int _offsetMinutes;
        private string? _characterName;

        public void Merge(ReadOnlySpan<byte> data)
        {
            var reader = new ProtoReader(data);
            while (!reader.End)
            {
                var (field, wireType) = reader.ReadTag();
                switch (field, wireType)
                {
                    case (SaveFields.SummarySceneTitle, ProtoWriter.LengthDelimited):
                        _sceneTitle = reader.ReadString();
                        break;
                    case (SaveFields.SummarySavedAtUtcTicks, ProtoWriter.Varint):
                        _savedAtUtcTicks = reader.ReadVarint();
                        break;
                    case (SaveFields.SummaryOffsetMinutes, ProtoWriter.Varint):
                        _offsetMinutes = reader.ReadSInt32();
                        break;
                    case (SaveFields.SummaryCharacterName, ProtoWriter.LengthDelimited):
                        _characterName = reader.ReadString();
                        break;
                    default:
                        reader.Skip(wireType);
                        break;
                }
            }
        }

        /// <summary>Returns null when the details are present but unusable.</summary>
        public SaveSummary? Build()
        {
            if (string.IsNullOrWhiteSpace(_sceneTitle) || _savedAtUtcTicks == 0 || _savedAtUtcTicks > (ulong)DateTime.MaxValue.Ticks
                || Math.Abs(_offsetMinutes) > 14 * 60)
            {
                return null;
            }

            var savedAt = new DateTimeOffset((long)_savedAtUtcTicks, TimeSpan.Zero);
            var offset = TimeSpan.FromMinutes(_offsetMinutes);
            var local = savedAt.UtcTicks + offset.Ticks;
            return local < DateTime.MinValue.Ticks || local > DateTime.MaxValue.Ticks
                ? null
                : new(_sceneTitle, savedAt.ToOffset(offset), _characterName);
        }
    }
}

/// <summary>Field numbers. They are permanent: never reuse or retype one; reserve removed numbers.</summary>
internal static class SaveFields
{
    public const int StoryId = 1;
    public const int StoryVersion = 2;
    public const int ContentFingerprint = 3;
    public const int Seed = 4;
    public const int Cursor = 5;
    public const int Character = 6;
    public const int ChoiceKeys = 7;
    public const int Choices = 8;
    public const int Summary = 9;

    public const int CharacterFirstName = 1;
    public const int CharacterLastName = 2;
    public const int CharacterPronouns = 3;
    public const int CharacterPreset = 4;
    public const int CharacterStats = 5;

    public const int StatId = 1;
    public const int StatValue = 2;

    public const int SummarySceneTitle = 1;
    public const int SummarySavedAtUtcTicks = 2;
    public const int SummaryOffsetMinutes = 3;
    public const int SummaryCharacterName = 4;
}
