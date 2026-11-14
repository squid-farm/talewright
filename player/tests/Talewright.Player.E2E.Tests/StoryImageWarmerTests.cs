using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.JSInterop;
using Talewright.Player.Library;
using Talewright.Core.Identity;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryImageWarmerTests
{
    private static readonly object Owner = new();

    internal sealed class ImageSource : IStoryImageSource
    {
        public ConcurrentQueue<string> Opened { get; } = new();
        public bool Disposed { get; private set; }

        public ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Disposed) throw new ObjectDisposedException(nameof(ImageSource));
            Opened.Enqueue(path);
            return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(path)));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Browser : IJSRuntime, IJSObjectReference
    {
        private readonly Channel<string> _started = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _failed = Channel.CreateUnbounded<string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _failures = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _pathsByContent = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _startedPaths = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _revoked = new();
        private int _active;
        private int _maximumActive;
        private int _nextUrl;
        private int _startedCount;

        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public int StartedCount => Volatile.Read(ref _startedCount);
        public int StartedFor(string path) => _startedPaths.GetValueOrDefault(path);
        public IReadOnlyList<string> Revoked => _revoked.ToArray();

        public TaskCompletionSource Block(string path) =>
            _gates.GetOrAdd(path, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void FailNext(string path) => _failures[path] = 0;
        public void RegisterContent(byte[] bytes, string path) =>
            _pathsByContent[Convert.ToHexString(SHA256.HashData(bytes))] = path;

        public async Task<string> ReadFailureAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _failed.Reader.ReadAsync(timeout.Token);
        }

        public async Task<IReadOnlyList<string>> ReadStartsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = new List<string>(count);
            while (result.Count < count)
                result.Add(await _started.Reader.ReadAsync(timeout.Token));
            return result;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, params object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
            params object?[]? args)
        {
            object? result = null;
            switch (identifier)
            {
                case "import":
                    result = this;
                    break;
                case "createUrl":
                {
                    var reference = Assert.IsType<DotNetStreamReference>(args![0]);
                    using var bytes = new MemoryStream();
                    await reference.Stream.CopyToAsync(bytes, cancellationToken);
                    var content = bytes.ToArray();
                    var path = _pathsByContent.GetValueOrDefault(Convert.ToHexString(SHA256.HashData(content)))
                        ?? Encoding.UTF8.GetString(content);
                    var active = Interlocked.Increment(ref _active);
                    UpdateMaximum(active);
                    Interlocked.Increment(ref _startedCount);
                    _startedPaths.AddOrUpdate(path, 1, static (_, count) => count + 1);
                    _started.Writer.TryWrite(path);
                    try
                    {
                        if (_gates.TryGetValue(path, out var gate))
                            await gate.Task.WaitAsync(cancellationToken);
                        if (_failures.TryRemove(path, out _))
                        {
                            _failed.Writer.TryWrite(path);
                            throw new JSException("Transfer failed.");
                        }
                        result = "blob:test-" + Interlocked.Increment(ref _nextUrl);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _active);
                    }
                    break;
                }
                case "revokeUrl":
                    _revoked.Enqueue(Assert.IsType<string>(args![0]));
                    break;
                default:
                    throw new InvalidOperationException(identifier);
            }
            return result is TValue value ? value : default!;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void UpdateMaximum(int active)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maximumActive);
                if (active <= current) return;
            } while (Interlocked.CompareExchange(ref _maximumActive, active, current) != current);
        }
    }

    private sealed class Fixture
    {
        public Fixture(int neighborCount, int imagesPerNeighbor, string prefix = "")
        {
            Story = Compile(neighborCount, imagesPerNeighbor, prefix);
            Source = new ImageSource();
            Images = StoryImages.FromSource(ImageCatalogue(Story), Source);
        }

        public StoryDefinition Story { get; }
        public ImageSource Source { get; }
        public StoryImages Images { get; }

        private static StoryDefinition Compile(int neighbors, int images, string prefix)
        {
            var sourcePaths = new List<string>();
            var story = new StringBuilder("=== start\n");
            for (var scene = 1; scene <= neighbors; scene++)
                story.AppendLine($"* [Scene {scene}] -> scene-{scene}");
            story.AppendLine("* [Finish] -> END");
            for (var scene = 1; scene <= neighbors; scene++)
            {
                story.AppendLine($"=== scene-{scene}");
                for (var image = 0; image < images; image++)
                {
                    var suffix = image < 26 ? ((char)('a' + image)).ToString() : "-" + image.ToString("D3");
                    var name = $"{prefix}{scene}{suffix}.png";
                    sourcePaths.Add("images/" + name);
                    story.AppendLine($"{{image(\"{name}\", \"{name}\")}}");
                }
                story.AppendLine("-> start");
            }

            var source = new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = TestStories.DefaultYaml,
                ["main.tale"] = story.ToString(),
            }, sourcePaths);
            var result = StoryCompiler.Compile(source);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!;
        }

        private static StorySource ImageCatalogue(StoryDefinition story)
        {
            return new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = TestStories.DefaultYaml,
                ["main.tale"] = "=== start\n-> END",
            }, StoryImageDiscovery.ConnectedScenes(story, story.Start).SelectMany(group => group.Paths));
        }
    }

    [Fact]
    public async Task Starts_round_robin_without_waiting_for_a_slow_image_and_bounds_active_requests()
    {
        var fixture = new Fixture(3, 3);
        var browser = new Browser();
        var slow = browser.Block("images/2a.png");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);

            var started = await browser.ReadStartsAsync(9);

            Assert.Equal([
                "images/1a.png", "images/2a.png", "images/3a.png",
                "images/1b.png", "images/2b.png", "images/3b.png",
                "images/1c.png", "images/2c.png", "images/3c.png",
            ], started);
            Assert.False(slow.Task.IsCompleted);
            Assert.InRange(browser.MaximumActive, 1, StoryImageWarmer.MaxConcurrentTransfers);
        }
        finally
        {
            slow.SetResult();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Directory_library_images_warm_for_a_direct_destination()
    {
        using var files = new ImageStoryFiles();
        await using var loaded = await new FileSystemStoryLibrary([files.Root]).OpenAsync(files.Root);
        var compilation = StoryCompiler.Compile(loaded.Source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var browser = new Browser();
        browser.RegisterContent(await File.ReadAllBytesAsync(files.ImagePath), "images/photo.jpeg");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, compilation.Story!, loaded.Images, compilation.Story!.Start, TimeSpan.Zero);

            Assert.Equal("images/photo.jpeg", Assert.Single(await browser.ReadStartsAsync(1)));
            await WaitUntilAsync(() => browser.Active == 0);
            var handoff = Assert.IsType<StoryImages.Lease>(loaded.Images!.TryAcquireExisting("images/photo.jpeg"));
            await handoff.Url;
            await handoff.DisposeAsync();
        }
        finally
        {
            await warmer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Does_not_open_or_queue_every_candidate_before_capacity_is_available()
    {
        var fixture = new Fixture(3, 100);
        var browser = new Browser();
        var gates = new[]
        {
            browser.Block("images/1a.png"),
            browser.Block("images/2a.png"),
            browser.Block("images/3a.png"),
        };
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(3);

            Assert.Equal(3, fixture.Source.Opened.Count);
            Assert.Equal(3, browser.Active);

            gates[0].TrySetResult();
            Assert.Equal("images/1b.png", Assert.Single(await browser.ReadStartsAsync(1)));
            Assert.Equal(StoryImageWarmer.MaxConcurrentTransfers, browser.MaximumActive);
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Immediate_navigation_replaces_a_delayed_plan()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                * [First] -> scene-1
                * [Finish] -> END
                === scene-1
                {image("first.png", "First")}
                * [Next] -> scene-2
                === scene-2
                {image("second.png", "Second")}
                -> END
                """,
        }, ["images/first.png", "images/second.png"]);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var story = compilation.Story!;
        var images = StoryImages.FromSource(source, new ImageSource());
        var browser = new Browser();
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmer = new StoryImageWarmer(browser, delay: async (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            await delayed.Task.WaitAsync(cancellationToken);
        });
        try
        {
            warmer.Navigate(Owner, story, images, story.Start, TimeSpan.FromSeconds(1));
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            warmer.Navigate(Owner, story, images, new SceneId("scene-1"), TimeSpan.Zero);

            var started = await browser.ReadStartsAsync(1);

            Assert.Equal("images/second.png", Assert.Single(started));
            Assert.DoesNotContain("images/1a.png", started);
        }
        finally
        {
            delayed.TrySetResult();
            await warmer.DisposeAsync();
            await images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Handoff_claims_an_existing_transfer_before_viewport_loading_and_keeps_presentation_ownership()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            await WaitUntilAsync(() => browser.Active == 0);

            Assert.True(warmer.Navigate(Owner, fixture.Story, fixture.Images, new SceneId("scene-1"), TimeSpan.Zero));
            var first = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));
            await first.Url;
            warmer.CompleteHandoff();
            var second = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));
            Assert.Same(first.Url, second.Url);

            await first.DisposeAsync();
            Assert.Empty(browser.Revoked);
            await second.DisposeAsync();
            await WaitUntilAsync(() => browser.Revoked.Count == 1);
            Assert.Single(browser.Revoked);
        }
        finally
        {
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Handoff_claims_in_flight_work_without_consuming_another_transfer_slot()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            Assert.True(warmer.Navigate(Owner, fixture.Story, fixture.Images,
                new SceneId("scene-1"), TimeSpan.Zero));

            var presentation = Assert.IsType<StoryImages.Lease>(
                fixture.Images.TryAcquireExisting("images/1a.png"));
            Assert.False(presentation.Url.IsCompleted);
            Assert.Equal(1, browser.StartedFor("images/1a.png"));
            warmer.CompleteHandoff();
            gate.TrySetResult();

            Assert.StartsWith("blob:test-", await presentation.Url);
            await presentation.DisposeAsync();
            await WaitUntilAsync(() => browser.Revoked.Count == 1);
            Assert.Equal(1, browser.StartedFor("images/1a.png"));
        }
        finally
        {
            gate.TrySetResult();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Old_transfers_keep_their_slots_across_story_replacement()
    {
        var old = new Fixture(1, 1, "old-");
        var current = new Fixture(3, 1, "new-");
        var browser = new Browser();
        var oldGate = browser.Block("images/old-1a.png");
        var firstNewGate = browser.Block("images/new-1a.png");
        var secondNewGate = browser.Block("images/new-2a.png");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, old.Story, old.Images, old.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            warmer.Navigate(Owner, current.Story, current.Images, current.Story.Start, TimeSpan.Zero);
            var firstTwo = await browser.ReadStartsAsync(2);

            Assert.Equal(["images/new-1a.png", "images/new-2a.png"], firstTwo);
            Assert.Equal(3, browser.Active);
            Assert.Equal(3, browser.MaximumActive);

            firstNewGate.TrySetResult();
            Assert.Equal("images/new-3a.png", Assert.Single(await browser.ReadStartsAsync(1)));
            Assert.True(oldGate.Task.IsCompleted is false);
        }
        finally
        {
            oldGate.TrySetResult();
            firstNewGate.TrySetResult();
            secondNewGate.TrySetResult();
            await warmer.DisposeAsync();
            await old.Images.DisposeAsync();
            await current.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_stale_component_cleanup_cannot_clear_a_replacement_players_plan()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var warmer = new StoryImageWarmer(browser);
        var oldOwner = new object();
        var replacementOwner = new object();
        try
        {
            warmer.Navigate(oldOwner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            warmer.Navigate(replacementOwner, fixture.Story, fixture.Images,
                fixture.Story.Start, TimeSpan.Zero);
            warmer.Clear(oldOwner);

            var claim = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));
            await claim.Url;
            Assert.Equal(1, browser.StartedFor("images/1a.png"));
            await claim.DisposeAsync();
        }
        finally
        {
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_warming_releases_its_lease_for_a_normal_lazy_retry()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        browser.FailNext("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            Assert.Equal("images/1a.png", await browser.ReadFailureAsync());
            await WaitForEntryRetirementAsync(fixture.Images, "images/1a.png");

            var retry = fixture.Images.Acquire("images/1a.png", browser);
            Assert.Equal("blob:test-1", await retry.Url);
            await retry.DisposeAsync();
            Assert.Single(browser.Revoked);
        }
        finally
        {
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task WaitForEntryRetirementAsync(StoryImages images, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var lease = images.TryAcquireExisting(path);
            if (lease is null) return;
            try
            {
                await Assert.ThrowsAsync<JSException>(() => lease.Url);
            }
            finally
            {
                await lease.DisposeAsync();
            }
            await Task.Delay(10, timeout.Token);
        }
    }
}
