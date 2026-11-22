using System.IO.Compression;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using MessagePack;

namespace Talewright.Talemark.Compilation;

/// <summary>A compressed binary publication format for desktop story text; images remain external.</summary>
public static class DesktopStoryPack
{
    public const string FileExtension = ".storypack";
    private static readonly byte[] Magic = "TWRPACK1"u8.ToArray();
    private const int PayloadVersion = 3;
    private const int ChunkChars = 16384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Write(Stream output, StorySource source)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);

        var payloadPath = Path.Combine(Path.GetTempPath(), $"talewright-pack-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var payload = File.Create(payloadPath, 4096, FileOptions.SequentialScan))
            {
                WriteArrayHeader(payload, 3);
                MessagePackSerializer.Serialize(payload, PayloadVersion);
                WriteArrayHeader(payload, source.Files.Count);
                foreach (var (path, text) in source.Files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    WriteArrayHeader(payload, 2);
                    WriteString(payload, ValidateTextPath(path));
                    WriteString(payload, text);
                }
                var imagePaths = StoryImageResolver.CompletionPaths(source)
                    .Select(path => "images/" + path)
                    .ToArray();
                WriteArrayHeader(payload, imagePaths.Length);
                foreach (var path in imagePaths)
                {
                    WriteString(payload, ValidateImagePath(path));
                }
            }

            using (var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       4096, FileOptions.SequentialScan))
            {
                output.Write(Magic);
                Span<byte> length = stackalloc byte[8];
                if (!BitConverter.TryWriteBytes(length, (ulong)payload.Length))
                    throw new InvalidDataException("The story pack is too large.");
                output.Write(length);
                using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
                payload.CopyTo(gzip);
            }
        }
        finally
        {
            if (File.Exists(payloadPath))
                File.Delete(payloadPath);
        }
    }

    public static StorySource Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var header = new byte[16];
        ReadExactly(input, header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported or damaged story pack.");

        var payloadLengthValue = BitConverter.ToUInt64(header, Magic.Length);
        if (payloadLengthValue > long.MaxValue)
            throw new InvalidDataException("The story pack payload length is out of range.");
        var payloadLength = (long)payloadLengthValue;
        if (payloadLength < 4)
            throw new InvalidDataException("The story pack payload is truncated.");

        var compressedPath = Path.Combine(Path.GetTempPath(), $"talewright-pack-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var compressed = File.Create(compressedPath, 4096, FileOptions.SequentialScan))
                input.CopyTo(compressed);

            var footer = ReadGZipFooter(compressedPath);
            using var compressedInput = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            using var gzip = new GZipStream(compressedInput, CompressionMode.Decompress, leaveOpen: true);
            using var counting = new CountingReadStream(gzip);
            using var reader = new MessagePackStreamReader(counting, leaveOpen: true);
            if (ReadArrayHeader(reader) != 3)
                throw new InvalidDataException("The story pack has an invalid payload shape.");
            var version = ReadInteger(reader);
            if (version != PayloadVersion)
                throw new InvalidDataException($"Unsupported story pack version '{version}'.");

            var fileCount = ReadArrayHeader(reader);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < fileCount; index++)
            {
                if (ReadArrayHeader(reader) != 2)
                    throw new InvalidDataException("The story pack has an invalid file entry.");
                var path = ReadString(reader);
                ValidateTextPath(path);
                var text = ReadString(reader);
                if (!files.TryAdd(path, text))
                    throw new InvalidDataException($"The story pack contains duplicate file '{path}'.");
            }

            var imageCount = ReadArrayHeader(reader);
            var images = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < imageCount; index++)
            {
                var path = ReadString(reader);
                ValidateImagePath(path);
                if (!images.Add(path))
                    throw new InvalidDataException($"The story pack contains duplicate image path '{path}'.");
            }

            if (!reader.RemainingBytes.IsEmpty || counting.ReadByte() != -1)
                throw new InvalidDataException("The story pack has trailing payload data.");
            if (counting.BytesRead != payloadLength)
                throw new InvalidDataException("The story pack payload length is invalid.");
            if (counting.Crc32 != footer.Crc32 || (uint)(counting.BytesRead & 0xffffffff) != footer.PayloadSize)
                throw new InvalidDataException("The story pack has an invalid GZip footer.");
            if (!files.ContainsKey(StorySource.ManifestFileName))
                throw new InvalidDataException("The story pack has no root manifest.");

            return new StorySource(files, images);
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException
            or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("The story pack contains damaged MessagePack data.", ex);
        }
        finally
        {
            if (File.Exists(compressedPath))
                File.Delete(compressedPath);
        }
    }

    private static void WriteArrayHeader(Stream output, int count)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(count);
        writer.Flush();
        output.Write(buffer.WrittenSpan);
    }

    private static int ChunkLength(string value, int offset)
    {
        var length = Math.Min(ChunkChars, value.Length - offset);
        if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1])
            && char.IsLowSurrogate(value[offset + length]))
            length--;
        return length;
    }

    private static void WriteString(Stream output, string value)
    {
        var chunks = 0;
        for (var offset = 0; offset < value.Length; chunks++)
            offset += ChunkLength(value, offset);
        WriteArrayHeader(output, chunks);
        for (var offset = 0; offset < value.Length;)
        {
            var length = ChunkLength(value, offset);
            MessagePackSerializer.Serialize(output, value.Substring(offset, length));
            offset += length;
        }
    }

    private static int ReadArrayHeader(MessagePackStreamReader reader) =>
        reader.ReadArrayHeaderAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static ReadOnlySequence<byte> ReadItem(MessagePackStreamReader reader) =>
        reader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidDataException("The story pack is truncated.");

    private static int ReadInteger(MessagePackStreamReader reader)
    {
        var item = new MessagePackReader(ReadItem(reader));
        return item.ReadInt32();
    }

    private static string ReadString(MessagePackStreamReader reader)
    {
        var count = ReadArrayHeader(reader);
        var output = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            var item = new MessagePackReader(ReadItem(reader));
            var bytes = item.ReadStringSequence()
                ?? throw new InvalidDataException("The story pack contains invalid text.");
            output.Append(StrictUtf8.GetString(bytes.ToArray()));
        }
        return output.ToString();
    }

    private static string ValidateTextPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.Any(c => c is '\\' || char.IsControl(c))
            || path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException($"Unsafe story pack path '{path}'.");
        return path;
    }

    private static string ValidateImagePath(string path)
    {
        ValidateTextPath(path);
        if (!path.StartsWith("images/", StringComparison.Ordinal)
            || path.Any(c => c is ':' or '?' or '#'))
            throw new InvalidDataException($"Invalid story pack image path '{path}'.");
        return path;
    }

    private readonly record struct GZipFooter(uint Crc32, uint PayloadSize);

    private static GZipFooter ReadGZipFooter(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        if (input.Length < 18)
            throw new InvalidDataException("The story pack GZip footer is truncated.");

        Span<byte> header = stackalloc byte[10];
        ReadExactly(input, header);
        if (header[0] != 0x1f || header[1] != 0x8b || header[2] != 8)
            throw new InvalidDataException("The story pack contains an invalid GZip stream.");

        input.Seek(-8, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[8];
        ReadExactly(input, footer);
        return new GZipFooter(
            BinaryPrimitives.ReadUInt32LittleEndian(footer),
            BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]));
    }

    private static void ReadExactly(Stream input, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new InvalidDataException("The story pack is truncated.");
            offset += read;
        }
    }

    private static void ReadExactly(Stream input, Span<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = input.Read(bytes[offset..]);
            if (read == 0)
                throw new InvalidDataException("The story pack is truncated.");
            offset += read;
        }
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        private static readonly uint[] CrcTable = CreateCrcTable();
        private uint _crc = 0xffffffff;
        public long BytesRead { get; private set; }
        public uint Crc32 => _crc ^ 0xffffffff;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            UpdateCrc(buffer.AsSpan(offset, read));
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            UpdateCrc(buffer[..read]);
            return read;
        }
        private void UpdateCrc(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
                _crc = CrcTable[(byte)_crc ^ value] ^ (_crc >> 8);
        }
        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (var index = 0; index < table.Length; index++)
            {
                var crc = (uint)index;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
                table[index] = crc;
            }
            return table;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
