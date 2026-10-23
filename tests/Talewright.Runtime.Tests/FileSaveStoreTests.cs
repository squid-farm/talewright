using Talewright.Runtime.Saves;

namespace Talewright.Runtime.Tests;

public sealed class FileSaveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-saves-{Guid.NewGuid():N}");
    private FileSaveStore Store => new(_root);

    [Fact]
    public async Task Catalog_keeps_corrupt_and_oversized_slots_visible_with_explicit_warnings()
    {
        await Store.WriteAsync("story", "Malformed", "{broken"u8.ToArray());
        await Store.WriteAsync("story", "Oversized", [1]);
        var path = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .Single(file => File.ReadAllBytes(file).SequenceEqual(new byte[] { 1 }));
        await using (var stream = File.OpenWrite(path))
        {
            stream.SetLength(StoryHistory.MaxSaveBytes + 1);
        }
        var entries = await Store.ListEntriesAsync("story");
        Assert.Equal(["Malformed", "Oversized"], entries.Select(entry => entry.Slot));
        Assert.All(entries, entry =>
        {
            Assert.Null(entry.Summary);
            Assert.Null(entry.ProgressKey);
            Assert.NotNull(entry.Warning);
        });
    }

    [Fact]
    public async Task Round_trip_isolated_slots_and_stories()
    {
        Assert.Empty(await Store.ListAsync("story"));
        await Store.WriteAsync("story", "Before the door", [1, 2, 3]);
        await Store.WriteAsync("other", "Before the door", [4]);
        Assert.Equal(["Before the door"], await Store.ListAsync("story"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await Store.ReadAsync("story", "Before the door"));
        Assert.Equal(new byte[] { 4 }, await Store.ReadAsync("other", "Before the door"));
    }

    [Fact]
    public async Task Overwrite_is_explicit_and_failed_duplicate_preserves_data()
    {
        await Store.WriteAsync("story", "slot", [1]);
        await Assert.ThrowsAsync<IOException>(() => Store.WriteAsync("story", "slot", [2]));
        Assert.Equal(new byte[] { 1 }, await Store.ReadAsync("story", "slot"));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
        await Store.WriteAsync("story", "slot", [3], overwrite: true);
        Assert.Equal(new byte[] { 3 }, await Store.ReadAsync("story", "slot"));
    }

    [Fact]
    public async Task Slots_with_case_colliding_base64_names_remain_independent()
    {
        await Store.WriteAsync("story", "aaa", [1]);
        await Store.WriteAsync("story", "aaG", [2]);
        await Store.WriteAsync("story", "café", [3]);
        var files = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories);
        Assert.Equal(files.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["aaG", "aaa", "café"], await Store.ListAsync("story"));
        await Store.WriteAsync("story", "aaG", [4], overwrite: true);
        Assert.Equal(new byte[] { 1 }, await Store.ReadAsync("story", "aaa"));
        Assert.Equal(new byte[] { 4 }, await Store.ReadAsync("story", "aaG"));
        Assert.Equal(new byte[] { 3 }, await Store.ReadAsync("story", "café"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" slot")]
    [InlineData("slot\n")]
    public async Task Invalid_names_are_explicit_errors(string slot)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.WriteAsync("story", slot, [1]));
    }

    [Fact]
    public async Task Names_cannot_escape_the_owned_save_directory()
    {
        await Store.WriteAsync("../story", "../../slot", [1]);
        Assert.Equal(["../../slot"], await Store.ListAsync("../story"));
        Assert.Equal(new byte[] { 1 }, await Store.ReadAsync("../story", "../../slot"));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Bounds_and_failed_io_are_explicit()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.WriteAsync("story", new string('a', 65), [1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.WriteAsync("story", "slot", []));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.WriteAsync("story", "slot", new byte[StoryHistory.MaxSaveBytes + 1]));
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "file-not-directory");
        File.WriteAllText(blocked, "blocked");
        await Assert.ThrowsAnyAsync<IOException>(() => new FileSaveStore(blocked).WriteAsync("story", "slot", [1]));
        await Assert.ThrowsAnyAsync<IOException>(() => Store.ReadAsync("story", "missing"));
    }

    [Fact]
    public async Task Reads_enforce_size_before_allocating()
    {
        await Store.WriteAsync("story", "slot", [1]);
        var path = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single();
        await using (var stream = File.OpenWrite(path))
        {
            stream.SetLength(StoryHistory.MaxSaveBytes + 1);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.ReadAsync("story", "slot"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
