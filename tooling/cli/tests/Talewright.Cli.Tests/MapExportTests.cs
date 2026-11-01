using System.Text;
using Talewright.Debugger;

namespace Talewright.Cli.Tests;

public sealed class MapExportTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4096, 4096)]
    [InlineData(8192, 2048)]
    public void Png_export_accepts_dimensions_within_both_limits(int width, int height)
    {
        var result = MapExportLimits.CheckPngSize(width, height);

        Assert.True(result.Allowed);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(8192, 2049)]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void Png_export_rejects_dimensions_outside_a_limit_with_an_svg_alternative(int width, int height)
    {
        var result = MapExportLimits.CheckPngSize(width, height);

        Assert.False(result.Allowed);
        Assert.Contains("SVG", result.Message);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
    }

    [Fact]
    public async Task Map_export_stream_is_written_to_the_selected_path_and_replaces_it_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "story-map.svg");
            await File.WriteAllTextAsync(path, "old");
            await using var contents = new MemoryStream(Encoding.UTF8.GetBytes("<svg>map</svg>"));

            await MapExportFiles.SaveAsync(path, "svg", contents);

            Assert.Equal("<svg>map</svg>", await File.ReadAllTextAsync(path));
            Assert.Equal(["story-map.svg"], Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_map_export_preserves_the_existing_file_and_removes_partial_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "story-map.svg");
            await File.WriteAllTextAsync(path, "old");
            await using var contents = new FailingExportStream();

            await Assert.ThrowsAsync<IOException>(() => MapExportFiles.SaveAsync(path, "svg", contents));

            Assert.Equal("old", await File.ReadAllTextAsync(path));
            Assert.Equal(["story-map.svg"], Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingExportStream : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes("partial"), cancellationToken);
            throw new IOException("Export stream was interrupted.");
        }
    }

    [Fact]
    public async Task Map_export_rejects_a_selected_path_with_the_wrong_extension()
    {
        var root = Path.Combine(Path.GetTempPath(), "talewright-map-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var contents = new MemoryStream(Encoding.UTF8.GetBytes("map"));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                MapExportFiles.SaveAsync(Path.Combine(root, "story-map.png"), "svg", contents));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
