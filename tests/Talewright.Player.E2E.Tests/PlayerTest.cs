using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Collection(BrowserCollection.Name)]
public abstract class PlayerTest(WebHostFixture host, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly List<string> _errors = [];
    private readonly string _artifacts = Path.Combine(
        Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.Combine(AppContext.BaseDirectory, "test-results"),
        host.BrowserName, Guid.NewGuid().ToString("N"));
    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;
    protected ILocator Latest => Page.GetByRole(AriaRole.Region, new() { Name = "Current passage" });
    protected ILocator Choices => Page.GetByRole(AriaRole.List, new() { Name = "Story choices" }).GetByRole(AriaRole.Button);
    protected ILocator Sheet => Page.GetByRole(AriaRole.Complementary, new() { Name = "Character sheet" });

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_artifacts);
        output.WriteLine($"Evidence: {_artifacts}");
        output.WriteLine($"Browser: {host.BrowserName} {host.Browser.Version}; .NET: {Environment.Version}");
        Context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        Context.SetDefaultTimeout(10_000);
        await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        Page = await Context.NewPageAsync();
        Page.PageError += (_, error) => _errors.Add("Uncaught: " + error);
        Page.Console += (_, message) =>
        {
            output.WriteLine($"Console [{message.Type}]: {message.Text}");
            if (message.Type == "error")
            {
                _errors.Add(message.Text);
            }
        };
        Page.RequestFailed += (_, request) => _errors.Add($"Request failed: {request.Url}: {request.Failure}");
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                _errors.Add($"HTTP {response.Status}: {response.Url}");
            }
        };
        await Page.GotoAsync("/");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Talewright", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Story archive")).ToBeVisibleAsync();
    }

    protected async Task Upload(StoryFixture fixture, string prefix = "") =>
        await UploadBytes(fixture.Id + ".zip", fixture.Zip(prefix));

    protected async Task UploadBytes(string name, byte[] bytes) =>
        await Page.GetByLabel("Story archive").SetInputFilesAsync(new FilePayload
        {
            Name = name,
            MimeType = "application/zip",
            Buffer = bytes,
        });

    protected async Task Choose(string label) =>
        await Choices.Filter(new() { HasTextRegex = new Regex(@"^\s*\d+\.\s*" + Regex.Escape(label) + @"\s*$") }).ClickAsync();

    protected async Task Library() => await Page.GetByRole(AriaRole.Button, new() { Name = "Library", Exact = true }).ClickAsync();

    protected async Task Observe(PlayerStep step)
    {
        await Expect(Latest).ToBeVisibleAsync();
        if (step.Contains.Length > 0)
        {
            await Expect(Latest).ToContainTextAsync(step.Contains);
        }

        if (step.Choices is not null)
        {
            await Expect(Choices).ToHaveCountAsync(step.Choices.Length);
            for (var i = 0; i < step.Choices.Length; i++)
            {
                await Expect(Choices.Nth(i)).ToHaveTextAsync($"{i + 1}. {step.Choices[i]}");
            }
        }

        foreach (var text in step.Sheet ?? [])
        {
            await Expect(Sheet).ToContainTextAsync(text);
        }

        if (step.Ended)
        {
            await Expect(Page.GetByText("The End", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true })).ToBeVisibleAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Context is null)
        {
            return;
        }

        try
        {
            if (Page is not null && !Page.IsClosed)
            {
                await Page.ScreenshotAsync(new() { Path = Path.Combine(_artifacts, "page.png"), FullPage = true });
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
