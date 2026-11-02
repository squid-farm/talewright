using System.Text;
using Talewright.Player.Saves;
using Talewright.Runtime;

namespace Talewright.Player.E2E.Tests;

public sealed class SaveFileExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"talewright-export-{Guid.NewGuid():N}");

    [Fact]
    public void Suggested_names_are_portable_bounded_and_keep_both_labels()
    {
        Assert.Equal("A_story - Before_the_door.json", SaveFileName.Create("A/story", "Before:the*door"));
        Assert.Equal("Café - 回廊.json", SaveFileName.Create("Café", "回廊"));
        Assert.Equal("Story - Save.json", SaveFileName.Create("...", " "));
        Assert.Equal("A - B.json", SaveFileName.Create("A . ", "B. "));

        var longName = SaveFileName.Create(new string('界', 100), string.Concat(Enumerable.Repeat("🚀", 100)));
        Assert.True(Encoding.UTF8.GetByteCount(longName) <= 188);
        Assert.DoesNotContain('\uFFFD', longName);
        Assert.EndsWith(".json", longName);
    }

    [Fact]
    public async Task Local_export_uses_selected_path_and_cancel_keeps_existing_file()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "chosen.json");
        await File.WriteAllBytesAsync(destination, [1]);
        string? selected = null;
        string? suggested = null;
        var exporter = new LocalSaveFileExporter(name =>
        {
            suggested = name;
            return Task.FromResult(selected);
        });

        Assert.False(await exporter.ExportAsync("Story - Slot.json", [2, 3]));
        Assert.Equal("Story - Slot.json", suggested);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(destination));
        selected = destination;
        Assert.True(await exporter.ExportAsync("Story - Slot.json", [2, 3]));
        Assert.Equal(new byte[] { 2, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Local_export_rejects_bad_data_and_surfaces_write_failures()
    {
        Directory.CreateDirectory(_directory);
        var exporter = new LocalSaveFileExporter(_ => Task.FromResult<string?>(_directory));
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync("save.json", []));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            exporter.ExportAsync("save.json", new byte[StoryHistory.MaxSaveBytes + 1]));
        await Assert.ThrowsAnyAsync<IOException>(() => exporter.ExportAsync("save.json", [1]));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
