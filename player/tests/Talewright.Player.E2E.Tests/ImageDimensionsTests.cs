using System.Buffers.Binary;
using System.Text;
using Talewright.Player.Library;

namespace Talewright.Player.E2E.Tests;

public sealed class ImageDimensionsTests
{
    [Fact]
    public async Task Png_dimensions_are_read_from_the_header_without_consuming_the_image()
    {
        var png = new byte[24 + 1024];
        Convert.FromHexString("89504E470D0A1A0A").CopyTo(png, 0);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 48);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 64);
        using var stream = new MemoryStream(png);

        var dimensions = await ImageDimensions.ReadAsync(stream, "images/glyph.png", CancellationToken.None);

        Assert.Equal((48, 64), dimensions);
        Assert.Equal(24, stream.Position);
    }

    [Fact]
    public async Task Jpeg_exif_orientation_is_applied_to_the_dimensions()
    {
        var jpeg = new List<byte> { 0xff, 0xd8, 0xff, 0xe1, 0, 34 };
        jpeg.AddRange(Convert.FromHexString("45786966000049492A0008000000010012010300010000000600000000000000"));
        jpeg.AddRange([0xff, 0xc0, 0, 11, 8, 0, 40, 0, 60, 3, 1, 0x11, 0, 0xff, 0xda, 0, 2]);
        using var stream = new MemoryStream(jpeg.ToArray());

        var dimensions = await ImageDimensions.ReadAsync(stream, "images/rotated.jpeg", CancellationToken.None);

        Assert.Equal((40, 60), dimensions);
    }

    [Fact]
    public async Task Webp_extended_header_dimensions_are_read()
    {
        var webp = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(webp, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4, 4), 22);
        Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(webp, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), 10);
        webp[22] = 0;
        webp[24] = 47;
        webp[27] = 63;

        var dimensions = await ImageDimensions.ReadAsync(
            new MemoryStream(webp), "images/glyph.webp", CancellationToken.None);

        Assert.Equal((48, 64), dimensions);
    }

    [Fact]
    public async Task Webp_lossy_header_dimensions_are_read()
    {
        var webp = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA=");

        var dimensions = await ImageDimensions.ReadAsync(
            new MemoryStream(webp), "images/glyph.webp", CancellationToken.None);

        Assert.Equal((1, 1), dimensions);
    }

    [Fact]
    public async Task Webp_lossless_header_dimensions_are_read()
    {
        var webp = new byte[26];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(webp, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4, 4), 18);
        Encoding.ASCII.GetBytes("WEBPVP8L").CopyTo(webp, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), 5);
        webp[20] = 0x2f;
        webp[21] = 47;
        webp[22] = 0xc0;
        webp[23] = 15;

        var dimensions = await ImageDimensions.ReadAsync(
            new MemoryStream(webp), "images/glyph.webp", CancellationToken.None);

        Assert.Equal((48, 64), dimensions);
    }

    [Fact]
    public async Task Unsupported_or_malformed_image_headers_remain_unclassified()
    {
        var dimensions = await ImageDimensions.ReadAsync(
            new MemoryStream([0, 1, 2, 3]), "images/broken.png", CancellationToken.None);

        Assert.Null(dimensions);
    }
}
