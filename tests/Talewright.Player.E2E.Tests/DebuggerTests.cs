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
            snapshot = await WaitFor(driver, "Source not adopted", "Play again");
            Assert.Contains("Play again", snapshot.Content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Native_watch_reload_preserves_entry_state_and_gates_yaml_until_explicit_restart()
    {
        var executable = Environment.GetEnvironmentVariable("TALEWRIGHT_DEBUGGER_EXECUTABLE")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_DEBUGGER_EXECUTABLE.");
        var root = Path.Combine(Path.GetTempPath(), "talewright-watch-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var artifacts = Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? root,
            "debugger-reload", Guid.NewGuid().ToString("N"));
        const string yaml = "id: reload\ntitle: Native reload\nstart: start\nvars: {count: 0}\ncharacters:\n  player: {first-name: Tester}\ncreation:\n  name: true\n";
        const string original = """
                === start
                * [Go] -> current
                === current
                Before reload. {player:first-name}.
                ~ var:count += 1
                * [Finish] -> END
                """;
        var tale = Path.Combine(root, "main.tale");
        var manifest = Path.Combine(root, "story.yaml");
        try
        {
            await File.WriteAllTextAsync(manifest, yaml);
            await File.WriteAllTextAsync(tale, original);
            await using var driver = new NativePlayerDriver(artifacts, executable);
            await driver.StartAsync(root);
            await WaitFor(driver, "Scene setup");
            await driver.FillAsync("Seed", "42");
            await driver.FillAsync("First name", "Alex");
            await driver.ClickAsync("Start story");
            await driver.ClickAsync("Start selected scene");
            await driver.ChooseAsync("Go");
            await WaitFor(driver, "Before reload.");
            await File.WriteAllTextAsync(tale, "=== start\n-> missing");
            var snapshot = await WaitFor(driver, "Story has compiler errors");
            Assert.Contains("Before reload.", snapshot.Content);
            var temp = Path.Combine(root, "editor.tmp");
            var updated = original.Replace("Before reload.", "After reload.").Replace("count += 1", "count += 2");
            await File.WriteAllTextAsync(temp, updated);
            File.Move(temp, tale, overwrite: true);
            snapshot = await WaitFor(driver, "After reload. Alex.");
            Assert.Contains("Reload boundary: current", snapshot.Content);
            Assert.Contains("Seed: 42", snapshot.Content);
            await driver.ActivateAsync("Flags, variables, factions, visits and triggers");
            snapshot = await WaitFor(driver, "var:count = 2");
            Assert.Contains("current visits: 1", snapshot.Content);
            await File.WriteAllTextAsync(manifest, yaml.Replace("Native reload", "Restarted title"));
            snapshot = await WaitFor(driver, "Restart required.");
            Assert.Contains("After reload.", snapshot.Content);
            await File.WriteAllTextAsync(tale, updated.Replace("After reload.", "After YAML restart."));
            snapshot = await WaitFor(driver, "Source compiled, but explicit Restart from source is required");
            Assert.DoesNotContain("After YAML restart.", snapshot.Content);
            await driver.ClickAsync("Restart from source");
            await WaitFor(driver, "Restarted title");
            await driver.ClickAsync("Start story");
            await driver.ClickAsync("Start selected scene");
            await driver.ChooseAsync("Go");
            snapshot = await WaitFor(driver, "After YAML restart. Alex.");
            Assert.Contains("Seed: 42", snapshot.Content);
            await File.WriteAllTextAsync(tale, updated.Replace("[Go]", "[Changed positional meaning]")
                .Replace("After reload.", "Incompatible progress."));
            snapshot = await WaitFor(driver, "previously consumed scene/choice meaning changed");
            Assert.Contains("After YAML restart.", snapshot.Content);
            Assert.DoesNotContain("Incompatible progress.", snapshot.Content);
            Assert.Equal(0, await driver.CloseWindowAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static async Task<AccessibleNode> WaitFor(NativePlayerDriver driver, params string[] texts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = await driver.SnapshotAsync();
            if (texts.All(text => snapshot.Content.Contains(text, StringComparison.Ordinal)))
            {
                return snapshot;
            }
            await Task.Delay(100, timeout.Token);
        }
    }
}
