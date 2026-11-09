using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class ScenePickerTests : BunitContext
{
    [Fact]
    public void Lists_every_scene_before_a_search()
    {
        using var model = ComponentStory.Load();

        var picker = Show(model);

        Assert.Equal("2 matching scenes. Showing up to 50; narrow the search or enter an exact ID.", picker.Find("p").TextContent);
        Assert.Equal(["Select hall Great Hall", "Select cave Cave"], Buttons(picker));
    }

    [Fact]
    public void Search_matches_ids_and_titles_case_insensitively()
    {
        using var model = ComponentStory.Load();
        var picker = Show(model);

        picker.Find("input[aria-label='Search scenes']").Input("GREAT");
        Assert.Equal(["Select hall Great Hall"], Buttons(picker));
        Assert.StartsWith("1 matching scenes.", picker.Find("p").TextContent);

        picker.Find("input[aria-label='Search scenes']").Input("cav");
        Assert.Equal(["Select cave Cave"], Buttons(picker));

        picker.Find("input[aria-label='Search scenes']").Input("nowhere");
        Assert.Empty(Buttons(picker));
        Assert.StartsWith("0 matching scenes.", picker.Find("p").TextContent);
    }

    [Fact]
    public void Selecting_raises_the_scene_id()
    {
        using var model = ComponentStory.Load();
        var selected = new List<string>();
        var picker = Show(model, selected.Add);

        picker.FindAll("button").Single(button => button.TextContent == "Select cave Cave").Click();

        Assert.Equal(["cave"], selected);
        // The picker only reports the choice; the owner decides what to do with it.
        Assert.Equal("hall", model.Target);
    }

    [Fact]
    public void Shows_at_most_fifty_matches_but_counts_them_all()
    {
        using var model = ComponentStory.Load(ComponentStory.Rooms(60));

        var picker = Show(model);

        Assert.StartsWith("60 matching scenes.", picker.Find("p").TextContent);
        var buttons = Buttons(picker);
        Assert.Equal(50, buttons.Count);
        Assert.Equal("Select room001", buttons[0]);
        Assert.Equal("Select room050", buttons[^1]);

        picker.Find("input[aria-label='Search scenes']").Input("room05");
        Assert.Equal(Enumerable.Range(50, 10).Select(i => $"Select room0{i}"), Buttons(picker));
    }

    private IRenderedComponent<ScenePicker> Show(WorkbenchModel model, Action<string>? select = null) =>
        Render<ScenePicker>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.OnSelect, select ?? (_ => { })));

    private static List<string> Buttons(IRenderedComponent<ScenePicker> picker) =>
        picker.FindAll("ul[aria-label='Matching scenes'] button").Select(button => button.TextContent.Trim()).ToList();
}
