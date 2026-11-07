using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(SerialCollection.Name)]
public sealed class DiagnosticReportNativeTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private readonly string _fixtures = Path.Combine(Path.GetTempPath(), $"talewright-diagnostic-story-{Guid.NewGuid():N}");
    protected override bool AutoStartDesktop => false;

    [Fact]
    public async Task Failed_no_library_startup_can_inspect_and_export_a_private_report()
    {
        Directory.CreateDirectory(_fixtures);
        var path = Path.Combine(_fixtures, "PRIVATE_STORY.zip");
        await File.WriteAllBytesAsync(path, TestArchives.Invalid("zip"));
        await Driver.StartAsync("--no-library", path);
        await AssertState(state => Assert.Contains("Couldn't open", state.Library));
        await AssertLibraryUnavailable();

        var native = Assert.IsType<NativePlayerDriver>(Driver);
        await native.ActivateAsync("Diagnostics");
        var snapshot = await WaitForAsync(native, "Open story: invalid data");
        Assert.Contains("Diagnostic report", snapshot.Content);
        Assert.Contains("Host: Desktop", snapshot.Content);

        var directory = Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY")
            ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
        var reportPath = Path.Combine(directory, "talewright-diagnostics.txt");
        await Driver.ClickAsync("Download diagnostic report");
        var timer = Stopwatch.StartNew();
        string report;
        while (true)
        {
            report = File.Exists(reportPath) ? await File.ReadAllTextAsync(reportPath) : "";
            if (report.Contains("Open story: invalid data", StringComparison.Ordinal)) break;
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The diagnostic report was not exported.");
            await Task.Delay(100);
        }

        Assert.Contains("Story ID: unavailable", report);
        Assert.Contains("Host: Desktop", report);
        Assert.DoesNotContain(path, report);
        Assert.DoesNotContain("PRIVATE_STORY", report);
    }

    [Fact]
    public async Task Native_report_matches_the_displayed_player_build_identity()
    {
        Directory.CreateDirectory(_fixtures);
        var path = Path.Combine(_fixtures, "invalid.zip");
        await File.WriteAllBytesAsync(path, TestArchives.Invalid("zip"));
        await Driver.StartAsync("--no-library", path);
        await AssertState(state => Assert.Contains("Couldn't open", state.Library));

        var native = Assert.IsType<NativePlayerDriver>(Driver);
        await native.ActivateAsync("Diagnostics");
        var snapshot = await WaitForAsync(native, "Player version:");
        var identity = Regex.Match(snapshot.Content,
            @"Talewright (?<version>[0-9A-Za-z.+-]+) \u00B7 commit (?<commit>[0-9A-Za-z]+) \u00B7 built [0-9]{4}-[0-9]{2}-[0-9]{2}");
        Assert.True(identity.Success, $"The player build identity was not displayed: {snapshot.Content}");
        Assert.Contains($"Player version: {identity.Groups["version"].Value}", snapshot.Content);
        Assert.Contains($"Player commit: {identity.Groups["commit"].Value}", snapshot.Content);
    }

    [Fact]
    public async Task Native_image_and_import_failures_are_reported_without_story_or_save_contents()
    {
        var fixture = TestStories.Linear with
        {
            Yaml = TestStories.DefaultManifest($"diagnostics-{Guid.NewGuid():N}").Build(),
        };
        await Driver.StartAsync();
        await Upload(fixture);
        var native = Assert.IsType<NativePlayerDriver>(Driver);
        await native.ScrollToImageAsync("Reserved artwork");
        await WaitForAsync(native, "This image could not be displayed.");
        await Driver.ClickAsync("Saves");
        await native.UploadSaveAsync("PRIVATE_SAVE.twsave", Encoding.UTF8.GetBytes("PRIVATE_SAVE_CONTENT"));
        await WaitForAsync(native, "malformed");
        await Driver.ClickAsync("Close saves");
        await native.ActivateAsync("Diagnostics");
        var snapshot = await WaitForAsync(native, "Import saved game:");
        Assert.Contains("Load image: invalid data", snapshot.Content);
        Assert.Contains($"Story ID: {fixture.Compile().Id}", snapshot.Content);
        Assert.DoesNotContain("PRIVATE_SAVE_CONTENT", snapshot.Content);
    }

    private static async Task<AccessibleNode> WaitForAsync(NativePlayerDriver driver, string expected)
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

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(_fixtures)) Directory.Delete(_fixtures, recursive: true);
        }
    }
}
