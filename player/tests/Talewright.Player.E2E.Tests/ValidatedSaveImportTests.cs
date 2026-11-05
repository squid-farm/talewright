using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Saves;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class ValidatedSaveImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"talewright-import-{Guid.NewGuid():N}");

    [Fact]
    public void Browser_choice_limit_fixture_has_valid_evidence_and_restores_at_the_exact_boundary()
    {
        var (_, story, bytes) = SaveHistoryTests.CreateChoiceLimitSave();
        var saved = SaveFormat.Read(bytes);
        Assert.NotNull(saved.ReplayEvidence);
        Assert.Equal(StoryHistory.MaxChoices, saved.Choices.Count);
        Assert.Equal(StoryHistory.MaxChoices, saved.Cursor);

        var history = StoryHistory.Restore(story, bytes);
        Assert.Equal(StoryHistory.MaxChoices, history.Cursor);
        Assert.Equal(StoryHistory.MaxChoices + 1, history.Transcript.Count);
        Assert.Throws<InvalidOperationException>(() => history.Choose("c0"));
        Assert.Equal(bytes, history.Save());

        history.Back();
        Assert.Equal(StoryHistory.MaxChoices - 1, history.Cursor);
        history.Forward();
        Assert.Equal(StoryHistory.MaxChoices, history.Cursor);
        Assert.Equal(bytes, history.Save());
    }

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

        var save = SaveFormat.Read(original);
        var changedOutcome = new StoryHistory(story, 4);
        changedOutcome.Choose("c0");
        var changedOutcomeSave = SaveFormat.Read(changedOutcome.Save());
        var replayIdentities = changedOutcomeSave.ReplayChoiceIdentities!.Select(identity => identity.ToArray()).ToArray();
        replayIdentities[0][0] ^= 1;
        var invalid = new[]
        {
            Array.Empty<byte>(),
            "broken"u8.ToArray(),
            """{"formatVersion":2}"""u8.ToArray(),
            original[..^1],
            [.. original[..4], 99, .. original[5..]],
            SaveFormat.Write(save with { StoryId = "other" }),
            SaveFormat.Write(save with { StoryVersion = "other" }),
            SaveFormat.Write(save with { ContentFingerprint = "other" }),
            SaveFormat.Write(changedOutcomeSave with { ReplayChoiceIdentities = replayIdentities }),
            new byte[StoryHistory.MaxSaveBytes + 1],
        };
        foreach (var bytes in invalid)
        {
            Assert.Throws<InvalidDataException>(() => ValidatedSaveImport.Prepare(story, "Slot", bytes));
            Assert.Equal(original, await store.ReadAsync(story.Id, "Slot"));
        }
    }

    [Fact]
    public async Task Desktop_import_of_a_compatible_revision_preserves_bytes_and_failed_overwrites()
    {
        var (original, updated, divergent) = Revisions();
        var source = new StoryHistory(original, 7);
        source.Choose("c0");
        source.Back();
        var bytes = source.Save();
        var active = new StoryHistory(updated, 8);
        var activeBefore = active.Save();
        var store = new FileSaveStore(_directory);
        await store.WriteAsync(updated.Id, "Slot", activeBefore);

        Assert.Throws<InvalidDataException>(() => ValidatedSaveImport.Prepare(divergent, "Slot", bytes));
        var import = ValidatedSaveImport.Prepare(updated, "Slot", bytes);
        await Assert.ThrowsAnyAsync<IOException>(() => import.StoreAsync(store, overwrite: false));
        Assert.Equal(activeBefore, await store.ReadAsync(updated.Id, "Slot"));
        Assert.Equal(activeBefore, active.Save());

        await import.StoreAsync(store, overwrite: true);
        var stored = await store.ReadAsync(updated.Id, "Slot");
        Assert.Equal(bytes, stored);
        var restored = StoryHistory.Restore(updated, stored);
        Assert.True(restored.CanForward);
        restored.Forward();
        source.Forward();
        Assert.Equal(source.Events, restored.Events);
        Assert.Equal(activeBefore, active.Save());
    }

    [Fact]
    public async Task Browser_store_import_preserves_existing_bytes_when_a_write_fails()
    {
        var (original, updated, divergent) = Revisions();
        var source = new StoryHistory(original, 7);
        source.Choose("c0");
        var bytes = source.Save();
        var active = new StoryHistory(updated, 8);
        var activeBefore = active.Save();
        var browser = new MemoryIndexedDb();
        await using var store = new IndexedDbSaveStore(browser);
        await store.WriteAsync(updated.Id, "Slot", activeBefore);

        Assert.Throws<InvalidDataException>(() => ValidatedSaveImport.Prepare(divergent, "Slot", bytes));
        var import = ValidatedSaveImport.Prepare(updated, "Slot", bytes);
        browser.FailWrites = true;
        await Assert.ThrowsAsync<JSException>(() => import.StoreAsync(store, overwrite: true));
        Assert.Equal(activeBefore, await store.ReadAsync(updated.Id, "Slot"));
        Assert.Equal(activeBefore, active.Save());

        browser.FailWrites = false;
        await import.StoreAsync(store, overwrite: true);
        var stored = await store.ReadAsync(updated.Id, "Slot");
        Assert.Equal(bytes, stored);
        Assert.Equal(source.Events, StoryHistory.Restore(updated, stored).Events);
        Assert.Equal(activeBefore, active.Save());
    }

    [Fact]
    public void Loaded_compatible_slot_protects_progress_until_the_timeline_changes()
    {
        var (original, updated, _) = Revisions();
        var source = new StoryHistory(original, 7);
        source.Choose("c0");
        source.Back();
        var bytes = source.Save();
        var restored = StoryHistory.Restore(updated, bytes);
        var manual = StoryHistory.DescribeSave("Slot", bytes);
        var autosave = StoryHistory.DescribeSave(SaveEntry.AutosaveSlot, bytes);

        Assert.True(StoryPlayer.HasProtectedProgress([manual], restored.ProgressKey, restored.RestoredProgressKey));
        Assert.True(StoryPlayer.HasProtectedProgress([manual with { Slot = SaveEntry.QuickSlot }], restored.ProgressKey, restored.RestoredProgressKey));
        Assert.False(StoryPlayer.HasProtectedProgress([autosave], restored.ProgressKey, restored.RestoredProgressKey));
        Assert.False(StoryPlayer.HasProtectedProgress([manual with { ProgressKey = null }], restored.ProgressKey, restored.RestoredProgressKey));
        Assert.False(StoryPlayer.HasProtectedProgress([manual], null, restored.RestoredProgressKey));

        restored.Forward();
        Assert.False(StoryPlayer.HasProtectedProgress([manual], restored.ProgressKey, restored.RestoredProgressKey));
        var current = StoryHistory.DescribeSave("Current", restored.Save());
        Assert.True(StoryPlayer.HasProtectedProgress([current], restored.ProgressKey, restored.RestoredProgressKey));
    }

    private static (StoryDefinition Original, StoryDefinition Updated, StoryDefinition Divergent) Revisions()
    {
        var fixture = TestStories.Linear;
        var updated = fixture with { Tale = fixture.Tale.Replace("Mara's **bold**", "Mara's revised **bold**", StringComparison.Ordinal) };
        var divergent = fixture with
        {
            Tale = fixture.Tale.Replace("* [Walk onward] -> second",
                "* [Walk onward] { var:visits += 1 } -> second",
                StringComparison.Ordinal),
        };
        return (fixture.Compile(), updated.Compile(), divergent.Compile());
    }

    private sealed class MemoryIndexedDb : IJSRuntime, IJSObjectReference
    {
        private readonly Dictionary<(string Story, string Slot), byte[]> _saves = [];
        public bool FailWrites { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, params object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            params object?[]? args)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? result = null;
            switch (identifier)
            {
                case "import":
                    result = this;
                    break;
                case "read":
                    result = _saves[(Assert.IsType<string>(args![0]), Assert.IsType<string>(args[1]))].ToArray();
                    break;
                case "write":
                    if (FailWrites) throw new JSException("Simulated storage failure.");
                    var key = (Assert.IsType<string>(args![0]), Assert.IsType<string>(args[1]));
                    var data = Assert.IsType<byte[]>(args[2]);
                    var overwrite = Assert.IsType<bool>(args[3]);
                    if (!overwrite && _saves.ContainsKey(key)) throw new JSException("Save already exists.");
                    _saves[key] = data.ToArray();
                    break;
                default:
                    throw new InvalidOperationException(identifier);
            }
            return ValueTask.FromResult(result is TValue value ? value : default!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
