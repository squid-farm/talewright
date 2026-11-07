using System.Text;
using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class DiagnosticReportBrowserTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Failed_uploads_can_be_inspected_downloaded_and_do_not_survive_reload()
    {
        await UploadBytes("PRIVATE_ARCHIVE.zip", TestArchives.Invalid("zip"));
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Couldn't read");
        await UploadBytes("broken.zip", TestArchives.Invalid("tale"));
        await Expect(Page.Locator(".tw-diagnostics")).ToContainTextAsync("TW2002");

        await Page.SetViewportSizeAsync(320, 720);
        var summary = Page.Locator(".tw-diagnostic-report summary");
        await summary.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToBeVisibleAsync();
        await Expect(preview).ToContainTextAsync("Upload story: invalid data");
        await Expect(preview).ToContainTextAsync("Compile story: compiler diagnostic (TW2002)");
        var inspected = await preview.InnerTextAsync();
        Assert.Contains("Host: Browser", inspected);
        Assert.DoesNotContain("PRIVATE_ARCHIVE", inspected);
        Assert.DoesNotContain("chapters/main.tale", inspected);
        Assert.DoesNotContain("Missing destination", inspected);
        var overflow = await Page.Locator(".tw-diagnostic-report").EvaluateAsync<string>("""
            panel => JSON.stringify({
                viewport: innerWidth,
                right: panel.getBoundingClientRect().right,
                elements: [...panel.querySelectorAll('*')].map(element => ({
                    name: element.className || element.tagName,
                    right: element.getBoundingClientRect().right,
                    scrollWidth: element.scrollWidth,
                    clientWidth: element.clientWidth
                })).filter(element => element.right > innerWidth + 1 || element.scrollWidth > element.clientWidth + 1)
            })
            """);
        using (var layout = System.Text.Json.JsonDocument.Parse(overflow))
        {
            Assert.True(layout.RootElement.GetProperty("right").GetDouble() <=
                layout.RootElement.GetProperty("viewport").GetDouble() + 1,
                $"The report should fit the viewport: {overflow}");
            Assert.Equal(0, layout.RootElement.GetProperty("elements").GetArrayLength());
        }

        var download = await Page.RunAndWaitForDownloadAsync(() =>
            Page.GetByRole(AriaRole.Button, new() { Name = "Download diagnostic report" }).ClickAsync());
        Assert.Equal("talewright-diagnostics.txt", download.SuggestedFilename);
        await using var stream = await download.CreateReadStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(inspected.ReplaceLineEndings("\n"), (await reader.ReadToEndAsync()).ReplaceLineEndings("\n"));

        await ReloadAsync();
        await Page.Locator(".tw-diagnostic-report summary").ClickAsync();
        await Expect(Page.GetByLabel("Diagnostic report")).ToContainTextAsync("Recent warnings and errors: 0");
    }

    [Fact]
    public async Task Image_import_and_storage_failures_keep_live_story_context_without_sensitive_details()
    {
        var fixture = TestStories.Linear with
        {
            Yaml = TestStories.DefaultManifest($"diagnostics-{Guid.NewGuid():N}").Build(),
        };
        await Upload(fixture);
        var image = Page.Locator(".tw-media").First;
        await image.ScrollIntoViewIfNeededAsync();
        await Expect(image).ToContainTextAsync("This image could not be displayed.");

        await Page.Locator(".tw-diagnostic-report summary").ClickAsync();
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToContainTextAsync($"Story ID: {fixture.Compile().Id}");
        await Expect(preview).ToContainTextAsync("Scene ID: start");
        await Expect(preview).ToContainTextAsync("Load image: invalid data");

        await Choose("Walk onward");
        await Expect(preview).ToContainTextAsync("Scene ID: second");
        await Driver.ClickAsync("Saves");
        await Page.GetByLabel("Import save file", new() { Exact = true }).SetInputFilesAsync(new FilePayload
        {
            Name = "PRIVATE_SAVE.twsave",
            MimeType = "application/octet-stream",
            Buffer = Encoding.UTF8.GetBytes("PRIVATE_SAVE_CONTENT"),
        });
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("malformed");
        await Driver.ClickAsync("Close saves");
        await Expect(preview).ToContainTextAsync("Import saved game:");

        await Page.EvaluateAsync("""
            () => Object.defineProperty(globalThis, 'indexedDB', {
                configurable: true,
                value: { open() { throw new DOMException('PRIVATE_STORAGE_DETAIL', 'SecurityError'); } }
            })
            """);
        await Driver.ClickAsync("Saves");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("storage is blocked");
        await Driver.ClickAsync("Close saves");
        await Expect(preview).ToContainTextAsync("List saved games: host operation failed");
        var report = await preview.InnerTextAsync();
        Assert.Contains("scene=second", report);
        Assert.DoesNotContain("PRIVATE_SAVE", report);
        Assert.DoesNotContain("PRIVATE_STORAGE_DETAIL", report);
        Assert.DoesNotContain("Reserved artwork", report);
        Assert.DoesNotContain("Second passage text.", report);
    }

    [Fact]
    public async Task Malformed_browser_preferences_are_reported_without_the_stored_value()
    {
        await Page.EvaluateAsync(
            "() => localStorage.setItem('talewright.player.preferences', 'PRIVATE_PREFERENCE_CONTENT')");
        await ReloadAsync();
        await Page.Locator(".tw-diagnostic-report summary").ClickAsync();
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToContainTextAsync("Warning  Read player settings: invalid data");
        await Expect(preview).Not.ToContainTextAsync("PRIVATE_PREFERENCE_CONTENT");
    }

    [Fact]
    public async Task Failed_report_download_is_visible_and_recorded_without_exception_details()
    {
        await Page.Locator(".tw-diagnostic-report summary").ClickAsync();
        await Page.EvaluateAsync("""
            () => { URL.createObjectURL = () => { throw new Error('PRIVATE_REPORT_EXPORT_ERROR'); }; }
            """);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Download diagnostic report" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("could not be downloaded");
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToContainTextAsync("Export diagnostic report: host operation failed");
        await Expect(preview).Not.ToContainTextAsync("PRIVATE_REPORT_EXPORT_ERROR");
    }
}
