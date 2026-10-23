using Talewright.Tale.Compilation;

namespace Talewright.Runtime.Tests;

public sealed class RicherHubTests
{
    private static StoryHistory History(string hub, string extra = "")
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = "id: hubs\ntitle: Hubs\nstart: hub\ncharacters:\n  player: {first-name: Tester}\n",
            ["main.tale"] = $$"""
                === hub
                @hub {{hub}}
                @hub-limit 2
                @tags a,b
                @priority 999
                * [Explicit] -> END
                {{extra}}
                -> END
                === blocked
                @tags a,b
                @priority 100
                @requires false
                -> hub
                === first
                @tags a,b
                @priority 10
                @once
                -> hub
                === second
                @tags a,b
                @priority 10
                -> hub
                === third
                @tags a
                @priority 9
                -> hub
                === fourth
                @tags a,b
                @priority 8
                -> hub
                """,
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
    public void Fallback_does_not_replace_an_explicit_choice()
    {
        var history = History("all(absent,b)");
        Assert.Single(history.Current.Choices);
        Assert.Equal("c0", history.Current.Choices[0].Key);
        history.Choose("c0");
        Assert.True(history.Current.IsEnded);
    }
}
