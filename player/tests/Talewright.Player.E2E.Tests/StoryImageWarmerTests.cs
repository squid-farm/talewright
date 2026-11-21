using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Diagnostics;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Runtime;
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
        private readonly ConcurrentQueue<string> _displayed = new();
        private int _active;
        private int _maximumActive;
        private int _nextUrl;
        private int _startedCount;

        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public int StartedCount => Volatile.Read(ref _startedCount);
        public int StartedFor(string path) => _startedPaths.GetValueOrDefault(path);
        public IReadOnlyList<string> Revoked => _revoked.ToArray();
        public IReadOnlyList<string> Displayed => _displayed.ToArray();
        public int Displays { get; private set; }

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
                case "observe":
                    result = 1;
                    break;
                case "display":
                    Displays++;
                    _displayed.Enqueue(Assert.IsType<string>(args![1]));
                    result = true;
                    break;
                case "dimensions":
                    result = new[] { 1, 1 };
                    break;
                case "release":
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

    private sealed class DirectHost : ComponentBase
    {
        [Parameter, EditorRequired] public StoryHistory History { get; set; } = default!;
        [Parameter, EditorRequired] public Action<DirectHost> Capture { get; set; } = default!;
        private StoryImages? _images;
        public StoryPlayer Player { get; private set; } = default!;

        protected override void OnInitialized() => Capture(this);

        public void SetImages(StoryImages? images)
        {
            _images = images;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<StoryImages?>>(0);
            builder.AddAttribute(1, nameof(CascadingValue<StoryImages?>.Value), _images);
            builder.AddAttribute(2, nameof(CascadingValue<StoryImages?>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<StoryPlayer>(0);
                child.AddAttribute(1, nameof(StoryPlayer.Story), History.Story);
                child.AddAttribute(2, nameof(StoryPlayer.ResumeHistory), History);
                child.AddAttribute(3, nameof(StoryPlayer.AutosaveResumeChecked), true);
                child.AddComponentReferenceCapture(4, value => Player = (StoryPlayer)value);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

#pragma warning disable BL0006
    private sealed class MediaRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int _rootId;

        public async Task<MediaView> MountAsync(StoryImages images, PlayerDiagnostics diagnostics)
        {
            var component = (MediaView)InstantiateComponent(typeof(MediaView));
            component.Report = diagnostics;
            _rootId = AssignRootComponentId(component);
            await RenderRootComponentAsync(_rootId, Parameters(images));
            return component;
        }

        public Task UpdateAsync(StoryImages images) =>
            RenderRootComponentAsync(_rootId, Parameters(images));

        private static ParameterView Parameters(StoryImages images) =>
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(MediaView.Media)] = new RenderedMedia("images/1a.png", "First"),
                [nameof(MediaView.Images)] = images,
            });

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    [Fact]
    public async Task Changing_image_source_releases_old_lease_and_displays_replacement_source()
    {
        var first = new Fixture(1, 1);
        var second = new Fixture(1, 1);
        var browser = new Browser();
        var firstWarm = first.Images.Acquire("images/1a.png", browser);
        var secondWarm = second.Images.Acquire("images/1a.png", browser);
        var urls = await Task.WhenAll(firstWarm.Url, secondWarm.Url);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime>(browser)
            .BuildServiceProvider();
        await using var renderer = new MediaRenderer(services, services.GetRequiredService<ILoggerFactory>());
        try
        {
            var diagnostics = new PlayerDiagnostics();
            var component = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(first.Images, diagnostics));
            await renderer.Dispatcher.InvokeAsync(component.LoadAsync);
            Assert.Equal([urls[0]], browser.Displayed);

            await renderer.Dispatcher.InvokeAsync(() => renderer.UpdateAsync(second.Images));

            Assert.Equal([urls[0], urls[1]], browser.Displayed);
            Assert.Equal(2, browser.StartedCount);
            await firstWarm.DisposeAsync();
            Assert.Contains(urls[0], browser.Revoked);
            Assert.DoesNotContain(urls[1], browser.Revoked);
        }
        finally
        {
            await renderer.DisposeAsync();
            await firstWarm.DisposeAsync();
            await secondWarm.DisposeAsync();
            await first.Images.DisposeAsync();
            await second.Images.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_preclaimed_transfer_retries_at_viewport_even_with_other_claims(bool failBeforeViewport)
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        browser.FailNext("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        var diagnostics = new PlayerDiagnostics();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime>(browser)
            .BuildServiceProvider();
        await using var renderer = new MediaRenderer(services, services.GetRequiredService<ILoggerFactory>());
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            var first = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(fixture.Images, diagnostics));
            var second = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(fixture.Images, diagnostics));
            Assert.True(warmer.Navigate(Owner, fixture.Story, fixture.Images, new SceneId("scene-1"), TimeSpan.Zero));
            warmer.CompleteHandoff();

            Task? loading = null;
            if (!failBeforeViewport)
                loading = renderer.Dispatcher.InvokeAsync(first.LoadAsync);
            gate.TrySetResult();
            await browser.ReadFailureAsync();
            if (failBeforeViewport)
                loading = renderer.Dispatcher.InvokeAsync(first.LoadAsync);
            await loading!.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.Dispatcher.InvokeAsync(second.LoadAsync);

            Assert.Equal(2, browser.StartedFor("images/1a.png"));
            Assert.Equal(2, browser.Displays);
            Assert.Contains("Warning  Load image: host operation failed", diagnostics.CreateReport());
        }
        finally
        {
            gate.TrySetResult();
            await renderer.DisposeAsync();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_viewport_retry_reports_the_error_after_a_failed_preclaim()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        browser.FailNext("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        var diagnostics = new PlayerDiagnostics();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime>(browser)
            .BuildServiceProvider();
        await using var renderer = new MediaRenderer(services, services.GetRequiredService<ILoggerFactory>());
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            var media = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(fixture.Images, diagnostics));
            gate.TrySetResult();
            await browser.ReadFailureAsync();
            browser.FailNext("images/1a.png");
            await renderer.Dispatcher.InvokeAsync(media.LoadAsync);

            Assert.Equal(2, browser.StartedFor("images/1a.png"));
            Assert.Equal(0, browser.Displays);
            var report = diagnostics.CreateReport();
            Assert.Contains("Warning  Load image: host operation failed", report);
            Assert.Contains("Error  Load image: host operation failed", report);
        }
        finally
        {
            gate.TrySetResult();
            await renderer.DisposeAsync();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

#pragma warning restore BL0006

    [Fact]
    public async Task Direct_player_reconciles_image_source_changes_without_resetting_history()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                * [Continue] -> next
                === next
                Progress preserved.
                * [Go] -> destination
                === destination
                {image("1a.png", "Destination")}
                -> END
                """,
        }, ["images/1a.png"]);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var first = StoryImages.FromSource(source, new ImageSource());
        var replacementSource = new ImageSource();
        var replacement = StoryImages.FromSource(source, replacementSource);
        var browser = new Browser();
        var history = new StoryHistory(compilation.Story!, 42);
        history.Choose(history.Current.Choices[0].Key);
        Assert.Equal(1, history.Cursor);
        DirectHost? host = null;
        try
        {
            await using var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IJSRuntime>(browser)
                .AddSingleton<PlayerPreferencesStore>()
                .BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DirectHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(DirectHost.History)] = history,
                    [nameof(DirectHost.Capture)] = (Action<DirectHost>)(value => host = value),
                })));
            var originalHtml = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            Assert.Contains("Progress preserved.", originalHtml);
            Assert.NotNull(host);

            await renderer.Dispatcher.InvokeAsync(() => host.SetImages(first));
            Assert.Same(first, host.Player.Images);
            Assert.Equal(new SceneId("next"), history.Current.Scene);
            await browser.ReadStartsAsync(1);
            await renderer.Dispatcher.InvokeAsync(() => host.SetImages(replacement));
            await browser.ReadStartsAsync(1);
            Assert.Single(replacementSource.Opened);
            await renderer.Dispatcher.InvokeAsync(() => host.SetImages(null));
            await WaitUntilAsync(() => browser.Revoked.Count == 2);
            await renderer.Dispatcher.InvokeAsync(() => host.SetImages(first));
            await browser.ReadStartsAsync(1);

            Assert.Equal(originalHtml, await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            Assert.Equal(1, history.Cursor);
        }
        finally
        {
            await first.DisposeAsync();
            await replacement.DisposeAsync();
        }
    }

    [Fact]
    public async Task Starts_one_image_per_scene_and_a_slow_scene_does_not_block_the_others()
    {
        var fixture = new Fixture(5, 3);
        var browser = new Browser();
        var gates = Enumerable.Range(1, 5).Select(scene => browser.Block($"images/{scene}a.png")).ToArray();
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);

            Assert.Equal([
                "images/1a.png", "images/2a.png", "images/3a.png", "images/4a.png", "images/5a.png",
            ], await browser.ReadStartsAsync(5));
            Assert.Equal(5, browser.Active);
            Assert.Equal(5, browser.StartedCount);

            gates[0].TrySetResult();
            Assert.Equal(["images/1b.png", "images/1c.png"], await browser.ReadStartsAsync(2));
            Assert.False(gates[1].Task.IsCompleted);
            Assert.Equal(0, browser.StartedFor("images/2b.png"));
            Assert.Equal(5, browser.MaximumActive);
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task More_than_ten_connections_can_each_start_an_image()
    {
        var fixture = new Fixture(12, 1);
        var browser = new Browser();
        var gates = Enumerable.Range(1, 12).Select(scene => browser.Block($"images/{scene}a.png")).ToArray();
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            Assert.Equal(Enumerable.Range(1, 12).Select(scene => $"images/{scene}a.png"),
                await browser.ReadStartsAsync(12));
            Assert.Equal(12, browser.Active);
            Assert.Equal(12, browser.StartedCount);
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult();
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
            Assert.Equal(3, browser.MaximumActive);
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
    public async Task Reacquiring_an_in_flight_transfer_does_not_consume_additional_capacity()
    {
        var fixture = new Fixture(2, 1);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        StoryImages.Lease? presentation = null;
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(2);
            await WaitUntilAsync(() => browser.Active == 1);
            presentation = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));

            for (var cycle = 0; cycle < 4; cycle++)
            {
                warmer.Navigate(Owner, fixture.Story, fixture.Images, new SceneId("scene-1"), TimeSpan.Zero);
                warmer.CompleteHandoff();
                warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
                Assert.Equal("images/2a.png", Assert.Single(await browser.ReadStartsAsync(1)));
                await WaitUntilAsync(() => browser.Active == 1);
            }

            Assert.Equal(1, browser.StartedFor("images/1a.png"));
        }
        finally
        {
            gate.TrySetResult();
            if (presentation is not null) await presentation.DisposeAsync();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Revisited_scene_waits_for_its_existing_transfer_before_starting_the_next_image()
    {
        var fixture = new Fixture(1, 2);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        StoryImages.Lease? presentation = null;
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            presentation = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));
            warmer.Navigate(Owner, fixture.Story, fixture.Images, new SceneId("scene-1"), TimeSpan.Zero);
            warmer.CompleteHandoff();
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);

            Assert.Equal(1, browser.StartedCount);
            await presentation.DisposeAsync();
            presentation = null;
            var reacquired = Assert.IsType<StoryImages.Lease>(fixture.Images.TryAcquireExisting("images/1a.png"));
            await reacquired.DisposeAsync();
            gate.TrySetResult();
            Assert.Equal("images/1b.png", Assert.Single(await browser.ReadStartsAsync(1)));
            Assert.Equal(1, browser.StartedFor("images/1a.png"));
        }
        finally
        {
            gate.TrySetResult();
            if (presentation is not null) await presentation.DisposeAsync();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Replacement_image_generation_never_retains_an_old_entry_with_the_same_path(
        bool newStory, bool sameImages)
    {
        var old = new Fixture(1, 1);
        var replacement = new Fixture(1, 1);
        var browser = new Browser();
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, old.Story, old.Images, old.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            await WaitUntilAsync(() => browser.Active == 0);

            var story = newStory ? replacement.Story : old.Story;
            var images = sameImages ? old.Images : replacement.Images;
            warmer.Navigate(Owner, story, images, story.Start, TimeSpan.Zero);
            Assert.Equal("images/1a.png", Assert.Single(await browser.ReadStartsAsync(1)));
            if (sameImages) Assert.Equal(2, old.Source.Opened.Count);
            else Assert.Single(replacement.Source.Opened);
            Assert.Equal(2, browser.StartedFor("images/1a.png"));
        }
        finally
        {
            await warmer.DisposeAsync();
            await old.Images.DisposeAsync();
            await replacement.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disposal_waits_for_a_retired_in_flight_transfer_and_finishes_after_it_completes()
    {
        var fixture = new Fixture(1, 1);
        var browser = new Browser();
        var gate = browser.Block("images/1a.png");
        var warmer = new StoryImageWarmer(browser);
        try
        {
            warmer.Navigate(Owner, fixture.Story, fixture.Images, fixture.Story.Start, TimeSpan.Zero);
            await browser.ReadStartsAsync(1);
            warmer.Clear();
            var disposing = warmer.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            gate.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(browser.Revoked);
        }
        finally
        {
            gate.TrySetResult();
            await warmer.DisposeAsync();
            await fixture.Images.DisposeAsync();
        }
    }

    [Fact]
    public async Task Old_transfers_remain_tracked_across_story_replacement_without_blocking_new_scenes()
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
            var started = await browser.ReadStartsAsync(3);

            Assert.Equal(["images/new-1a.png", "images/new-2a.png", "images/new-3a.png"], started);
            Assert.Equal(3, browser.Active);
            Assert.Equal(4, browser.MaximumActive);

            firstNewGate.TrySetResult();
            secondNewGate.TrySetResult();
            var disposing = warmer.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            oldGate.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
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
