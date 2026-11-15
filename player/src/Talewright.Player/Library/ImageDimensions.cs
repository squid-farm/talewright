using System.Buffers.Binary;

namespace Talewright.Player.Library;

internal readonly record struct ImageDimensions(int Width, int Height)
{
    public static async Task<ImageDimensions> ReadAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => await ReadPngAsync(stream, path, cancellationToken),
            ".jpg" or ".jpeg" => await ReadJpegAsync(stream, path, cancellationToken),
            ".webp" => await ReadWebpAsync(stream, path, cancellationToken),
            _ => throw new InvalidDataException($"Image '{path}' has an unsupported format."),
        };
    }

    private static async Task<ImageDimensions> ReadPngAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var header = new byte[24];
        await ReadExactlyAsync(stream, header, path, cancellationToken);
        if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) != 13 ||
            !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw InvalidHeader(path);
        }

        return CreateDimensions(
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)), path);
    }

    private static async Task<ImageDimensions> ReadJpegAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var start = new byte[2];
        await ReadExactlyAsync(stream, start, path, cancellationToken);
        if (start[0] != 0xff || start[1] != 0xd8) throw InvalidHeader(path);

        uint? width = null;
        while (true)
        {
            var markerStart = await ReadByteAsync(stream, path, cancellationToken);
            if (markerStart != 0xff) throw InvalidHeader(path);
            byte marker;
            do marker = await ReadByteAsync(stream, path, cancellationToken);
            while (marker == 0xff);

            if (marker == 0x00) throw InvalidHeader(path);
            if (marker == 0xd9)
            {
                throw InvalidHeader(path);
            }
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;

            var length = await ReadUInt16BigEndianAsync(stream, path, cancellationToken);
            if (length < 2) throw InvalidHeader(path);
            var payloadLength = length - 2;
            if (IsStartOfFrame(marker))
            {
                if (payloadLength < 5) throw InvalidHeader(path);
                var frame = new byte[5];
                await ReadExactlyAsync(stream, frame, path, cancellationToken);
                var height = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3, 2));
                if (width is 0) throw InvalidHeader(path);
                if (height is not 0) return CreateDimensions(width.Value, height, path);
                await SkipAsync(stream, (uint)(payloadLength - frame.Length), path, cancellationToken);
                continue;
            }

            if (marker == 0xdc && width is { } dnlWidth)
            {
                if (payloadLength != 2) throw InvalidHeader(path);
                var height = await ReadUInt16BigEndianAsync(stream, path, cancellationToken);
                return CreateDimensions(dnlWidth, height, path);
            }
            if (marker == 0xda && width is { } scanWidth)
                return await ReadJpegScanHeightAsync(stream, path, scanWidth, cancellationToken, payloadLength);
            await SkipAsync(stream, payloadLength, path, cancellationToken);
        }
    }

    private static async Task<ImageDimensions> ReadJpegScanHeightAsync(Stream stream, string path, uint width,
        CancellationToken cancellationToken, int scanHeaderLength = 0)
    {
        if (scanHeaderLength > 0) await SkipAsync(stream, scanHeaderLength, path, cancellationToken);
        while (true)
        {
            var value = await ReadByteAsync(stream, path, cancellationToken);
            if (value != 0xff) continue;
            byte marker;
            do marker = await ReadByteAsync(stream, path, cancellationToken);
            while (marker == 0xff);
            if (marker is 0x00 or >= 0xd0 and <= 0xd7 or 0x01) continue;
            if (marker == 0xdc)
            {
                var length = await ReadUInt16BigEndianAsync(stream, path, cancellationToken);
                if (length != 4) throw InvalidHeader(path);
                return CreateDimensions(width,
                    await ReadUInt16BigEndianAsync(stream, path, cancellationToken), path);
            }
            if (marker is 0xd9 or 0xda) throw InvalidHeader(path);
            var segmentLength = await ReadUInt16BigEndianAsync(stream, path, cancellationToken);
            if (segmentLength < 2) throw InvalidHeader(path);
            await SkipAsync(stream, (uint)(segmentLength - 2), path, cancellationToken);
        }
    }

    private static async Task<ImageDimensions> ReadWebpAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await ReadExactlyAsync(stream, header, path, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw InvalidHeader(path);

        var remaining = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (remaining < 4) throw InvalidHeader(path);
        remaining -= 4;
        while (remaining >= 8)
        {
            var chunk = new byte[8];
            await ReadExactlyAsync(stream, chunk, path, cancellationToken);
            remaining -= 8;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
            var paddedSize = (ulong)size + (size % 2);
            if (paddedSize > remaining) throw InvalidHeader(path);
            var chunkSize = size;

            ImageDimensions? dimensions = null;
            if (chunk.AsSpan(0, 4).SequenceEqual("VP8X"u8))
            {
                if (size < 10) throw InvalidHeader(path);
                var data = new byte[10];
                await ReadExactlyAsync(stream, data, path, cancellationToken);
                dimensions = CreateDimensions(
                    1u + ReadUInt24LittleEndian(data.AsSpan(4, 3)),
                    1u + ReadUInt24LittleEndian(data.AsSpan(7, 3)), path);
                size -= 10;
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("VP8 "u8))
            {
                if (size < 10) throw InvalidHeader(path);
                var data = new byte[10];
                await ReadExactlyAsync(stream, data, path, cancellationToken);
                if (!data.AsSpan(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a })) throw InvalidHeader(path);
                dimensions = CreateDimensions(
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2)) & 0x3fff),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)) & 0x3fff), path);
                size -= 10;
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("VP8L"u8))
            {
                if (size < 5) throw InvalidHeader(path);
                var data = new byte[5];
                await ReadExactlyAsync(stream, data, path, cancellationToken);
                if (data[0] != 0x2f) throw InvalidHeader(path);
                var width = 1u + data[1] + ((uint)(data[2] & 0x3f) << 8);
                var height = 1u + ((uint)(data[2] & 0xc0) >> 6) + ((uint)data[3] << 2) +
                    ((uint)(data[4] & 0x0f) << 10);
                dimensions = CreateDimensions(width, height, path);
                size -= 5;
            }

            if (dimensions is { } found) return found;
            await SkipAsync(stream, size, path, cancellationToken);
            if (chunkSize % 2 != 0) await SkipAsync(stream, 1, path, cancellationToken);
            remaining -= (uint)paddedSize;
        }
        throw InvalidHeader(path);
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static ImageDimensions CreateDimensions(uint width, uint height, string path)
    {
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw InvalidHeader(path);
        return new((int)width, (int)height);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await ReadExactlyAsync(stream, value, path, cancellationToken);
        return value[0];
    }

    private static async Task<ushort> ReadUInt16BigEndianAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var value = new byte[2];
        await ReadExactlyAsync(stream, value, path, cancellationToken);
        return BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, string path, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Image '{path}' has an incomplete header.", ex);
        }
    }

    private static async Task SkipAsync(Stream stream, uint count, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (count > 0)
        {
            var read = (int)Math.Min(count, (uint)buffer.Length);
            await ReadExactlyAsync(stream, buffer.AsMemory(0, read), path, cancellationToken);
            count -= (uint)read;
        }
    }

    private static Task SkipAsync(Stream stream, int count, string path, CancellationToken cancellationToken) =>
        SkipAsync(stream, checked((uint)count), path, cancellationToken);

    private static InvalidDataException InvalidHeader(string path) =>
        new($"Image '{path}' has an invalid or incomplete header.");
}
