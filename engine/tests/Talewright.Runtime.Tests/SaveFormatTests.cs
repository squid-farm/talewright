using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using MessagePack;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public class SaveFormatTests
{
    private const int ReplayChoiceIdLength = 8;
    private static readonly SavedGame Sample = new()
    {
        StoryId = "lost-relic",
        StoryVersion = "1.2.0",
        ContentFingerprint = "sha256:0123456789abcdef",
        Seed = 0xFEDC_BA98_7654_3210,
        Cursor = 3,
        Choices = ["c0", "go-north", "c0", "雪-é-🐉"],
        Character = new CharacterSelection
        {
            FirstName = "Mara",
            LastName = "",
            Pronouns = "she/her",
            Preset = null,
            Stats = new Dictionary<string, int>
            {
                ["str"] = 12,
                ["dex"] = -3,
                ["luck"] = int.MinValue,
                ["wis"] = int.MaxValue,
            },
        },
        Summary = new SaveSummary("The Old Gate",
            new DateTimeOffset(2026, 10, 23, 18, 30, 15, TimeSpan.FromMinutes(-330)), "Mara"),
    };
    private static SavedGame ReplaySample => Sample with
    {
        ReplayEvidence = CreateEvidence(),
        ReplayChoiceIdentities = Enumerable.Range(1, Sample.Choices.Count)
            .Select(value => Enumerable.Repeat((byte)value, ReplayChoiceIdLength).ToArray())
            .ToArray(),
    };

    [Fact]
    public void Write_then_read_round_trips_the_sample_and_container_header()
    {
        var bytes = SaveFormat.Write(Sample);

        Assert.Equal("TWSV"u8.ToArray(), bytes[..4]);
        Assert.Equal(SaveFormat.Version, bytes[4]);
        Assert.Equal((uint)SaveFormat.EncodeBody(Sample).Length,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4)));
        AssertSame(Sample, SaveFormat.Read(bytes));
    }

    [Fact]
    public void MessagePack_body_uses_the_frozen_positional_array_layout()
    {
        var sample = ReplaySample;
        var body = SaveFormat.EncodeBody(sample);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(body));

        Assert.Equal(10, reader.ReadArrayHeader());
        Assert.Equal(sample.StoryId, reader.ReadString());
        Assert.Equal(sample.StoryVersion, reader.ReadString());
        Assert.Equal(sample.ContentFingerprint, reader.ReadString());
        Assert.Equal(sample.Seed, reader.ReadUInt64());
        Assert.Equal(sample.Cursor, reader.ReadInt32());

        Assert.Equal(5, reader.ReadArrayHeader());
        Assert.Equal(sample.Character!.FirstName, reader.ReadString());
        Assert.Equal(sample.Character.LastName, reader.ReadString());
        Assert.Equal(sample.Character.Pronouns, reader.ReadString());
        Assert.True(reader.TryReadNil());
        Assert.Equal(sample.Character.Stats.Count, reader.ReadArrayHeader());
        foreach (var (id, value) in sample.Character.Stats.OrderBy(stat => stat.Key, StringComparer.Ordinal))
        {
            Assert.Equal(2, reader.ReadArrayHeader());
            Assert.Equal(id, reader.ReadString());
            Assert.Equal(value, reader.ReadInt32());
        }

        Assert.Equal(sample.Choices.Count, reader.ReadArrayHeader());
        foreach (var choice in sample.Choices)
        {
            Assert.Equal(choice, reader.ReadString());
        }

        var summaryBytes = reader.ReadBytes();
        Assert.NotNull(summaryBytes);
        var summaryReader = new MessagePackReader(summaryBytes.Value);
        Assert.Equal(4, summaryReader.ReadArrayHeader());
        Assert.Equal(sample.Summary!.SceneTitle, summaryReader.ReadString());
        Assert.Equal(sample.Summary.SavedAt.UtcTicks, summaryReader.ReadInt64());
        Assert.Equal((int)sample.Summary.SavedAt.Offset.TotalMinutes, summaryReader.ReadInt32());
        Assert.Equal(sample.Summary.CharacterName, summaryReader.ReadString());
        Assert.True(summaryReader.End);
        Assert.Equal(sample.ReplayEvidence, reader.ReadBytes()!.Value.ToArray());
        Assert.Equal(sample.ReplayChoiceIdentities!.Count, reader.ReadArrayHeader());
        foreach (var identity in sample.ReplayChoiceIdentities)
        {
            Assert.Equal(identity, reader.ReadBytes()!.Value.ToArray());
        }
        Assert.True(reader.End);
    }

    [Fact]
    public void Frozen_sample_file_still_decodes_to_the_same_save()
    {
        var path = SourcePath("engine", "tests", "Talewright.Runtime.Tests", "SaveSamples", "v4-sample.twsave");
        var frozen = File.ReadAllBytes(path);
        Assert.Equal("80E5E696135AF0F9F5FB254B63E8DB06F37B21C126435E3F7418A446FF94717C",
            Convert.ToHexString(SHA256.HashData(frozen)));
        AssertSame(Sample, SaveFormat.Read(frozen));
        var oldBody = Decompress(frozen);
        var currentBody = SaveFormat.EncodeBody(Sample);
        Assert.Equal(0x98, oldBody[0]);
        Assert.Equal(0x9a, currentBody[0]);
        Assert.Equal(oldBody[1..], currentBody[1..^2]);
        Assert.Equal([0xc0, 0xc0], currentBody[^2..]);
    }

    [Fact]
    public void Missing_trailing_fields_and_unknown_future_fields_are_supported()
    {
        var missingSummary = MessagePackBody((ref MessagePackWriter writer) =>
        {
            writer.WriteArrayHeader(7);
            writer.Write("story");
            writer.Write("1");
            writer.Write("fingerprint");
            writer.Write(7UL);
            writer.Write(0);
            writer.WriteNil();
            writer.WriteArrayHeader(0);
        });
        var missing = SaveFormat.DecodeBody(missingSummary, out var missingSummaryDamaged);
        Assert.Equal("story", missing.StoryId);
        Assert.Empty(missing.Choices);
        Assert.Null(missing.Summary);
        Assert.False(missingSummaryDamaged);

        var futureField = MessagePackBody((ref MessagePackWriter writer) =>
        {
            writer.WriteArrayHeader(11);
            writer.Write("story");
            writer.Write("1");
            writer.Write("fingerprint");
            writer.Write(7UL);
            writer.Write(0);
            writer.WriteNil();
            writer.WriteArrayHeader(0);
            writer.WriteNil();
            writer.WriteNil();
            writer.WriteNil();
            writer.Write("future");
        });
        Assert.Equal("story", SaveFormat.DecodeBody(futureField, out _).StoryId);
    }

    [Fact]
    public void Replay_evidence_round_trips_and_invalid_evidence_is_rejected()
    {
        var evidence = new byte[ReplayEvidence.Length];
        evidence[0] = 3;
        evidence[^1] = 42;
        var save = ReplaySample with { ReplayEvidence = evidence };
        Assert.Equal(evidence, SaveFormat.Read(SaveFormat.Write(save)).ReplayEvidence);

        foreach (var invalid in new[]
        {
            Array.Empty<byte>(), new byte[ReplayEvidence.Length - 1],
            new byte[ReplayEvidence.Length], new byte[ReplayEvidence.Length + 1],
        })
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                SaveFormat.Read(SaveFormat.Write(Sample with { ReplayEvidence = invalid })));
            Assert.Equal(SaveFormat.HistoryMessage, error.Message);
        }
    }

    [Fact]
    public void Damaged_optional_summary_does_not_prevent_loading_replay()
    {
        var payload = SaveFormat.ToPayload(Sample);
        payload.Summary = [0x91, 0xC1];

        var decoded = SaveFormat.DecodeBody(SaveFormat.EncodeBody(payload), out var summaryDamaged);

        Assert.True(summaryDamaged);
        Assert.Null(decoded.Summary);
        AssertSame(Sample with { Summary = null }, decoded);
    }

    [Fact]
    public void Int_min_value_summary_offset_is_dropped_instead_of_overflowing()
    {
        var summary = MessagePackBody((ref MessagePackWriter writer) =>
        {
            writer.WriteArrayHeader(4);
            writer.Write("Gate");
            writer.Write(DateTime.UtcNow.Ticks);
            writer.Write(int.MinValue);
            writer.WriteNil();
        });
        var payload = SaveFormat.ToPayload(Sample);
        payload.Summary = summary;

        var decoded = SaveFormat.DecodeBody(SaveFormat.EncodeBody(payload), out var summaryDamaged);

        Assert.True(summaryDamaged);
        Assert.Null(decoded.Summary);
    }

    [Fact]
    public void Randomized_saves_round_trip_exactly()
    {
        var random = new Random(417);
        for (var i = 0; i < 500; i++)
        {
            var save = RandomSave(random);
            AssertSame(save, SaveFormat.Read(SaveFormat.Write(save)));
        }
    }

    [Fact]
    public void Typical_choice_history_compresses_to_a_small_save()
    {
        var choices = Enumerable.Range(0, 204).Select(i => $"choice-{i:D3}").ToArray();
        var save = ReplaySample with
        {
            Choices = choices,
            Cursor = choices.Length,
            ReplayChoiceIdentities = choices.Select(_ => new byte[ReplayChoiceIdLength]).ToArray(),
        };
        Assert.True(SaveFormat.Write(save).Length < 4 * 1024);
    }

    [Fact]
    public void Every_truncation_and_single_bit_flip_is_rejected_or_harmless()
    {
        var bytes = SaveFormat.Write(Sample);
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes.AsSpan(0, length)));
        }

        for (var bit = 0; bit < bytes.Length * 8; bit++)
        {
            var copy = bytes.ToArray();
            copy[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                AssertSame(Sample, SaveFormat.Read(copy));
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void Random_bodies_and_mutated_bodies_only_fail_as_invalid_data()
    {
        var random = new Random(512);
        var body = SaveFormat.EncodeBody(Sample);
        for (var i = 0; i < 20_000; i++)
        {
            var data = i % 2 == 0 ? new byte[random.Next(0, 64)] : body.ToArray();
            if (i % 2 == 0)
            {
                random.NextBytes(data);
            }
            else
            {
                for (var edits = random.Next(1, 4); edits > 0; edits--)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }
            }

            try
            {
                SaveFormat.DecodeBody(data, out _);
                SaveFormat.Read(SaveFormat.Pack(data));
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void Malformed_messagepack_is_rejected_as_invalid_data()
    {
        foreach (var body in new byte[][]
        {
            [],
            [0xC0],
            [0x90],
            [0x90, 0x00],
        })
        {
            Assert.Throws<InvalidDataException>(() => SaveFormat.DecodeBody(body, out _));
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(SaveFormat.Pack(body)));
        }
    }

    [Fact]
    public void Choice_and_stat_collection_limits_are_checked_before_deserialization()
    {
        var tooManyChoices = SaveFormat.ToPayload(Sample);
        tooManyChoices.Choices = Enumerable.Repeat("c0", SaveFormat.MaxChoices + 1).ToList();
        Assert.Equal(SaveFormat.HistoryMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.DecodeBody(SaveFormat.EncodeBody(tooManyChoices), out _)).Message);

        var tooManyStats = SaveFormat.ToPayload(Sample);
        tooManyStats.Character!.Stats = Enumerable.Range(0, SaveFormat.MaxCharacterStats + 1)
            .Select(i => new StatPayload { Id = $"stat-{i}", Value = i })
            .ToList();
        Assert.Throws<InvalidDataException>(() => SaveFormat.DecodeBody(SaveFormat.EncodeBody(tooManyStats), out _));
    }

    [Fact]
    public void Write_rejects_bodies_over_the_decoded_size_limit()
    {
        var tooLarge = Sample with { StoryId = new string('s', SaveFormat.MaxDecodedBytes + 1) };
        var error = Assert.Throws<InvalidOperationException>(() => SaveFormat.Write(tooLarge));
        Assert.Contains("maximum 8 MiB uncompressed", error.Message);
    }

    [Fact]
    public void Reader_enforces_compressed_and_declared_decoded_size_limits()
    {
        Assert.Equal(SaveFormat.TooLargeMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(new byte[SaveFormat.MaxSaveBytes + 1])).Message);

        var bytes = SaveFormat.Write(Sample);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5, 4), SaveFormat.MaxDecodedBytes + 1);
        Assert.Equal(SaveFormat.MalformedMessage, Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes)).Message);
    }

    [Fact]
    public void Reader_requires_the_declared_length_and_adler32_trailer_to_match()
    {
        var bytes = SaveFormat.Write(Sample);
        var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5, 4), declared - 1);
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes));

        bytes = SaveFormat.Write(Sample);
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes));

        bytes = SaveFormat.Write(Sample);
        Array.Resize(ref bytes, bytes.Length + 1);
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes));
    }

    [Fact]
    public void Unsupported_versions_and_old_json_are_reported()
    {
        Assert.Equal(SaveFormat.UnsupportedMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read("{\"formatVersion\":2}"u8)).Message);

        var bytes = SaveFormat.Write(Sample);
        bytes[4] = 3;
        Assert.Equal(SaveFormat.UnsupportedMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes)).Message);

        bytes = SaveFormat.Write(Sample);
        bytes[4]++;
        Assert.Equal(SaveFormat.UnsupportedMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes)).Message);
    }

    private static SavedGame RandomSave(Random random)
    {
        var choices = Enumerable.Range(0, random.Next(0, 20))
            .Select(_ => "c" + random.Next(0, 10))
            .ToArray();
        CharacterSelection? character = random.Next(2) == 0 ? null : new CharacterSelection
        {
            FirstName = random.Next(2) == 0 ? null : "Name" + random.Next(100),
            LastName = "",
            Pronouns = "they/them",
            Preset = null,
            Stats = Enumerable.Range(0, random.Next(0, 8))
                .ToDictionary(i => "stat" + i, _ => random.Next(int.MinValue, int.MaxValue), StringComparer.Ordinal),
        };
        SaveSummary? summary = random.Next(2) == 0 ? null : new SaveSummary("Scene",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(random.Next(-840, 841))), null);
        var evidence = random.Next(2) == 0 ? null : new byte[ReplayEvidence.Length];
        if (evidence is not null)
        {
            random.NextBytes(evidence);
            evidence[0] = 3;
        }
        return Sample with
        {
            Seed = (ulong)random.NextInt64(),
            Cursor = random.Next(choices.Length + 1),
            Choices = choices,
            Character = character,
            Summary = summary,
            ReplayEvidence = evidence,
            ReplayChoiceIdentities = evidence is null ? null
                : choices.Select(_ => new byte[ReplayChoiceIdLength]).ToArray(),
        };
    }

    private static byte[] CreateEvidence()
    {
        var evidence = new byte[ReplayEvidence.Length];
        evidence[0] = 3;
        return evidence;
    }

    private delegate void MessagePackWriteAction(ref MessagePackWriter writer);

    private static byte[] MessagePackBody(MessagePackWriteAction write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data[9..]), CompressionMode.Decompress);
        using var body = new MemoryStream();
        zlib.CopyTo(body);
        return body.ToArray();
    }

    private static string SourcePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Talewright.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, Path.Combine(parts));
    }

    private static void AssertSame(SavedGame expected, SavedGame actual)
    {
        Assert.Equal(expected.StoryId, actual.StoryId);
        Assert.Equal(expected.StoryVersion, actual.StoryVersion);
        Assert.Equal(expected.ContentFingerprint, actual.ContentFingerprint);
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(expected.Choices, actual.Choices);
        Assert.Equal(expected.Character?.FirstName, actual.Character?.FirstName);
        Assert.Equal(expected.Character?.LastName, actual.Character?.LastName);
        Assert.Equal(expected.Character?.Pronouns, actual.Character?.Pronouns);
        Assert.Equal(expected.Character?.Preset, actual.Character?.Preset);
        Assert.Equal(expected.Character?.Stats, actual.Character?.Stats);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.ReplayEvidence, actual.ReplayEvidence);
        Assert.Equal(expected.ReplayChoiceIdentities?.Select(identity => Convert.ToHexString(identity)),
            actual.ReplayChoiceIdentities?.Select(identity => Convert.ToHexString(identity)));
    }
}
