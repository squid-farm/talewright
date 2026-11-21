using System.Buffers.Binary;
using System.Text;

namespace Talewright.Player.Library;

internal static class ImageDimensions
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] WebpSignature = "WEBP"u8.ToArray();

    internal static async Task<(int Width, int Height)?> ReadAsync(
        Stream stream, string path, CancellationToken cancellationToken)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => await ReadPngAsync(stream, cancellationToken),
            ".jpg" or ".jpeg" => await ReadJpegAsync(stream, cancellationToken),
            ".webp" => await ReadWebpAsync(stream, cancellationToken),
            _ => null,
        };
    }

    private static async Task<(int Width, int Height)?> ReadPngAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[24];
        if (!await ReadExactlyAsync(stream, header, cancellationToken) ||
            !header.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return null;

        return CreateDimensions(
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)));
    }

    private static async Task<(int Width, int Height)?> ReadJpegAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (await ReadByteAsync(stream, cancellationToken) != 0xff ||
            await ReadByteAsync(stream, cancellationToken) != 0xd8)
            return null;

        (int Width, int Height)? dimensions = null;
        var swapDimensions = false;
        while (true)
        {
            var prefix = await ReadByteAsync(stream, cancellationToken);
            if (prefix < 0) return null;
            if (prefix != 0xff) return null;

            int marker;
            do marker = await ReadByteAsync(stream, cancellationToken);
            while (marker == 0xff);
            if (marker < 0 || marker == 0x00) return null;
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;

            var length = await ReadUInt16BigEndianAsync(stream, cancellationToken);
            if (length < 2) return null;
            var payloadLength = length - 2;

            if (marker == 0xe1 && payloadLength <= 65533)
            {
                var payload = new byte[payloadLength];
                if (!await ReadExactlyAsync(stream, payload, cancellationToken)) return null;
                swapDimensions |= ExifRotatesDimensions(payload);
                continue;
            }

            if (IsStartOfFrame(marker))
            {
                if (payloadLength < 5) return null;
                var frame = new byte[5];
                if (!await ReadExactlyAsync(stream, frame, cancellationToken)) return null;
                dimensions = CreateDimensions(
                    BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1, 2)));
                if (payloadLength > frame.Length &&
                    !await SkipExactlyAsync(stream, payloadLength - frame.Length, cancellationToken))
                    return null;
                continue;
            }

            if (!await SkipExactlyAsync(stream, payloadLength, cancellationToken)) return null;
        }

        if (dimensions is not { } found) return null;
        return swapDimensions ? (found.Height, found.Width) : found;
    }

    private static bool IsStartOfFrame(int marker) =>
        marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
            0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static bool ExifRotatesDimensions(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14 || !payload[..6].SequenceEqual("Exif\0\0"u8)) return false;
        var tiff = payload[6..];
        var littleEndian = tiff[..2].SequenceEqual("II"u8);
        if (!littleEndian && !tiff[..2].SequenceEqual("MM"u8)) return false;

        ushort Read16(ReadOnlySpan<byte> value) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);
        uint Read32(ReadOnlySpan<byte> value) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);

        if (Read16(tiff[2..]) != 42) return false;
        var offset = Read32(tiff[4..]);
        if (offset > int.MaxValue || offset + 2 > tiff.Length) return false;
        var directory = tiff[(int)offset..];
        var count = Read16(directory);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 2 + index * 12;
            if (entryOffset + 12 > directory.Length) return false;
            var entry = directory.Slice(entryOffset, 12);
            if (Read16(entry) == 0x0112 && Read16(entry[2..]) == 3 && Read32(entry[4..]) >= 1)
            {
                var orientation = Read16(entry[8..]);
                return orientation is 5 or 6 or 7 or 8;
            }
        }
        return false;
    }

    private static async Task<(int Width, int Height)?> ReadWebpAsync(Stream stream, CancellationToken cancellationToken)
    {
        var riff = new byte[12];
        if (!await ReadExactlyAsync(stream, riff, cancellationToken) ||
            !riff.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !riff.AsSpan(8, 4).SequenceEqual(WebpSignature))
            return null;

        var remaining = (long)BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4, 4)) - 4;
        while (remaining >= 8)
        {
            var chunk = new byte[8];
            if (!await ReadExactlyAsync(stream, chunk, cancellationToken)) return null;
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
            remaining -= 8;
            if (length > remaining) return null;

            var chunkType = Encoding.ASCII.GetString(chunk, 0, 4);
            var headerLength = chunkType switch
            {
                "VP8X" => 10,
                "VP8 " => 10,
                "VP8L" => 5,
                _ => 0,
            };
            if (headerLength > 0)
            {
                if (length < headerLength) return null;
                var data = new byte[headerLength];
                if (!await ReadExactlyAsync(stream, data, cancellationToken)) return null;

                var dimensions = chunkType switch
                {
                    "VP8X" => CreateDimensions(
                        ReadUInt24LittleEndian(data.AsSpan(4, 3)) + 1,
                        ReadUInt24LittleEndian(data.AsSpan(7, 3)) + 1),
                    "VP8 " when data[3] == 0x9d && data[4] == 0x01 && data[5] == 0x2a =>
                        CreateDimensions(
                            (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2)) & 0x3fff),
                            (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)) & 0x3fff)),
                    "VP8L" when data[0] == 0x2f => CreateDimensions(
                        (uint)(data[1] | (data[2] & 0x3f) << 8) + 1,
                        (uint)((data[2] & 0xc0) >> 6 | data[3] << 2 | (data[4] & 0x0f) << 10) + 1),
                    _ => null,
                };
                if (dimensions is not null) return dimensions;
                if (!await SkipExactlyAsync(stream, length - headerLength, cancellationToken)) return null;
            }
            else if (!await SkipExactlyAsync(stream, length, cancellationToken))
                return null;

            remaining -= length;
            if ((length & 1) != 0)
            {
                if (remaining == 0 || await ReadByteAsync(stream, cancellationToken) < 0) return null;
                --remaining;
            }
        }
        return null;
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        (uint)(value[0] | value[1] << 8 | value[2] << 16);

    private static (int Width, int Height)? CreateDimensions(uint width, uint height) =>
        width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? ((int)width, (int)height)
            : null;

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, cancellationToken) == 0 ? -1 : buffer[0];
    }

    private static async Task<ushort> ReadUInt16BigEndianAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        return await ReadExactlyAsync(stream, buffer, cancellationToken)
            ? BinaryPrimitives.ReadUInt16BigEndian(buffer)
            : (ushort)0;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return false;
            buffer = buffer[count..];
        }
        return true;
    }

    private static async Task<bool> SkipExactlyAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0) return false;
            count -= read;
        }
        return true;
    }
}
