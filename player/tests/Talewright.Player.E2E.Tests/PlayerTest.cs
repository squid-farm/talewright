using System.Diagnostics;
using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Collection(BrowserCollection.Name)]
public abstract class PlayerTest(WebHostFixture host, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<string> _errors = [];
    private bool _reloading;
    private readonly string _artifacts = Path.Combine(
        Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.Combine(AppContext.BaseDirectory, "test-results"),
        PlayerTarget.IsDesktop ? "linux" : host.BrowserName, Guid.NewGuid().ToString("N"));
    protected IPlayerDriver Driver { get; private set; } = null!;
    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;
    protected ILocator Latest => Page.GetByRole(AriaRole.Region, new() { Name = "Current passage" });
    protected ILocator Choices => Page.GetByRole(AriaRole.List, new() { Name = "Story choices" }).GetByRole(AriaRole.Button);
    protected ILocator Sheet => Page.GetByRole(AriaRole.Complementary, new() { Name = "Character sheet" });
    protected ILocator SaveStatus => Page.GetByRole(AriaRole.Status).And(Page.Locator(".tw-save-status"));
    protected virtual bool AutoStartDesktop => true;
    protected virtual bool StartsInLibrary => true;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_artifacts);
        output.WriteLine($"Evidence: {_artifacts}");
        if (PlayerTarget.IsDesktop)
        {
            Driver = new NativePlayerDriver(_artifacts);
            if (AutoStartDesktop)
            {
                await Driver.StartAsync();
            }

            return;
        }

        output.WriteLine($"Browser: {host.BrowserName} {host.Browser.Version}; .NET: {Environment.Version}");
        Context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        Context.SetDefaultTimeout(10_000);
        await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        Page = await Context.NewPageAsync();
        Driver = new BrowserPlayerDriver(Page);
        Page.PageError += (_, error) => _errors.Add("Uncaught: " + error);
        Page.Console += (_, message) =>
        {
            output.WriteLine($"Console [{message.Type}]: {message.Text}");
            if (message.Type == "error")
            {
                _errors.Add(message.Text);
            }
        };
        Page.RequestFailed += (_, request) =>
        {
            if (!IsReloadCancellation(_reloading, request.ResourceType, request.Url, request.Failure))
            {
                _errors.Add($"Request failed: {request.Url}: {request.Failure}");
            }
        };
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                _errors.Add($"HTTP {response.Status}: {response.Url}");
            }
        };
        await Page.GotoAsync(host.BaseUrl);
        if (StartsInLibrary)
        {
            await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Talewright", Exact = true })).ToBeVisibleAsync();
            await Expect(Page.GetByLabel("Story archive")).ToBeVisibleAsync();
        }
    }

    protected async Task Upload(StoryFixture fixture, string prefix = "") =>
        await UploadBytes(fixture.Id + ".zip", fixture.Zip(prefix));

    protected async Task UploadBytes(string name, byte[] bytes) =>
        await Driver.UploadAsync(name, bytes);

    protected async Task Choose(string label) =>
        await Driver.ChooseAsync(label);

    internal static bool IsReloadCancellation(bool reloading, string resourceType, string url, string? failure) =>
        reloading && resourceType == "image" && url.StartsWith("blob:", StringComparison.Ordinal)
        && failure == "Load request cancelled";

    protected async Task Reload()
    {
        // WebKit can cancel document-owned Blob image requests during an intentional reload.
        _reloading = true;
        try
        {
            await Page.ReloadAsync();
        }
        finally
        {
            _reloading = false;
        }
    }

    protected async Task Library()
    {
        if ((await Driver.ObserveAsync()).PassageVisible)
        {
            await Driver.ClickAsync("More actions");
        }
        await Driver.ClickAsync("Library");
    }

    protected async Task Restart()
    {
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Restart");
        await Driver.ClickAsync("Restart story");
    }

    protected async Task AssertLibraryUnavailable(bool duringCreation = false)
    {
        var playing = (await Driver.ObserveAsync()).PassageVisible;
        if (playing)
        {
            await Driver.ClickAsync("More actions");
        }
        if (Driver is NativePlayerDriver native)
        {
            var timer = Stopwatch.StartNew();
            var snapshot = await native.SnapshotAsync();
            while (duringCreation && !snapshot.Descendants().Any(node =>
                node.Role == "push button" && node.Name == "Start story") &&
                timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
                snapshot = await native.SnapshotAsync();
            }
            if (duringCreation)
            {
                Assert.Contains(snapshot.Descendants(), node =>
                    node.Role == "push button" && node.Name == "Start story");
            }
            Assert.DoesNotContain(snapshot.Descendants(), node =>
                node.Name is "Library" or "Story archive");
        }
        else
        {
            if (duringCreation)
            {
                await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Start story", Exact = true })).ToBeVisibleAsync();
            }
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Library", Exact = true })).ToHaveCountAsync(0);
            await Expect(Page.GetByLabel("Story archive")).ToHaveCountAsync(0);
            await Expect(Page.Locator(".tw-library")).ToHaveCountAsync(0);
        }
        if (playing)
        {
            await Driver.PressAsync("Escape");
        }
    }

    protected async Task Observe(PlayerStep step)
    {
        await AssertState(state =>
        {
            Assert.True(state.PassageVisible, "Current passage is visible.");
            Assert.Contains(step.Contains, state.Passage);
            if (step.Choices is not null)
            {
                Assert.Equal(step.Choices, state.Choices);
            }

            foreach (var text in step.Sheet ?? [])
            {
                Assert.Contains(text, state.Sheet);
            }

            if (step.Ended)
            {
                Assert.True(state.Ended, "The ended story offers Play again.");
                Assert.Contains("THE END", state.Transcript.ToUpperInvariant());
            }
        });
    }

    protected async Task AssertState(Action<PlayerObservation> assertion, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        var maximumWait = timeout ?? TimeSpan.FromSeconds(15);
        while (true)
        {
            var state = await Driver.ObserveAsync();
            try
            {
                assertion(state);
                return;
            }
            catch (Xunit.Sdk.XunitException) when (timer.Elapsed < maximumWait)
            {
                await Task.Delay(100);
            }
        }
    }

    public virtual async Task DisposeAsync()
    {
        if (Driver is not null)
        {
            await Driver.DisposeAsync();
        }

        if (Context is null)
        {
            return;
        }

        try
        {
            if (Page is not null && !Page.IsClosed)
            {
                await Page.ScreenshotAsync(new() { Path = Path.Combine(_artifacts, "page.png"), FullPage = false });
                await File.WriteAllTextAsync(Path.Combine(_artifacts, "page.html"), await Page.ContentAsync());
                if (await Page.Locator("#blazor-error-ui").IsVisibleAsync())
                {
                    _errors.Add("The Blazor error banner is visible.");
                }
            }

            await File.WriteAllLinesAsync(Path.Combine(_artifacts, "errors.txt"), _errors);
            await Context.Tracing.StopAsync(new() { Path = Path.Combine(_artifacts, "trace.zip") });
        }
        finally
        {
            await Context.CloseAsync();
        }

        Assert.Empty(_errors);
    }
}
