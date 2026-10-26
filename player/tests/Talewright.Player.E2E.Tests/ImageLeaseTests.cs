using Microsoft.JSInterop;
using Talewright.Player.Library;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

public sealed class ImageLeaseTests
{
    private sealed class Browser : IJSRuntime, IJSObjectReference
    {
        public int Transfers { get; private set; }
        public List<string> Revoked { get; } = [];
        public TaskCompletionSource? Gate { get; set; }
        public bool Fail { get; set; }
        public int ModulesDisposed { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, params object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            params object?[]? args)
        {
            object? result = null;
            switch (identifier)
            {
                case "import": result = this; break;
                case "createUrl":
                    var number = ++Transfers;
                    var reference = Assert.IsType<DotNetStreamReference>(args![0]);
                    using (var bytes = new MemoryStream()) await reference.Stream.CopyToAsync(bytes, cancellationToken);
                    if (Gate is not null) await Gate.Task;
                    if (Fail) throw new JSException("Transfer failed");
                    result = "blob:test-" + number;
                    break;
                case "revokeUrl":
                    var url = Assert.IsType<string>(args![0]);
                    Assert.DoesNotContain(url, Revoked);
                    Revoked.Add(url);
                    break;
                default: throw new InvalidOperationException(identifier);
            }
            return result is TValue value ? value : default!;
        }
        public ValueTask DisposeAsync() { ++ModulesDisposed; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Same_resolved_path_shares_transfer_until_final_lease_then_reads_live_file_again()
    {
        using var files = new ImageStoryFiles();
        var browser = new Browser();
        await using var owner = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var first = owner.Acquire("images/photo.jpeg", browser);
        var second = owner.Acquire("images/photo.jpeg", browser);
        Assert.Same(first.Url, second.Url);
        Assert.Equal(await first.Url, await second.Url);
        Assert.Equal(1, browser.Transfers);
        await first.DisposeAsync();
        Assert.Empty(browser.Revoked);
        File.Delete(files.ImagePath);
        Assert.Equal("blob:test-1", await second.Url);
        await second.DisposeAsync();
        Assert.Equal(["blob:test-1"], browser.Revoked);
        var retry = owner.Acquire("images/photo.jpeg", browser);
        await Assert.ThrowsAsync<FileNotFoundException>(() => retry.Url);
        await retry.DisposeAsync();
    }

    [Fact]
    public async Task Initiator_unmount_does_not_cancel_transfer_needed_by_another_holder()
    {
        using var files = new ImageStoryFiles();
        var browser = new Browser { Gate = new() };
        await using var owner = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var first = owner.Acquire("images/photo.jpeg", browser);
        var second = owner.Acquire("images/photo.jpeg", browser);
        await first.DisposeAsync();
        Assert.False(second.Url.IsCompleted);
        browser.Gate.SetResult();
        Assert.Equal("blob:test-1", await second.Url);
        await second.DisposeAsync();
        Assert.Single(browser.Revoked);
    }

    [Fact]
    public async Task Late_retired_transfer_cannot_touch_new_entry_for_the_same_path()
    {
        using var files = new ImageStoryFiles();
        var browser = new Browser { Gate = new() };
        await using var owner = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var old = owner.Acquire("images/photo.jpeg", browser);
        while (browser.Transfers == 0) await Task.Delay(1);
        var retiring = old.DisposeAsync().AsTask();
        var replacement = owner.Acquire("images/photo.jpeg", browser);
        browser.Gate.SetResult();
        await retiring;
        Assert.Equal("blob:test-2", await replacement.Url);
        Assert.Equal(["blob:test-1"], browser.Revoked);
        var additional = owner.Acquire("images/photo.jpeg", browser);
        Assert.Same(replacement.Url, additional.Url);
        await replacement.DisposeAsync();
        await additional.DisposeAsync();
        Assert.Equal(["blob:test-1", "blob:test-2"], browser.Revoked);
    }

    [Fact]
    public async Task Different_paths_and_generations_do_not_share_and_owner_teardown_drains_urls()
    {
        using var files = new ImageStoryFiles();
        var source = StorySource.FromDirectory(files.Root);
        var browser = new Browser();
        var first = StoryImages.FromDirectory(source, files.Root);
        var second = StoryImages.FromDirectory(source, files.Root);
        var a = first.Acquire("images/photo.jpeg", browser);
        var b = first.Acquire("images/pixel.png", browser);
        var c = second.Acquire("images/photo.jpeg", browser);
        await Task.WhenAll(a.Url, b.Url, c.Url);
        Assert.Equal(3, browser.Transfers);
        await first.DisposeAsync();
        Assert.Equal(2, browser.Revoked.Count);
        Assert.DoesNotContain(await c.Url, browser.Revoked);
        await a.DisposeAsync();
        await b.DisposeAsync();
        Assert.Equal(2, browser.Revoked.Count);
        await second.DisposeAsync();
        await c.DisposeAsync();
        Assert.Equal(3, browser.Revoked.Count);
        Assert.Equal(2, browser.ModulesDisposed);
    }

    [Fact]
    public async Task Owner_disposal_waits_for_late_browser_transfer_and_revokes_exactly_once()
    {
        using var files = new ImageStoryFiles();
        var browser = new Browser { Gate = new() };
        var owner = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var lease = owner.Acquire("images/photo.jpeg", browser);
        while (browser.Transfers == 0) await Task.Delay(1);
        var disposing = owner.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        browser.Gate.SetResult();
        await disposing;
        Assert.Equal(["blob:test-1"], browser.Revoked);
        await lease.DisposeAsync();
        Assert.Single(browser.Revoked);
    }

    [Fact]
    public async Task Failed_transfer_is_retired_and_reacquisition_can_retry()
    {
        using var files = new ImageStoryFiles();
        var browser = new Browser { Fail = true };
        await using var owner = StoryImages.FromDirectory(StorySource.FromDirectory(files.Root), files.Root);
        var first = owner.Acquire("images/photo.jpeg", browser);
        await Assert.ThrowsAsync<JSException>(() => first.Url);
        await first.DisposeAsync();
        browser.Fail = false;
        var retry = owner.Acquire("images/photo.jpeg", browser);
        Assert.Equal("blob:test-2", await retry.Url);
        await retry.DisposeAsync();
        Assert.Equal(["blob:test-2"], browser.Revoked);
    }
}
