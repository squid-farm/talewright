namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(BrowserCollection.Name)]
public sealed class DebuggerTests
{
    [Fact]
    public async Task Native_workbench_jumps_with_setup_inspects_branches_and_recovers_from_invalid_source()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE to the built or installed debugger DLL.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-debug-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger", Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "story.yaml"),
                "id: native-debug\ntitle: Native Debug\nstart: scene1\nflags: [ready]\ncharacters:\n  player: {first-name: Tester}\n");
            var tale = Path.Combine(root, "main.tale");
            await File.WriteAllTextAsync(tale, "=== scene1\n-> missing");
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Story has compiler errors");
            await File.WriteAllTextAsync(tale, string.Join("\n", Enumerable.Range(1, 1000).Select(i => $$"""
                === scene{{i}}
                @title Chapter {{i}}
                Late scene {{i}}.
                * [Prepared] (requires: flag:ready) -> END
                * [Wait] -> END
                """)));
            await driver.ClickAsync("Reload story");
            await WaitFor(driver, "Scene setup");
            await driver.FillAsync("Search scenes", "Chapter 1000");
            await WaitFor(driver, "Select scene1000 Chapter 1000");
            await driver.ClickAsync("Select scene1000 Chapter 1000");
            await driver.FillAsync("Seed", "42");
            await driver.ActivateAsync("Prerequisite overrides");
            await driver.ActivateAsync("Override flag:ready");
            await driver.ActivateAsync("Value flag:ready");
            await driver.ClickAsync("Start selected scene");
            var snapshot = await WaitFor(driver, "Late scene 1000.");
            Assert.Contains("Synthetic history", snapshot.Content);
            Assert.Contains("Seed: 42", snapshot.Content);
            await driver.FillAsync("Filter decisions", "c0");
            snapshot = await WaitFor(driver, "ConditionTrue");
            Assert.Contains("Offered", snapshot.Content);
            await driver.ActivateAsync("Choice scene1000 c0: Offered (ConditionTrue)");
            snapshot = await WaitFor(driver, "Events [");
            Assert.Contains("ready", snapshot.Content);
            await driver.ChooseAsync("Prepared");
            await WaitFor(driver, "Play again");
            await File.WriteAllTextAsync(tale, "=== scene1\n-> missing");
            await driver.ClickAsync("Reload story");
            snapshot = await WaitFor(driver, "Source not adopted");
            Assert.Contains("Play again", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AccessibleNode> WaitFor(NativePlayerDriver driver, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = await driver.SnapshotAsync();
            if (snapshot.Content.Contains(text, StringComparison.Ordinal))
            {
                return snapshot;
            }
            await Task.Delay(100, timeout.Token);
        }
    }
}
