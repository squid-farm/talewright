using System.Diagnostics;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(SerialCollection.Name)]
public sealed class NativeCliDiagnosticReportTests
{
    [Fact]
    public async Task Cli_player_can_inspect_and_export_a_diagnostic_report()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"talewright-cli-diagnostics-{Guid.NewGuid():N}");
        var storyId = $"cli-diagnostics-{Guid.NewGuid():N}";
        var fixture = TestStories.Linear with { Yaml = TestStories.DefaultManifest(storyId).Build() };
        var cli = Environment.GetEnvironmentVariable("TALEWRIGHT_CLI_EXECUTABLE")
            ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
        var exports = Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY")
            ?? throw new InvalidOperationException("Run native tests through player/tests/run_e2e.py.");
        var reportPath = Path.Combine(exports, "talewright-diagnostics.txt");
        try
        {
            fixture.WriteDirectory(directory);
            await using var player = new NativePlayerDriver(
                Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.GetTempPath(),
                    "cli-diagnostics", Guid.NewGuid().ToString("N")),
                cli, windowInChildProcess: true);
            await player.StartAsync("run", directory);
            await player.ActivateAsync("Diagnostics");
            var timer = Stopwatch.StartNew();
            while (!(await player.SnapshotAsync()).Content.Contains($"Story ID: {storyId}", StringComparison.Ordinal))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "The CLI player did not show its diagnostic report.");
                await Task.Delay(100);
            }
            await player.ClickAsync("Download diagnostic report");
            timer.Restart();
            string report;
            while (true)
            {
                report = File.Exists(reportPath) ? await File.ReadAllTextAsync(reportPath) : "";
                if (report.Contains($"Story ID: {storyId}", StringComparison.Ordinal)) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The CLI player did not export its report.");
                await Task.Delay(100);
            }
            Assert.Contains("Host: Desktop", report);
            Assert.DoesNotContain(directory, report);
            Assert.Equal(0, await player.CloseWindowAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
