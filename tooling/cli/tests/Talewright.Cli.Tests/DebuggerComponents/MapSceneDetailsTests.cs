using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapSceneDetailsTests : BunitContext
{
    private static readonly MapItem Road = new("d0", "line0", "road", "Divert to road, main.tale line 3", false);
    private static readonly MapItem Well = new("o0", "line1", "well", "Hub offer of Well (well) via #water", true);

    private static readonly MapView View = new("Components", [
        Node("start", "Start (start)", hub: true, Road, Well),
        Node("road", "Road (road)", hub: false),
        Node("plaza", "Plaza (plaza)", hub: true),
    ], [], [], 0);

    private readonly List<string?> _highlights = [];
    private readonly List<string> _selected = [];
    private int _removed;

    [Fact]
    public void Without_a_selected_scene_it_asks_for_one()
    {
        var details = Show(null);

        Assert.Equal("Select a scene to list its diverts and hub offers.", details.Find("section p").TextContent);
        Assert.Empty(details.FindAll("button"));
    }

    [Fact]
    public void Map_scene_details_render_accessible_divert_and_hub_offer_lists()
    {
        var html = Show("start").Markup;

        Assert.Contains("aria-label=\"Scene details\"", html);
        Assert.Contains("<h3>Start (start)</h3>", html);
        Assert.Contains("<h4>Diverts: 1</h4>", html);
        Assert.Contains("<ul aria-label=\"Diverts\">", html);
        Assert.Contains("Divert to road, main.tale line 3", html);
        Assert.Contains("<h4>Hub offers: 1</h4>", html);
        Assert.Contains("<ul aria-label=\"Hub offers\">", html);
        Assert.Contains("Hub offer of Well (well) via #water", html);
    }

    [Fact]
    public void Scenes_without_items_show_zero_diverts_and_only_hubs_show_an_offer_count()
    {
        var road = Show("road");
        Assert.Equal(["Diverts: 0"], road.FindAll("h4").Select(heading => heading.TextContent));
        Assert.Empty(road.FindAll("ul"));

        var plaza = Show("plaza");
        Assert.Equal(["Diverts: 0", "Hub offers: 0"], plaza.FindAll("h4").Select(heading => heading.TextContent));
        Assert.Empty(plaza.FindAll("ul"));
    }

    [Fact]
    public void Choosing_a_divert_or_hub_offer_selects_its_target_scene()
    {
        var details = Show("start");

        Item(details, Road).Click();
        Item(details, Well).Click();

        Assert.Equal(["road", "well"], _selected);
    }

    [Fact]
    public void Focus_and_blur_highlight_and_clear_the_drawn_line()
    {
        var details = Show("start");

        Item(details, Road).Focus();
        Item(details, Road).Blur();

        Assert.Equal(["line0", null], _highlights);
    }

    [Fact]
    public void Pointer_hover_highlights_and_leaving_clears_only_an_unfocused_item()
    {
        var details = Show("start");

        Item(details, Road).MouseEnter();
        Item(details, Road).MouseLeave();
        Assert.Equal(["line0", null], _highlights);

        _highlights.Clear();
        Item(details, Well).Focus();
        Item(details, Well).MouseEnter();
        Item(details, Well).MouseLeave();
        Assert.Equal(["line1", "line1"], _highlights);
    }

    [Fact]
    public void Leaving_an_item_keeps_a_different_highlighted_line()
    {
        var details = Show("start");

        Item(details, Road).MouseEnter();
        Item(details, Well).MouseLeave();

        Assert.Equal(["line0"], _highlights);
    }

    [Fact]
    public void Removing_the_focused_item_restores_scene_focus_exactly_once()
    {
        var details = Show("start");
        Item(details, Road).Focus();

        details.Render(parameters => parameters.Add(component => component.SelectedNodeId, "road"));
        Assert.Equal(1, _removed);

        // The removed button never blurs, so later renders must not keep pulling focus back to the scene.
        details.Render(parameters => parameters.Add(component => component.SelectedNodeId, "plaza"));
        details.Render(parameters => parameters.Add(component => component.SelectedNodeId, "start"));
        Assert.Equal(1, _removed);
    }

    [Fact]
    public void Rerendering_while_the_focused_item_remains_does_not_move_focus()
    {
        var details = Show("start");
        Item(details, Well).Focus();

        details.Render(parameters => parameters.Add(component => component.SelectedNodeId, "start"));

        Assert.Equal(0, _removed);
    }

    [Fact]
    public void Removing_an_item_after_it_blurred_does_not_move_focus()
    {
        var details = Show("start");
        Item(details, Road).Focus();
        Item(details, Road).Blur();

        details.Render(parameters => parameters.Add(component => component.SelectedNodeId, "road"));

        Assert.Equal(0, _removed);
    }

    private IRenderedComponent<MapSceneDetails> Show(string? selected) => Render<MapSceneDetails>(parameters => parameters
        .Add(component => component.View, View)
        .Add(component => component.SelectedNodeId, selected)
        .Add(component => component.OnHighlight, (string? line) => _highlights.Add(line))
        .Add(component => component.OnSelectScene, (string scene) => _selected.Add(scene))
        .Add(component => component.OnFocusedItemRemoved, () => _removed++));

    private static AngleSharp.Dom.IElement Item(IRenderedComponent<MapSceneDetails> details, MapItem item) =>
        details.FindAll("button.map-item").Single(button => button.TextContent == item.Name);

    private static MapNode Node(string id, string name, bool hub, params MapItem[] items) =>
        new(id, id, MapView.MainArea, [id], name, null, id == "start", false, hub, false, true, false, items);
}
