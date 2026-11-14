using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class StandaloneLinuxFactAttribute : FactAttribute
{
    public StandaloneLinuxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1" || !PlayerTarget.IsDesktop)
        {
            Skip = "Requires a published Linux desktop artifact.";
        }
    }
}

[Trait("Artifact", "Standalone")]
[Collection(SerialCollection.Name)]
public sealed class StandaloneDiagnosticsTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    protected override bool StartsInLibrary => false;
    protected override bool AutoStartDesktop => false;

    [StandaloneFact]
    public async Task Published_game_can_inspect_and_download_a_report()
    {
        if (Driver is NativePlayerDriver native)
        {
            await native.StartAsync();
            if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE_CREATION") == "1")
            {
                await native.ActivateAsync("Start story");
            }
            await Driver.ClickAsync("More actions");
            await native.ActivateAsync("Diagnostics");
            var directory = Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY")
                ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
            var path = Path.Combine(directory, "talewright-diagnostics.txt");
            File.Delete(path);
            await Driver.ClickAsync("Download diagnostic report");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The diagnostic report was not exported.");
                await Task.Delay(100);
            }
            Assert.Contains("Host: Desktop", await File.ReadAllTextAsync(path));
        }
        else
        {
            await WaitForPublishedStoryAsync();
            await OpenPlayDiagnosticsAsync();
            var preview = Page.GetByLabel("Diagnostic report");
            await Expect(preview).ToContainTextAsync("Host: Browser");
            var download = await Page.RunAndWaitForDownloadAsync(() =>
                Page.GetByRole(AriaRole.Button, new() { Name = "Download diagnostic report" }).ClickAsync());
            Assert.Equal("talewright-diagnostics.txt", download.SuggestedFilename);
            await using var stream = await download.CreateReadStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Contains("Host: Browser", await reader.ReadToEndAsync());
        }
    }

    [StandaloneLinuxFact]
    [Trait("Host", "Linux")]
    public async Task Published_desktop_load_failure_can_inspect_and_export_a_report()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DESKTOP_EXECUTABLE")
            ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
        var root = Path.GetDirectoryName(executable)!;
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var runtimeDirectory = Path.Combine(root, runtime);
        var source = File.Exists(Path.Combine(runtimeDirectory, "Talewright.Host.Desktop")) ? runtimeDirectory : root;
        var copy = Path.Combine(Path.GetTempPath(), $"talewright-broken-published-{Guid.NewGuid():N}");
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (relative.StartsWith($"story{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                var destination = Path.Combine(copy, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var sourceExecutable = Path.Combine(source, "Talewright.Host.Desktop");
            var copiedExecutable = Path.Combine(copy, "Talewright.Host.Desktop");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(copiedExecutable, File.GetUnixFileMode(sourceExecutable));
            Directory.CreateDirectory(Path.Combine(copy, "story"));
            await File.WriteAllTextAsync(Path.Combine(copy, "story", "story.talewright.yaml"), "PRIVATE_INVALID: [");

            var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.GetTempPath(),
                "broken-published", Guid.NewGuid().ToString("N"));
            await using var native = new NativePlayerDriver(artifacts, copiedExecutable,
                xdgDataHome: Path.Combine(artifacts, "xdg-data"));
            await native.StartAsync();
            var snapshot = await WaitForNativeAsync(native, "TW4001");
            Assert.DoesNotContain(snapshot.Descendants(), node => node.Name is "Library" or "Story archive");

            await native.ActivateAsync("Diagnostics");
            snapshot = await WaitForNativeAsync(native, "Compile story: compiler diagnostic (TW4001)");
            Assert.Contains("Diagnostic report", snapshot.Content);

            var directory = Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY")
                ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
            var reportPath = Path.Combine(directory, "talewright-diagnostics.txt");
            File.Delete(reportPath);
            await native.ClickAsync("Download diagnostic report");
            var timer = Stopwatch.StartNew();
            string report;
            while (true)
            {
                report = File.Exists(reportPath) ? await File.ReadAllTextAsync(reportPath) : "";
                if (report.Contains("TW4001", StringComparison.Ordinal)) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The failed-startup report was not exported.");
                await Task.Delay(100);
            }
            Assert.Contains("Story ID: unavailable", report);
            Assert.DoesNotContain("PRIVATE_INVALID", report);
            Assert.DoesNotContain(copy, report);
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    [StandaloneFact]
    [Trait("Host", "Browser")]
    public async Task Published_report_matches_the_player_build_identity()
    {
        await WaitForPublishedStoryAsync();
        var response = await Page.APIRequest.GetAsync(new Uri(new Uri(Page.Url), "version.json").ToString());
        Assert.True(response.Ok, $"version.json returned HTTP {response.Status}");
        var identity = await response.JsonAsync();
        var version = identity!.Value.GetProperty("version").GetString();
        var commit = identity.Value.GetProperty("commit").GetString();
        Assert.False(string.IsNullOrEmpty(version));
        Assert.False(string.IsNullOrEmpty(commit));

        await OpenPlayDiagnosticsAsync();
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToContainTextAsync($"Player version: {version}");
        await Expect(preview).ToContainTextAsync($"Player commit: {commit}");
        var download = await Page.RunAndWaitForDownloadAsync(() =>
            Page.GetByRole(AriaRole.Button, new() { Name = "Download diagnostic report" }).ClickAsync());
        await using var stream = await download.CreateReadStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.TrimEntries);
        Assert.Contains($"Player version: {version}", lines);
        Assert.Contains($"Player commit: {commit}", lines);
    }

    [StandaloneFact]
    [Trait("Host", "Browser")]
    public async Task Published_story_load_failure_remains_inspectable_without_a_library()
    {
        await WaitForPublishedStoryAsync();

        const string privateUrl = "https://private.example/SECRET_TOKEN";
        var invalidIndex = JsonSerializer.Serialize(new Dictionary<string, string> { [privateUrl] = new('A', 64) });
        await Page.RouteAsync(new Regex("/story/index\\.json\\?"), route =>
            route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = invalidIndex }));
        await ReloadAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Invalid published story index entry");
        await Expect(Page.GetByLabel("Story archive")).ToHaveCountAsync(0);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Diagnostics", Exact = true }).ClickAsync();
        var preview = Page.GetByLabel("Diagnostic report");
        await Expect(preview).ToContainTextAsync("Open story: invalid data");
        await Expect(preview).Not.ToContainTextAsync(privateUrl);
        var download = await Page.RunAndWaitForDownloadAsync(() =>
            Page.GetByRole(AriaRole.Button, new() { Name = "Download diagnostic report" }).ClickAsync());
        await using var stream = await download.CreateReadStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var report = await reader.ReadToEndAsync();
        Assert.Contains("Story ID: unavailable", report);
        Assert.DoesNotContain(privateUrl, report);
    }

    private async Task WaitForPublishedStoryAsync()
    {
        var ready = Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE_CREATION") == "1"
            ? Page.GetByRole(AriaRole.Button, new() { Name = "Start story", Exact = true })
            : Latest;
        await Expect(ready).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    private async Task OpenPlayDiagnosticsAsync()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE_CREATION") == "1")
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = "Start story", Exact = true }).ClickAsync();
        }
        await Page.GetByRole(AriaRole.Button, new() { Name = "More actions", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Diagnostics", Exact = true }).ClickAsync();
    }

    private static async Task<AccessibleNode> WaitForNativeAsync(NativePlayerDriver driver, string expected)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync();
            if (snapshot.Content.Contains(expected, StringComparison.Ordinal)) return snapshot;
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Timed out waiting for {expected}.");
            await Task.Delay(100);
        }
    }
}
