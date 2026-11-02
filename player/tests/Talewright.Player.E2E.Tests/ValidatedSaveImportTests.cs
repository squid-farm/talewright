using System.Text;
using System.Text.Json.Nodes;
using Talewright.Player.Saves;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class ValidatedSaveImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"talewright-import-{Guid.NewGuid():N}");

    [Fact]
    public async Task Validated_import_preserves_exact_bytes_and_requires_explicit_overwrite()
    {
        var story = TestStories.Linear.Compile();
        var original = new StoryHistory(story, 1).Save();
        var history = new StoryHistory(story, 2);
        history.Choose("c0");
        var bytes = history.Save(history.CaptureSummary(DateTimeOffset.UtcNow));
        var expected = bytes.ToArray();
        var import = ValidatedSaveImport.Prepare(story, "Slot", bytes);
        var store = new FileSaveStore(_directory);
        await store.WriteAsync(story.Id, "Slot", original);

        bytes[0] = 0;
        await Assert.ThrowsAnyAsync<IOException>(() => import.StoreAsync(store, overwrite: false));
        Assert.Equal(original, await store.ReadAsync(story.Id, "Slot"));
        await import.StoreAsync(store, overwrite: true);
        Assert.Equal(expected, await store.ReadAsync(story.Id, "Slot"));
        Assert.Equal(history.ProgressKey, import.Describe().ProgressKey);
    }

    [Fact]
    public async Task Corrupt_and_incompatible_imports_never_replace_existing_data()
    {
        var story = TestStories.Linear.Compile();
        var history = new StoryHistory(story, 3);
        var original = history.Save();
        var store = new FileSaveStore(_directory);
        await store.WriteAsync(story.Id, "Slot", original);

        var format = JsonNode.Parse(original)!;
        format["formatVersion"] = 999;
        var otherStory = JsonNode.Parse(original)!;
        otherStory["storyId"] = "other";
        var otherVersion = JsonNode.Parse(original)!;
        otherVersion["storyVersion"] = "other";
        var fingerprint = JsonNode.Parse(original)!;
        fingerprint["contentFingerprint"] = "other";
        var replay = JsonNode.Parse(original)!;
        replay["choices"] = new JsonArray("not-a-choice");
        replay["cursor"] = 1;
        var invalid = new[]
        {
            Array.Empty<byte>(),
            "{broken"u8.ToArray(),
            Encoding.UTF8.GetBytes(format.ToJsonString()),
            Encoding.UTF8.GetBytes(otherStory.ToJsonString()),
            Encoding.UTF8.GetBytes(otherVersion.ToJsonString()),
            Encoding.UTF8.GetBytes(fingerprint.ToJsonString()),
            Encoding.UTF8.GetBytes(replay.ToJsonString()),
            new byte[StoryHistory.MaxSaveBytes + 1],
        };
        foreach (var bytes in invalid)
        {
            Assert.Throws<InvalidDataException>(() => ValidatedSaveImport.Prepare(story, "Slot", bytes));
            Assert.Equal(original, await store.ReadAsync(story.Id, "Slot"));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
