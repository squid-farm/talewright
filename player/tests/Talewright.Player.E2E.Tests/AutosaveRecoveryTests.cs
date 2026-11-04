using Talewright.Player.Saves;
using Talewright.Runtime;
using Talewright.Runtime.Saves;

namespace Talewright.Player.E2E.Tests;

public sealed class AutosaveRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"talewright-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task Rejected_autosave_is_preserved_exactly_before_the_new_autosave_is_written()
    {
        var store = new FileSaveStore(_directory);
        byte[] previous = [0, 255, 1, 2];
        byte[] current = [3, 4, 5];
        await store.WriteAutosaveAsync("story", previous);
        await store.WriteQuickAsync("story", [6]);
        await store.WriteAsync("story", "Existing", [7]);
        var recovery = new AutosaveRecovery(store, "story", previous);

        Assert.True(await recovery.WriteAsync(current, () => true));

        Assert.False(recovery.NeedsPreservation);
        Assert.StartsWith("Recovered autosave ", recovery.RecoveredSlot);
        Assert.Equal(previous, await store.ReadAsync("story", recovery.RecoveredSlot!));
        Assert.Equal(current, await store.ReadAutosaveAsync("story"));
        Assert.Equal(new byte[] { 6 }, await store.ReadQuickAsync("story"));
        Assert.Equal(new byte[] { 7 }, await store.ReadAsync("story", "Existing"));
        Assert.Equal(2, (await store.ListAsync("story")).Count);

        Assert.True(await recovery.WriteAsync([8], () => true));
        Assert.Equal(2, (await store.ListAsync("story")).Count);
        Assert.Equal(previous, await store.ReadAsync("story", recovery.RecoveredSlot!));
        Assert.Equal(new byte[] { 8 }, await store.ReadAutosaveAsync("story"));
    }

    [Fact]
    public async Task A_failed_recovery_copy_never_overwrites_the_autosave_and_can_be_retried()
    {
        var fileStore = new FileSaveStore(_directory);
        byte[] previous = [1, 2, 3];
        await fileStore.WriteAutosaveAsync("story", previous);
        var store = new FaultingSaveStore(fileStore) { FailNamedWrite = true };
        var recovery = new AutosaveRecovery(store, "story", previous);

        await Assert.ThrowsAsync<IOException>(() => recovery.WriteAsync([4], () => true));

        Assert.True(recovery.NeedsPreservation);
        Assert.Null(recovery.RecoveredSlot);
        Assert.Equal(0, store.AutosaveWrites);
        Assert.Equal(previous, await fileStore.ReadAutosaveAsync("story"));
        Assert.Empty(await fileStore.ListAsync("story"));

        store.FailNamedWrite = false;
        Assert.True(await recovery.WriteAsync([4], () => true));
        Assert.Equal(previous, await fileStore.ReadAsync("story", recovery.RecoveredSlot!));
        Assert.Equal(new byte[] { 4 }, await fileStore.ReadAutosaveAsync("story"));
    }

    [Fact]
    public async Task An_unreadable_autosave_is_not_replaced_and_reading_is_retried()
    {
        var fileStore = new FileSaveStore(_directory);
        byte[] previous = [1, 2, 3];
        await fileStore.WriteAutosaveAsync("story", previous);
        var store = new FaultingSaveStore(fileStore) { FailAutosaveRead = true };
        var recovery = new AutosaveRecovery(store, "story", rejectedAutosave: null);

        await Assert.ThrowsAsync<IOException>(() => recovery.WriteAsync([4], () => true));

        Assert.True(recovery.NeedsPreservation);
        Assert.Equal(0, store.AutosaveWrites);
        Assert.Equal(previous, await fileStore.ReadAutosaveAsync("story"));

        store.FailAutosaveRead = false;
        Assert.True(await recovery.WriteAsync([4], () => true));
        Assert.Equal(previous, await fileStore.ReadAsync("story", recovery.RecoveredSlot!));
        Assert.Equal(new byte[] { 4 }, await fileStore.ReadAutosaveAsync("story"));
    }

    [Fact]
    public async Task A_missing_autosave_needs_no_recovery_copy()
    {
        var fileStore = new FileSaveStore(_directory);
        var recovery = new AutosaveRecovery(fileStore, "story", rejectedAutosave: null);

        Assert.True(await recovery.WriteAsync([4], () => true));

        Assert.False(recovery.NeedsPreservation);
        Assert.Null(recovery.RecoveredSlot);
        Assert.Equal(new byte[] { 4 }, await fileStore.ReadAutosaveAsync("story"));
        Assert.Empty(await fileStore.ListAsync("story"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(StoryHistory.MaxSaveBytes + 1)]
    public async Task Empty_or_oversized_autosaves_are_left_untouched(int length)
    {
        var fileStore = new FileSaveStore(_directory);
        await fileStore.WriteAutosaveAsync("story", [1]);
        var path = Directory.EnumerateFiles(_directory, "autosave" + SaveFormat.FileExtension, SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(path, new byte[length]);
        var store = new FaultingSaveStore(fileStore);
        var recovery = new AutosaveRecovery(store, "story", rejectedAutosave: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.WriteAsync([2], () => true));

        Assert.True(recovery.NeedsPreservation);
        Assert.Equal(0, store.AutosaveWrites);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Empty(await fileStore.ListAsync("story"));
    }

    [Fact]
    public async Task A_stale_write_preserves_the_original_but_does_not_replace_the_autosave()
    {
        var fileStore = new FileSaveStore(_directory);
        byte[] previous = [1, 2];
        await fileStore.WriteAutosaveAsync("story", previous);
        var store = new FaultingSaveStore(fileStore);
        var recovery = new AutosaveRecovery(store, "story", previous);
        var checks = 0;

        Assert.False(await recovery.WriteAsync([3], () => ++checks == 1));

        Assert.Equal(0, store.AutosaveWrites);
        Assert.Equal(previous, await fileStore.ReadAutosaveAsync("story"));
        Assert.Equal(previous, await fileStore.ReadAsync("story", recovery.RecoveredSlot!));
        Assert.True(await recovery.WriteAsync([4], () => true));
        Assert.Equal(new byte[] { 4 }, await fileStore.ReadAutosaveAsync("story"));
        Assert.Single(await fileStore.ListAsync("story"));
    }

    [Fact]
    public async Task A_failed_new_autosave_keeps_the_recovery_copy_for_the_next_retry()
    {
        var fileStore = new FileSaveStore(_directory);
        byte[] previous = [1, 2];
        await fileStore.WriteAutosaveAsync("story", previous);
        var store = new FaultingSaveStore(fileStore) { FailAutosaveWrite = true };
        var recovery = new AutosaveRecovery(store, "story", previous);

        await Assert.ThrowsAsync<IOException>(() => recovery.WriteAsync([3], () => true));

        Assert.False(recovery.NeedsPreservation);
        Assert.Equal(previous, await fileStore.ReadAutosaveAsync("story"));
        Assert.Equal(previous, await fileStore.ReadAsync("story", recovery.RecoveredSlot!));
        store.FailAutosaveWrite = false;
        Assert.True(await recovery.WriteAsync([4], () => true));
        Assert.Equal(new byte[] { 4 }, await fileStore.ReadAutosaveAsync("story"));
        Assert.Single(await fileStore.ListAsync("story"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FaultingSaveStore(ISaveStore inner) : ISaveStore
    {
        public bool FailAutosaveRead { get; set; }
        public bool FailNamedWrite { get; set; }
        public bool FailAutosaveWrite { get; set; }
        public int AutosaveWrites { get; private set; }

        public Task<IReadOnlyList<string>> ListAsync(string storyId) => inner.ListAsync(storyId);
        public Task<byte[]> ReadAsync(string storyId, string slot) => inner.ReadAsync(storyId, slot);
        public Task<byte[]> ReadQuickAsync(string storyId) => inner.ReadQuickAsync(storyId);
        public Task WriteQuickAsync(string storyId, byte[] data) => inner.WriteQuickAsync(storyId, data);

        public Task<byte[]> ReadAutosaveAsync(string storyId) => FailAutosaveRead
            ? Task.FromException<byte[]>(new IOException("Read failed."))
            : inner.ReadAutosaveAsync(storyId);

        public Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false) => FailNamedWrite
            ? Task.FromException(new IOException("Recovery copy failed."))
            : inner.WriteAsync(storyId, slot, data, overwrite);

        public Task WriteAutosaveAsync(string storyId, byte[] data)
        {
            AutosaveWrites++;
            return FailAutosaveWrite
                ? Task.FromException(new IOException("Autosave failed."))
                : inner.WriteAutosaveAsync(storyId, data);
        }
    }
}
