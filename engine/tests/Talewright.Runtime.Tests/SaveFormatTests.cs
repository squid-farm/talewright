using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

/// <summary>
/// Checks the hand-written save format against the protobuf wire format, using Google.Protobuf's
/// CodedInputStream and CodedOutputStream (test-only) as the reference implementation.
/// </summary>
public partial class SaveFormatTests
{
    private const string SampleName = "v3-sample.twsave";

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
            Stats = new Dictionary<string, int> { ["str"] = 12, ["dex"] = -3, ["luck"] = int.MinValue, ["wis"] = int.MaxValue },
        },
        Summary = new SaveSummary("The Old Gate", new DateTimeOffset(2026, 10, 23, 18, 30, 15, TimeSpan.FromMinutes(-330)), "Mara"),
    };

    [Fact]
    public void Google_reader_decodes_our_body_field_by_field()
    {
        var input = new CodedInputStream(SaveFormat.EncodeBody(Sample));
        var keys = new List<string>();
        var indices = new List<uint>();
        var seen = new List<int>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            seen.Add(field);
            switch (field)
            {
                case 1: Assert.Equal(Sample.StoryId, input.ReadString()); break;
                case 2: Assert.Equal(Sample.StoryVersion, input.ReadString()); break;
                case 3: Assert.Equal(Sample.ContentFingerprint, input.ReadString()); break;
                case 4: Assert.Equal(Sample.Seed, input.ReadFixed64()); break;
                case 5: Assert.Equal((uint)Sample.Cursor, input.ReadUInt32()); break;
                case 6: AssertGoogleCharacter(input.ReadBytes()); break;
                case 7: keys.Add(input.ReadString()); break;
                case 8:
                    Assert.Equal(WireFormat.WireType.LengthDelimited, WireFormat.GetTagWireType(tag));
                    var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                    while (!packed.IsAtEnd)
                    {
                        indices.Add(packed.ReadUInt32());
                    }

                    break;
                case 9: AssertGoogleSummary(input.ReadBytes()); break;
                default: Assert.Fail($"Unexpected field {field}"); break;
            }
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 7, 7, 8, 9], seen);
        Assert.Equal(Sample.Choices, indices.Select(index => keys[(int)index]));
    }

    [Fact]
    public void Our_reader_decodes_a_google_written_body_with_unknown_fields_and_unpacked_choices()
    {
        var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        WriteUnknownFields(output, 1000);
        output.WriteTag(2, WireFormat.WireType.LengthDelimited);
        output.WriteString("v-old");
        output.WriteTag(1, WireFormat.WireType.LengthDelimited);
        output.WriteString("story");
        output.WriteTag(2, WireFormat.WireType.LengthDelimited);
        output.WriteString("v2"); // Repeated scalars: the last value wins.
        output.WriteTag(3, WireFormat.WireType.LengthDelimited);
        output.WriteString("fp");
        output.WriteTag(4, WireFormat.WireType.Fixed64);
        output.WriteFixed64(ulong.MaxValue);
        output.WriteTag(7, WireFormat.WireType.LengthDelimited);
        output.WriteString("a");
        output.WriteTag(7, WireFormat.WireType.LengthDelimited);
        output.WriteString("b");
        foreach (var index in new uint[] { 1, 0 })
        {
            output.WriteTag(8, WireFormat.WireType.Varint);
            output.WriteUInt32(index);
        }

        output.WriteTag(8, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(1, 1));
        output.WriteTag(5, WireFormat.WireType.Varint);
        output.WriteUInt32(4);
        // The character arrives in two parts, which protobuf merges.
        output.WriteTag(6, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(GoogleMessage(character =>
        {
            character.WriteTag(1, WireFormat.WireType.LengthDelimited);
            character.WriteString("Ana");
            WriteGoogleStat(character, "str", 1);
            WriteUnknownFields(character, 50);
        }));
        output.WriteTag(6, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(GoogleMessage(character =>
        {
            character.WriteTag(2, WireFormat.WireType.LengthDelimited);
            character.WriteString("Vale");
            WriteGoogleStat(character, "str", -7);
            WriteGoogleStat(character, "dex", 5);
        }));
        output.WriteTag(9, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(GoogleMessage(summary =>
        {
            summary.WriteTag(1, WireFormat.WireType.LengthDelimited);
            summary.WriteString("Gate");
            summary.WriteTag(2, WireFormat.WireType.Varint);
            summary.WriteUInt64((ulong)new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks);
            summary.WriteTag(3, WireFormat.WireType.Varint);
            summary.WriteSInt32(60);
            WriteUnknownFields(summary, 70);
        }));
        WriteUnknownFields(output, 536_870_911);
        output.Flush();

        var save = SaveFormat.DecodeBody(stream.ToArray(), out var summaryDamaged);

        Assert.False(summaryDamaged);
        Assert.Equal("story", save.StoryId);
        Assert.Equal("v2", save.StoryVersion);
        Assert.Equal("fp", save.ContentFingerprint);
        Assert.Equal(ulong.MaxValue, save.Seed);
        Assert.Equal(4, save.Cursor);
        Assert.Equal(["b", "a", "b", "b"], save.Choices);
        Assert.Equal("Ana", save.Character!.FirstName);
        Assert.Equal("Vale", save.Character.LastName);
        Assert.Null(save.Character.Pronouns);
        Assert.Equal(new Dictionary<string, int> { ["dex"] = 5, ["str"] = -7 }, save.Character.Stats);
        Assert.Equal(new SaveSummary("Gate", new DateTimeOffset(2026, 1, 2, 1, 0, 0, TimeSpan.FromHours(1)), null), save.Summary);
        Assert.Equal(TimeSpan.FromHours(1), save.Summary!.SavedAt.Offset);
    }

    [Fact]
    public void Our_written_scalars_match_google_byte_for_byte()
    {
        var values = new ulong[] { 0, 1, 127, 128, 300, 16_383, 16_384, uint.MaxValue, (ulong)uint.MaxValue + 1, long.MaxValue, ulong.MaxValue };
        var signed = new[] { 0, 1, -1, 2, -2, 63, -64, int.MaxValue, int.MinValue };
        var ours = new ProtoWriter();
        var stream = new MemoryStream();
        var google = new CodedOutputStream(stream);
        foreach (var value in values)
        {
            ours.WriteUInt64(3, value);
            google.WriteTag(3, WireFormat.WireType.Varint);
            google.WriteUInt64(value);
            ours.WriteFixed64(4, value);
            google.WriteTag(4, WireFormat.WireType.Fixed64);
            google.WriteFixed64(value);
        }

        foreach (var value in signed)
        {
            ours.WriteSInt32(5, value);
            google.WriteTag(5, WireFormat.WireType.Varint);
            google.WriteSInt32(value);
            ours.WriteSInt64(6, value * 3L);
            google.WriteTag(6, WireFormat.WireType.Varint);
            google.WriteSInt64(value * 3L);
        }

        foreach (var text in new[] { "", "a", "雪-é-🐉", new string('x', 200), "bad \uD800 surrogate" })
        {
            ours.WriteString(536_870_911, text);
            google.WriteTag(536_870_911, WireFormat.WireType.LengthDelimited);
            google.WriteString(text);
        }

        ours.WritePackedUInt32(8, [0, 1, 128, uint.MaxValue]);
        google.WriteTag(8, WireFormat.WireType.LengthDelimited);
        google.WriteLength(CodedOutputStream.ComputeUInt32Size(0) + CodedOutputStream.ComputeUInt32Size(1)
            + CodedOutputStream.ComputeUInt32Size(128) + CodedOutputStream.ComputeUInt32Size(uint.MaxValue));
        foreach (var value in new uint[] { 0, 1, 128, uint.MaxValue })
        {
            google.WriteUInt32(value);
        }

        google.Flush();
        Assert.Equal(stream.ToArray(), ours.WrittenSpan.ToArray());
    }

    [Fact]
    public void Our_reader_matches_google_for_every_valid_scalar_encoding()
    {
        var random = new Random(518);
        for (var i = 0; i < 2_000; i++)
        {
            var value = ((ulong)random.NextInt64() << random.Next(0, 2)) | (uint)random.Next(0, 3);
            value >>= random.Next(0, 64);
            var stream = new MemoryStream();
            var google = new CodedOutputStream(stream);
            google.WriteUInt64(value);
            google.WriteSInt64((long)value);
            google.WriteSInt32((int)value);
            google.WriteFixed64(value);
            google.Flush();

            var reader = new ProtoReader(stream.ToArray());
            Assert.Equal(value, reader.ReadVarint());
            Assert.Equal((long)value, reader.ReadSInt64());
            Assert.Equal((int)value, reader.ReadSInt32());
            Assert.Equal(value, reader.ReadFixed64());
            Assert.True(reader.End);
        }
    }

    [Fact]
    public void Write_then_read_round_trips_the_sample_and_the_header_layout()
    {
        var bytes = SaveFormat.Write(Sample);

        Assert.Equal("TWSV"u8.ToArray(), bytes[..4]);
        Assert.Equal(SaveFormat.Version, bytes[4]);
        var header = new ProtoReader(bytes.AsSpan(5));
        Assert.Equal((ulong)SaveFormat.EncodeBody(Sample).Length, header.ReadVarint());
        AssertSame(Sample, SaveFormat.Read(bytes));
    }

    [Fact]
    public void Frozen_sample_file_still_decodes_to_the_same_save()
    {
        var path = SourcePath("engine", "tests", "Talewright.Runtime.Tests", "SaveSamples", SampleName);
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_REGENERATE_SAVE_SAMPLES") == "1")
        {
            File.WriteAllBytes(path, SaveFormat.Write(Sample));
        }

        var frozen = File.ReadAllBytes(path);

        AssertSame(Sample, SaveFormat.Read(frozen));
        // Compressed bytes may differ between zlib builds; the body may not.
        Assert.Equal(SaveFormat.EncodeBody(Sample), Decompress(frozen));
    }

    [Fact]
    public void Schema_file_field_numbers_match_the_reader_and_writer()
    {
        var schema = File.ReadAllText(SourcePath("engine", "src", "Talewright.Runtime", "Saves", "save-format.proto"));
        var declared = new Dictionary<string, int>();
        string? message = null;
        foreach (var line in schema.Split('\n'))
        {
            if (MessagePattern().Match(line) is { Success: true } start)
            {
                message = start.Groups[1].Value;
            }
            else if (FieldPattern().Match(line) is { Success: true } field)
            {
                declared[Pascal(message! is "Save" ? "" : message) + Pascal(field.Groups[1].Value)] = int.Parse(field.Groups[2].Value);
            }
        }

        var constants = typeof(SavedGame).Assembly.GetType("Talewright.Runtime.Saves.SaveFields")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);
        Assert.Equal(constants.OrderBy(pair => pair.Key), declared.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Randomized_saves_round_trip_exactly()
    {
        var random = new Random(417);
        for (var i = 0; i < 500; i++)
        {
            var save = RandomSave(random);
            var bytes = SaveFormat.Write(save);
            AssertSame(save, SaveFormat.Read(bytes));
            Assert.Equal(SaveFormat.EncodeBody(save), SaveFormat.EncodeBody(SaveFormat.Read(bytes)));
        }
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
    public void Random_bodies_and_mutated_bodies_only_ever_fail_as_invalid_data()
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

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })] // Varint longer than 10 bytes.
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 })] // Varint overflows 64 bits.
    [InlineData(new byte[] { 0x28, 0x80, 0x80, 0x80, 0x80, 0x10 })] // uint32 cursor above 2^32-1.
    [InlineData(new byte[] { 0x00, 0x00 })] // Field number 0.
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10, 0x00 })] // Field number above 2^29-1.
    [InlineData(new byte[] { 0x0B, 0x0C })] // Group start and end (wire types 3 and 4).
    [InlineData(new byte[] { 0x0E })] // Wire type 6.
    [InlineData(new byte[] { 0x0F })] // Wire type 7.
    [InlineData(new byte[] { 0x0A, 0x05, (byte)'a' })] // Length runs past the end.
    [InlineData(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })] // Length near 2^32.
    [InlineData(new byte[] { 0x21, 0x01, 0x02 })] // Truncated fixed64.
    [InlineData(new byte[] { 0x0D, 0x01 })] // Truncated fixed32 in an unknown position.
    [InlineData(new byte[] { 0x0A, 0x02, 0xC3, 0x28 })] // Invalid UTF-8.
    [InlineData(new byte[] { 0x0A, 0x03, 0xED, 0xA0, 0x80 })] // Encoded surrogate.
    [InlineData(new byte[] { 0x12, 0x01, (byte)'v' })] // Missing story id.
    public void Malformed_bodies_are_rejected(byte[] body)
    {
        var error = Assert.Throws<InvalidDataException>(() => SaveFormat.DecodeBody(body, out _));
        Assert.Equal(SaveFormat.MalformedMessage, error.Message);
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(SaveFormat.Pack(body)));
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00 })]
    [InlineData(new byte[] { 0x0C })]
    [InlineData(new byte[] { 0x0A, 0x05, (byte)'a' })]
    [InlineData(new byte[] { 0x21, 0x01, 0x02 })]
    // Not listed: Google's C# reader skips wire types 6 and 7 without consuming anything; we reject them.
    public void Google_also_rejects_the_bodies_we_treat_as_malformed_wire_data(byte[] body)
    {
        Assert.ThrowsAny<InvalidProtocolBufferException>(() =>
        {
            var input = new CodedInputStream(body);
            while (input.ReadTag() != 0)
            {
                input.SkipLastField();
            }
        });
    }

    [Fact]
    public void Missing_fields_read_as_defaults()
    {
        var save = SaveFormat.DecodeBody([0x0A, 0x01, (byte)'s'], out var summaryDamaged);

        Assert.Equal("s", save.StoryId);
        Assert.Equal("", save.StoryVersion);
        Assert.Equal("", save.ContentFingerprint);
        Assert.Equal(0UL, save.Seed);
        Assert.Equal(0, save.Cursor);
        Assert.Empty(save.Choices);
        Assert.Null(save.Character);
        Assert.Null(save.Summary);
        Assert.False(summaryDamaged);
    }

    [Fact]
    public void History_damage_is_reported_separately_from_wire_damage()
    {
        foreach (var save in new[]
        {
            Sample with { Cursor = Sample.Choices.Count + 1 },
            Sample with { Choices = [new string('k', SaveFormat.MaxChoiceKeyLength + 1)], Cursor = 0 },
            Sample with { Choices = [""], Cursor = 0 },
            Sample with { Choices = Enumerable.Repeat("c0", SaveFormat.MaxChoices + 1).ToArray(), Cursor = 0 },
        })
        {
            var error = Assert.Throws<InvalidDataException>(() => SaveFormat.Read(SaveFormat.Write(save)));
            Assert.Equal(SaveFormat.HistoryMessage, error.Message);
        }

        var outOfRange = new ProtoWriter();
        outOfRange.WriteString(1, "story");
        outOfRange.WriteString(7, "c0");
        outOfRange.WritePackedUInt32(8, [0, 1]);
        Assert.Equal(SaveFormat.HistoryMessage,
            Assert.Throws<InvalidDataException>(() => SaveFormat.DecodeBody(outOfRange.WrittenSpan, out _)).Message);
        Assert.Equal(SaveFormat.MaxChoices, SaveFormat.Read(SaveFormat.Write(
            Sample with { Choices = Enumerable.Repeat("c0", SaveFormat.MaxChoices).ToArray() })).Choices.Count);
    }

    [Fact]
    public void Too_many_character_stats_are_rejected()
    {
        var stats = Enumerable.Range(0, SaveFormat.MaxCharacterStats + 1).ToDictionary(i => $"s{i}", i => i);
        var save = Sample with { Character = Sample.Character! with { Stats = stats } };
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(SaveFormat.Write(save)));
        stats.Remove("s0");
        Assert.Equal(SaveFormat.MaxCharacterStats, SaveFormat.Read(SaveFormat.Write(save with
        {
            Character = save.Character with { Stats = stats },
        })).Character!.Stats.Count);
    }

    [Fact]
    public void Container_damage_is_rejected_with_specific_messages()
    {
        var valid = SaveFormat.Write(Sample);
        Assert.Equal(SaveFormat.EmptyMessage, Assert.Throws<InvalidDataException>(() => SaveFormat.Read([])).Message);
        Assert.Equal(SaveFormat.TooLargeMessage, Assert.Throws<InvalidDataException>(() =>
            SaveFormat.Read(new byte[SaveFormat.MaxSaveBytes + 1])).Message);
        Assert.Equal(SaveFormat.UnsupportedMessage, Assert.Throws<InvalidDataException>(() =>
            SaveFormat.Read([.. valid[..4], 2, .. valid[5..]])).Message);
        Assert.Equal(SaveFormat.UnsupportedMessage, Assert.Throws<InvalidDataException>(() =>
            SaveFormat.Read("""{"formatVersion":2}"""u8)).Message);
        Assert.Equal(SaveFormat.MalformedMessage, Assert.Throws<InvalidDataException>(() =>
            SaveFormat.Read("TWSX"u8)).Message);

        var body = SaveFormat.EncodeBody(Sample);
        foreach (var declared in new[] { (ulong)body.Length - 1, (ulong)body.Length + 1, (ulong)SaveFormat.MaxDecodedBytes + 1 })
        {
            Assert.Equal(SaveFormat.MalformedMessage, Assert.Throws<InvalidDataException>(() =>
                SaveFormat.Read(Container(declared, Zlib(body)))).Message);
        }

        // A raw Deflate stream without the zlib header and Adler-32 checksum is not accepted.
        var deflate = new MemoryStream();
        using (var stream = new DeflateStream(deflate, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            stream.Write(body);
        }

        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container((ulong)body.Length, deflate.ToArray())));

        var badChecksum = Zlib(body);
        badChecksum[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container((ulong)body.Length, badChecksum)));

        // .NET accepts a zlib stream whose Adler-32 trailer is missing, so the reader checks it itself.
        var zlib = Zlib(body);
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container((ulong)body.Length, zlib[..^4])));
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container((ulong)body.Length, [.. zlib, 0])));
        Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(zlib.AsSpan(zlib.Length - 4)), SaveFormat.Adler32(body));
    }

    [Fact]
    public void Decompression_bombs_stop_at_the_declared_length()
    {
        var bomb = Zlib(new byte[SaveFormat.MaxDecodedBytes * 4]);
        Assert.True(bomb.Length < SaveFormat.MaxSaveBytes);
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container(16, bomb)));
        Assert.Throws<InvalidDataException>(() => SaveFormat.Read(Container((ulong)SaveFormat.MaxDecodedBytes, bomb)));
    }

    [Fact]
    public void Invalid_summaries_are_dropped_without_failing_the_save()
    {
        foreach (var summary in new byte[][]
        {
            [0x0A, 0x05, (byte)'a'],
            [0x0A, 0x01, (byte)'a'],
            [0x10, 0x01],
            [0x0A, 0x01, (byte)'a', 0x10, 0x01, 0x18, 0x90, 0x0E],
            [0x0A, 0x01, (byte)'a', 0x10, 0x01, 0x18, 0x01],
            [0x0A, 0x01, (byte)'a', 0x10, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01],
        })
        {
            var writer = new ProtoWriter();
            writer.WriteBytes(SaveFields.Summary, summary);
            var save = SaveFormat.DecodeBody([.. SaveFormat.EncodeBody(Sample with { Summary = null }), .. writer.WrittenSpan], out var damaged);
            Assert.True(damaged);
            Assert.Null(save.Summary);
            Assert.Equal(Sample.Choices, save.Choices);
        }
    }

    [Fact]
    public void A_long_realistic_playthrough_stays_far_below_the_save_limit()
    {
        var result = StoryCompiler.Compile(RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource());
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var character = story.Creation?.Default(story.World.Characters[Talewright.Core.Identity.EntityId.Player]);
        var history = new StoryHistory(story, 57, character);
        var random = new Random(57);
        while (history.Cursor < 2_000 && !history.Current.IsEnded && history.Current.Choices.Count > 0)
        {
            history.Choose(history.Current.Choices[random.Next(history.Current.Choices.Count)].Key);
        }

        var save = history.Save(history.CaptureSummary(DateTimeOffset.UtcNow));

        Assert.True(history.Cursor >= 100, $"Only {history.Cursor} choices were made.");
        // Measured at 405 bytes for this 204-choice playthrough when the format was introduced.
        Assert.True(save.Length < 4 * 1024, $"Save is {save.Length} bytes for {history.Cursor} choices.");
    }

    private static SavedGame RandomSave(Random random)
    {
        var keyPool = Enumerable.Range(0, random.Next(1, 40)).Select(_ => RandomText(random, 1, 40)).Distinct().ToArray();
        var choices = Enumerable.Range(0, random.Next(0, 300)).Select(_ => keyPool[random.Next(keyPool.Length)]).ToArray();
        var hasSummary = random.Next(2) == 0;
        return new SavedGame
        {
            StoryId = RandomText(random, 1, 30),
            StoryVersion = RandomText(random, 0, 10),
            ContentFingerprint = RandomText(random, 0, 70),
            Seed = (ulong)random.NextInt64() * (ulong)random.Next(0, 3),
            Choices = choices,
            Cursor = random.Next(0, choices.Length + 1),
            Character = random.Next(3) == 0 ? null : new CharacterSelection
            {
                FirstName = random.Next(3) == 0 ? null : RandomText(random, 0, 20),
                LastName = random.Next(3) == 0 ? null : RandomText(random, 0, 20),
                Pronouns = random.Next(3) == 0 ? null : RandomText(random, 0, 20),
                Preset = random.Next(3) == 0 ? null : RandomText(random, 0, 20),
                Stats = Enumerable.Range(0, random.Next(0, 10)).Select(_ => RandomText(random, 0, 12)).Distinct()
                    .ToDictionary(id => id, _ => random.Next(int.MinValue, int.MaxValue)),
            },
            Summary = hasSummary
                ? new SaveSummary(RandomText(random, 1, 40),
                    new DateTimeOffset(random.NextInt64(1, DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay), TimeSpan.Zero)
                        .ToOffset(TimeSpan.FromMinutes(random.Next(-840, 841))),
                    random.Next(2) == 0 ? null : RandomText(random, 0, 30))
                : null,
        };
    }

    private static string RandomText(Random random, int min, int max)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz-_:/ 0123456789éß雪🐉";
        var length = random.Next(min, max + 1);
        var builder = new System.Text.StringBuilder();
        while (builder.Length < length || (min > 0 && builder.ToString().Trim().Length == 0))
        {
            var start = random.Next(alphabet.Length);
            builder.Append(char.IsHighSurrogate(alphabet[start]) ? alphabet.Substring(start, 2)
                : char.IsLowSurrogate(alphabet[start]) ? "x" : alphabet[start].ToString());
        }

        return builder.ToString();
    }

    private static void AssertSame(SavedGame expected, SavedGame actual)
    {
        Assert.Equal(expected.StoryId, actual.StoryId);
        Assert.Equal(expected.StoryVersion, actual.StoryVersion);
        Assert.Equal(expected.ContentFingerprint, actual.ContentFingerprint);
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(expected.Choices, actual.Choices);
        Assert.Equal(expected.Character is null, actual.Character is null);
        if (expected.Character is { } character)
        {
            Assert.Equal(character.FirstName, actual.Character!.FirstName);
            Assert.Equal(character.LastName, actual.Character.LastName);
            Assert.Equal(character.Pronouns, actual.Character.Pronouns);
            Assert.Equal(character.Preset, actual.Character.Preset);
            Assert.Equal(character.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                actual.Character.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }

        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Summary?.SavedAt.Offset, actual.Summary?.SavedAt.Offset);
    }

    private static void AssertGoogleCharacter(ByteString bytes)
    {
        var input = new CodedInputStream(bytes.ToByteArray());
        var stats = new List<(string Id, int Value)>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: Assert.Equal("Mara", input.ReadString()); break;
                case 2: Assert.Equal("", input.ReadString()); break;
                case 3: Assert.Equal("she/her", input.ReadString()); break;
                case 5:
                    var stat = new CodedInputStream(input.ReadBytes().ToByteArray());
                    Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited), stat.ReadTag());
                    var id = stat.ReadString();
                    Assert.Equal(WireFormat.MakeTag(2, WireFormat.WireType.Varint), stat.ReadTag());
                    stats.Add((id, stat.ReadSInt32()));
                    Assert.True(stat.IsAtEnd);
                    break;
                default: Assert.Fail($"Unexpected character field {tag}"); break;
            }
        }

        Assert.Equal(Sample.Character!.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)), stats);
    }

    private static void AssertGoogleSummary(ByteString bytes)
    {
        var input = new CodedInputStream(bytes.ToByteArray());
        Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited), input.ReadTag());
        Assert.Equal(Sample.Summary!.SceneTitle, input.ReadString());
        Assert.Equal(WireFormat.MakeTag(2, WireFormat.WireType.Varint), input.ReadTag());
        Assert.Equal((ulong)Sample.Summary.SavedAt.UtcTicks, input.ReadUInt64());
        Assert.Equal(WireFormat.MakeTag(3, WireFormat.WireType.Varint), input.ReadTag());
        Assert.Equal(-330, input.ReadSInt32());
        Assert.Equal(WireFormat.MakeTag(4, WireFormat.WireType.LengthDelimited), input.ReadTag());
        Assert.Equal("Mara", input.ReadString());
        Assert.True(input.IsAtEnd);
    }

    private static ByteString GoogleMessage(Action<CodedOutputStream> write)
    {
        var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);
        write(output);
        output.Flush();
        return ByteString.CopyFrom(stream.ToArray());
    }

    private static void WriteGoogleStat(CodedOutputStream output, string id, int value)
    {
        output.WriteTag(5, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(GoogleMessage(stat =>
        {
            stat.WriteTag(1, WireFormat.WireType.LengthDelimited);
            stat.WriteString(id);
            stat.WriteTag(2, WireFormat.WireType.Varint);
            stat.WriteSInt32(value);
        }));
    }

    private static void WriteUnknownFields(CodedOutputStream output, int field)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteUInt64(ulong.MaxValue);
        output.WriteTag(field, WireFormat.WireType.Fixed64);
        output.WriteFixed64(42);
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteString("future data");
        output.WriteTag(field, WireFormat.WireType.Fixed32);
        output.WriteFixed32(7);
        // A known field number with the wrong wire type is skipped too.
        output.WriteTag(1, WireFormat.WireType.Fixed32);
        output.WriteFixed32(9);
    }

    private static byte[] Container(ulong declared, byte[] compressed)
    {
        var length = new ProtoWriter();
        length.WriteVarint(declared);
        return [.. "TWSV"u8, SaveFormat.Version, .. length.WrittenSpan, .. compressed];
    }

    private static byte[] Zlib(byte[] body)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(body);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] save)
    {
        var header = new ProtoReader(save.AsSpan(5));
        header.ReadVarint();
        using var zlib = new ZLibStream(new MemoryStream(save, 5 + header.Position, save.Length - 5 - header.Position), CompressionMode.Decompress);
        using var body = new MemoryStream();
        zlib.CopyTo(body);
        return body.ToArray();
    }

    private static string SourcePath(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }

    private static string Pascal(string? snake) => string.Concat((snake ?? "").Split('_')
        .Where(part => part.Length > 0).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [GeneratedRegex(@"^message\s+(\w+)\s*\{")]
    private static partial Regex MessagePattern();

    [GeneratedRegex(@"^\s+(?:optional\s+|repeated\s+)?\w+\s+(\w+)\s*=\s*(\d+);")]
    private static partial Regex FieldPattern();
}
