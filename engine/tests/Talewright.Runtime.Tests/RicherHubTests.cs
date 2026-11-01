using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class RicherHubTests
{
    private static StoryHistory History(string hub, string extra = "")
    {
        var manifest = new ManifestFaker("hubs", "Hubs", "hub");
        manifest.Character("player", "Tester");
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("hub"),
                SceneFaker.Hub(hub),
                SceneFaker.HubLimit(2),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(999),
                ChoiceFaker.Create("END", "Explicit"),
                extra,
                SceneFaker.Header("blocked"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(100),
                SceneFaker.Requires("false"),
                DivertFaker.To("hub"),
                SceneFaker.Header("first"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(10),
                SceneFaker.Once,
                DivertFaker.To("hub"),
                SceneFaker.Header("second"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(10),
                DivertFaker.To("hub"),
                SceneFaker.Header("third"),
                SceneFaker.Tags("a"),
                SceneFaker.Priority(9),
                DivertFaker.To("hub"),
                SceneFaker.Header("fourth"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(8),
                DivertFaker.To("hub")),
        });
        var compiled = StoryCompiler.Compile(source);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        return new StoryHistory(compiled.Story!, 42);
    }

    [Theory]
    [InlineData("all(a,b)", "fourth")]
    [InlineData("any(a,b)", "third")]
    [InlineData("a", "third")]
    public void Filters_before_limiting_keeps_stable_ties_explicit_choices_and_replays_once(string query, string next)
    {
        var history = History(query);
        Assert.Equal(["c0", "storylet:first", "storylet:second"], history.Current.Choices.Select(c => c.Key));
        history.Choose("storylet:first");
        history.Choose("continue");
        Assert.Equal(["c0", "storylet:second", $"storylet:{next}"], history.Current.Choices.Select(c => c.Key));
        var loaded = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Current.Choices.Select(c => c.Key), loaded.Current.Choices.Select(c => c.Key));
        loaded.Back();
        loaded.Back();
        Assert.Contains(loaded.Current.Choices, c => c.Key == "storylet:first");
    }

    [Fact]
    public void Empty_hub_keeps_the_explicit_exit_choice()
    {
        var history = History("all(absent,b)");
        Assert.Single(history.Current.Choices);
        Assert.Equal("c0", history.Current.Choices[0].Key);
        history.Choose("c0");
        Assert.True(history.Current.IsEnded);
    }
}
