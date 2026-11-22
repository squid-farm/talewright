using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;

namespace Talewright.Talemark.Compilation;

/// <summary>A compressed binary publication format for desktop story text; images remain external.</summary>
public static class DesktopStoryPack
{
    public const string FileExtension = ".storypack";
    private static readonly byte[] Magic = "TWRPACK1"u8.ToArray();
    private const int PayloadVersion = 2;

    public static void Write(Stream output, StorySource source)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);

        var payloadPath = Path.Combine(Path.GetTempPath(), $"talewright-pack-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var payload = File.Create(payloadPath, 4096, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(PayloadVersion);
                writer.Write(source.Files.Count);
                foreach (var (path, text) in source.Files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    WriteString(writer, ValidateTextPath(path));
                    WriteString(writer, text);
                }
                var imagePaths = StoryImageResolver.CompletionPaths(source)
                    .Select(path => "images/" + path)
                    .ToArray();
                writer.Write(imagePaths.Length);
                foreach (var path in imagePaths)
                {
                    WriteString(writer, ValidateImagePath(path));
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
            using var reader = new BinaryReader(counting, Encoding.UTF8, leaveOpen: true);
            var version = reader.ReadInt32();
            if (version != PayloadVersion)
                throw new InvalidDataException($"Unsupported story pack version '{version}'.");

            var fileCount = reader.ReadInt32();
            if (fileCount < 0)
                throw new InvalidDataException("The story pack has an invalid file count.");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < fileCount; index++)
            {
                var path = ReadString(reader);
                ValidateTextPath(path);
                var text = ReadString(reader);
                if (!files.TryAdd(path, text))
                    throw new InvalidDataException($"The story pack contains duplicate file '{path}'.");
            }

            var imageCount = reader.ReadInt32();
            if (imageCount < 0)
                throw new InvalidDataException("The story pack has an invalid image count.");
            var images = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < imageCount; index++)
            {
                var path = ReadString(reader);
                ValidateImagePath(path);
                if (!images.Add(path))
                    throw new InvalidDataException($"The story pack contains duplicate image path '{path}'.");
            }

            if (counting.BytesRead != payloadLength)
                throw new InvalidDataException("The story pack payload length is invalid.");
            if (gzip.ReadByte() != -1)
                throw new InvalidDataException("The story pack has trailing payload data.");
            if (counting.Crc32 != footer.Crc32 || (uint)(counting.BytesRead & 0xffffffff) != footer.PayloadSize)
                throw new InvalidDataException("The story pack has an invalid GZip footer.");
            if (!files.ContainsKey(StorySource.ManifestFileName))
                throw new InvalidDataException("The story pack has no root manifest.");

            return new StorySource(files, images);
        }
        finally
        {
            if (File.Exists(compressedPath))
                File.Delete(compressedPath);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var prefixPosition = writer.BaseStream.Position;
        writer.Write((long)0);

        var encoded = new byte[81920];
        var encoder = Encoding.UTF8.GetEncoder();
        var charOffset = 0;
        var byteLength = (long)0;
        while (charOffset < value.Length)
        {
            var charCount = Math.Min(encoded.Length, value.Length - charOffset);
            var flush = charOffset + charCount == value.Length;
            encoder.Convert(value.AsSpan(charOffset, charCount), encoded, flush,
                out var charsUsed, out var bytesUsed, out _);
            if (charsUsed == 0)
                throw new InvalidOperationException("The UTF-8 encoder made no progress.");
            writer.Write(encoded, 0, bytesUsed);
            byteLength = checked(byteLength + bytesUsed);
            charOffset += charsUsed;
        }

        var endPosition = writer.BaseStream.Position;
        writer.BaseStream.Position = prefixPosition;
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(length, byteLength);
        writer.BaseStream.Write(length);
        writer.BaseStream.Position = endPosition;
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt64();
        if (length < 0)
            throw new InvalidDataException("The story pack contains an invalid string length.");
        var output = new System.Text.StringBuilder();
        var bytes = new byte[81920];
        var chars = new char[81920];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var remaining = length;
        try
        {
            while (remaining > 0)
            {
                var count = (int)Math.Min(bytes.Length, remaining);
                var read = reader.Read(bytes, 0, count);
                if (read == 0)
                    throw new InvalidDataException("The story pack is truncated.");
                decoder.Convert(bytes.AsSpan(0, read), chars, flush: false,
                    out _, out var charsUsed, out _);
                output.Append(chars, 0, charsUsed);
                remaining -= read;
            }
            decoder.Convert(ReadOnlySpan<byte>.Empty, chars, flush: true,
                out _, out var flushedCharsUsed, out _);
            output.Append(chars, 0, flushedCharsUsed);
            return output.ToString();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The story pack contains invalid text.", ex);
        }
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
