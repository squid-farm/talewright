using System.Diagnostics;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
[Collection(BrowserCollection.Name)]
public sealed class NativeCliRunTests
{
    private static string Cli => Environment.GetEnvironmentVariable("TALEWRIGHT_CLI_EXECUTABLE")
        ?? throw new InvalidOperationException("Set TALEWRIGHT_CLI_EXECUTABLE or use player/tests/run_e2e.py linux.");

    [Fact]
    public async Task Run_plays_saves_restores_and_exits_when_the_window_closes()
    {
        using var story = new StoryDirectory();
        await using (var player = Driver())
        {
            await player.StartAsync("run", story.Root);
            await WaitFor(player, snapshot => snapshot.Content.Contains("First run passage.", StringComparison.Ordinal));
            await player.ClickAsync("More actions");
            var menu = await player.SnapshotAsync();
            Assert.DoesNotContain(menu.Descendants(), node => node.Role == "push button" && node.Name == "Library");
            Assert.DoesNotContain("Author Debugger", menu.Content);
            await player.ClickAsync("More actions");
            await player.ChooseAsync("Continue");
            await WaitFor(player, snapshot => snapshot.Content.Contains("Second run passage.", StringComparison.Ordinal));
            await player.ClickAsync("Quick Save");
            await WaitFor(player, snapshot => snapshot.Content.Contains("Quick Save checkpoint saved.", StringComparison.Ordinal));
            Assert.Equal(0, await player.CloseWindowAsync());
        }

        await using (var player = Driver())
        {
            await player.StartAsync("run", story.Root);
            await WaitFor(player, snapshot => snapshot.Content.Contains("First run passage.", StringComparison.Ordinal));
            await player.ClickAsync("Saves");
            await player.ClickAsync("Load Quick Save checkpoint");
            await player.ClickAsync("Load save");
            await WaitFor(player, snapshot => snapshot.Content.Contains("Second run passage.", StringComparison.Ordinal));
            Assert.Equal(0, await player.CloseWindowAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupt_or_termination_stops_the_player_process_tree(bool terminate)
    {
        using var story = new StoryDirectory();
        await using var player = Driver();
        await player.StartAsync("run", story.Root);
        await WaitFor(player, snapshot => snapshot.Content.Contains("First run passage.", StringComparison.Ordinal));

        Assert.Equal(130, await player.CancelAsync(terminate));
    }

    private static NativePlayerDriver Driver() => new(
        Path.Combine(Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_RESULTS") ?? Path.GetTempPath(),
            "cli-run", Guid.NewGuid().ToString("N")),
        Cli, windowInChildProcess: true);

    private static async Task WaitFor(NativePlayerDriver player, Func<AccessibleNode, bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await player.SnapshotAsync();
            if (condition(snapshot)) return;
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), snapshot.Content);
            await Task.Delay(100);
        }
    }

    private sealed class StoryDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"talewright-run-story-{Guid.NewGuid():N}");

        public StoryDirectory()
        {
            Directory.CreateDirectory(Root);
            var manifest = new ManifestFaker($"cli-run-{Guid.NewGuid():N}", "Run story", "start");
            manifest.Character("player", "Tester");
            File.WriteAllText(Path.Combine(Root, "story.talewright.yaml"), manifest.Build(trailingNewline: true));
            File.WriteAllText(Path.Combine(Root, "main.tale"), string.Join('\n',
                SceneFaker.Header("start"), "First run passage.", ChoiceFaker.Create("next", "Continue"),
                SceneFaker.Header("next"), "Second run passage.", ChoiceFaker.Create("END", "Finish")));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
