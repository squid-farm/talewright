using System.Diagnostics;
using System.Text;
using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class PerformanceTests : PlayerTest
{
    private readonly ITestOutputHelper _output;

    public PerformanceTests(WebHostFixture host, ITestOutputHelper output) : base(host, output) => _output = output;

    [Theory]
    [InlineData(1_000, 5_000)]
    [InlineData(10_000, 15_000)]
    public async Task Generated_story_compiles_and_becomes_playable_within_browser_budget(int scenes, int budgetMs)
    {
        var tale = new StringBuilder();
        for (var i = 0; i < scenes; i++)
        {
            tale.AppendLine($"=== scene.{i}");
            tale.AppendLine("~ var:visits += 1");
            tale.AppendLine("Generated passage for {player:first-name}; visits: {var:visits}.");
            tale.AppendLine($"* [Next] -> scene.{(i + 1) % scenes}");
        }
        var fixture = new StoryFixture("performance", """
            game:
              id: performance
              title: Performance
              start: scene.0
              seed: 42
            characters:
              player: {first-name: Tester}
            """, tale.ToString());
        var timer = Stopwatch.StartNew();
        await Upload(fixture);
        await Observe(new(null, "Generated passage for Tester", ["Next"]));
        timer.Stop();
        _output.WriteLine($"Upload+WASM compile+first render: {scenes} scenes, {timer.Elapsed.TotalMilliseconds:F2} ms; budget {budgetMs} ms.");
        Assert.True(timer.ElapsedMilliseconds <= budgetMs, $"Load took {timer.ElapsedMilliseconds} ms; budget {budgetMs} ms.");
        await Choose("Next");
        await Observe(new(null, "visits: 2"));
    }
}
