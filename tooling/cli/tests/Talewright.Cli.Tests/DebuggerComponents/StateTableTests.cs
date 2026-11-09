using Bunit;
using Talewright.Debugger;
using Talewright.Runtime;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class StateTableTests : BunitContext
{
    [Fact]
    public void Shows_characters_and_world_state_after_the_run()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        var table = Show(model.Inspection!);

        var character = table.FindAll("details")[0];
        Assert.Equal("player: Ada Vale", character.QuerySelector("summary")!.TextContent);
        Assert.Equal("Base and effective stats", character.QuerySelector("caption")!.TextContent);
        var stats = character.QuerySelectorAll("tbody tr").Select(row => row.TextContent).ToList();
        Assert.Equal(model.Inspection!.Characters[0].Stats.Count, stats.Count);
        Assert.Contains("str Strength1010", stats);
        Assert.Contains("level Level11", stats);
        Assert.Equal(["lantern Lantern: 1"], character.QuerySelectorAll("ul > li").Select(item => item.TextContent));

        var world = table.FindAll("details")[1];
        Assert.Equal("Flags, variables, factions, visits and triggers", world.QuerySelector("summary")!.TextContent);
        Assert.Equal(
        [
            "flag:ready = True",
            "var:count (integer) = 1",
            "var:weather (text) = \"sunny\"",
            "faction:river = 1 (Neutral)",
            "hall visits: 1",
            "cave visits: 1",
            "counted fired: True",
        ], world.QuerySelectorAll("li").Select(item => item.TextContent));
    }

    [Fact]
    public void Text_variables_are_shown_as_quoted_literals()
    {
        using var model = ComponentStory.Load();
        var weather = Assert.Single(model.Texts);
        weather.Enabled = true;
        weather.Value = "a \"quoted\" <b>value</b>";
        model.Start();
        Assert.Null(model.Error);

        var table = Show(model.Inspection!);

        var item = table.FindAll("li").Single(item => item.TextContent.StartsWith("var:weather", StringComparison.Ordinal));
        Assert.Equal("\"a \\u0022quoted\\u0022 \\u003Cb\\u003Evalue\\u003C/b\\u003E\"", item.QuerySelector("code")!.TextContent);
        Assert.Empty(item.QuerySelectorAll("b"));
    }

    [Fact]
    public void Filter_narrows_characters_and_world_rows_case_insensitively()
    {
        using var model = ComponentStory.Start();

        var byName = Show(model.Inspection!, "VALE");
        Assert.Equal(["player: Ada Vale", "Flags, variables, factions, visits and triggers"],
            byName.FindAll("details > summary").Select(summary => summary.TextContent));
        Assert.Empty(byName.FindAll("details")[1].QuerySelectorAll("li"));

        var byVariable = Show(model.Inspection!, "var:");
        Assert.Single(byVariable.FindAll("details"));
        Assert.Equal(["var:count (integer) = 0", "var:weather (text) = \"sunny\""],
            byVariable.FindAll("li").Select(item => item.TextContent));

        var byId = Show(model.Inspection!, "C");
        Assert.Equal(["var:count (integer) = 0", "cave visits: 0", "counted fired: False"],
            byId.FindAll("details")[0].QuerySelectorAll("li").Select(item => item.TextContent));
    }

    [Fact]
    public void Typing_raises_the_filter_without_applying_it_itself()
    {
        using var model = ComponentStory.Start();
        var filters = new List<string>();
        var table = Show(model.Inspection!, changed: filters.Add);

        table.Find("input[aria-label='Filter inspection']").Input("cave");

        Assert.Equal(["cave"], filters);
        // The owner holds the filter; until it passes one back, every row stays visible.
        Assert.Equal(7, table.FindAll("details")[1].QuerySelectorAll("li").Length);
    }

    [Fact]
    public void Shows_at_most_one_hundred_scene_visit_rows()
    {
        using var model = ComponentStory.Load(ComponentStory.Rooms(120));
        model.Start();
        Assert.Null(model.Error);

        var table = Show(model.Inspection!);

        var visits = table.FindAll("li").Select(item => item.TextContent).Where(text => text.Contains(" visits: ")).ToList();
        Assert.Equal(100, visits.Count);
        Assert.Equal("room001 visits: 1", visits[0]);
        Assert.Equal("room100 visits: 0", visits[^1]);
        Assert.Contains("Showing at most 100 scene visit rows.", table.Markup);

        var narrowed = Show(model.Inspection!, "room11");
        Assert.Equal(Enumerable.Range(110, 10).Select(i => $"room{i} visits: 0"),
            narrowed.FindAll("li").Select(item => item.TextContent));
    }

    private IRenderedComponent<StateTable> Show(AuthorInspection inspection, string filter = "", Action<string>? changed = null) =>
        Render<StateTable>(parameters => parameters
            .Add(component => component.Inspection, inspection)
            .Add(component => component.Filter, filter)
            .Add(component => component.FilterChanged, changed ?? (_ => { })));
}
