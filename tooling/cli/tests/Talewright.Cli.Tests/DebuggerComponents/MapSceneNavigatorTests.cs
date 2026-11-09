using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapSceneNavigatorTests : BunitContext
{
    private static readonly MapView View = new("Components", [
        Node("start", "Start (start)"),
        Node("stub:road", "Road (road)", stub: true),
        Node("road", "Road (road)"),
        Node("END", "End"),
    ], [], [], 0);

    private readonly List<string> _focused = [];
    private readonly List<string> _blurred = [];

    [Fact]
    public void Lists_every_scene_except_stubs_in_a_labelled_group()
    {
        var navigator = Show(null);

        var group = navigator.Find("div[role=group]");
        Assert.Equal("Scenes on the map. Use the arrow keys to move between scenes, and Home or End for the first or last.",
            group.GetAttribute("aria-label"));
        Assert.Equal(["Start (start)", "Road (road)", "End"], Buttons(navigator).Select(button => button.TextContent));
    }

    [Theory]
    [InlineData(null, "Start (start)")]
    [InlineData("missing", "Start (start)")]
    [InlineData("road", "Road (road)")]
    [InlineData("END", "End")]
    public void Only_the_selected_scene_or_else_the_first_is_a_tab_stop(string? selected, string tabStop)
    {
        var navigator = Show(selected);

        Assert.Equal([tabStop], Buttons(navigator).Where(button => button.GetAttribute("tabindex") == "0")
            .Select(button => button.TextContent));
        Assert.All(Buttons(navigator).Where(button => button.TextContent != tabStop),
            button => Assert.Equal("-1", button.GetAttribute("tabindex")));
    }

    [Fact]
    public void Focus_and_blur_report_the_scene_id()
    {
        var navigator = Show(null);

        Button(navigator, "Road (road)").Focus();
        Button(navigator, "Road (road)").Blur();

        Assert.Equal(["road"], _focused);
        Assert.Equal(["road"], _blurred);
    }

    [Theory]
    [InlineData("Start (start)", "ArrowRight", "Road (road)")]
    [InlineData("Start (start)", "ArrowDown", "Road (road)")]
    [InlineData("End", "ArrowLeft", "Road (road)")]
    [InlineData("End", "ArrowUp", "Road (road)")]
    [InlineData("End", "Home", "Start (start)")]
    [InlineData("Start (start)", "End", "End")]
    [InlineData("Start (start)", "Home", "Start (start)")]
    [InlineData("End", "End", "End")]
    public async Task Navigation_keys_move_focus_between_scenes(string from, string key, string to)
    {
        var navigator = Show(null);
        var expectedFocus = await ButtonFor(navigator, Scene(to));
        var previousFocusCount = JSInterop.Invocations.Count;

        await Button(navigator, from).KeyDownAsync(new KeyboardEventArgs { Key = key });

        var focus = JSInterop.VerifyFocusAsyncInvoke(previousFocusCount + 1).Last();
        Assert.Equal(expectedFocus, focus.Arguments[0]);
    }

    [Theory]
    [InlineData("Start (start)", "ArrowLeft")]
    [InlineData("Start (start)", "ArrowUp")]
    [InlineData("End", "ArrowRight")]
    [InlineData("End", "ArrowDown")]
    [InlineData("Road (road)", "Enter")]
    [InlineData("Road (road)", "a")]
    public void Keys_that_cannot_move_leave_focus_alone(string from, string key)
    {
        var navigator = Show(null);

        Button(navigator, from).KeyDown(new KeyboardEventArgs { Key = key });

        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public async Task Focus_scene_moves_focus_to_that_scene_and_ignores_unknown_scenes()
    {
        var navigator = Show(null);

        await navigator.InvokeAsync(() => navigator.Instance.FocusSceneAsync("missing"));
        Assert.Empty(JSInterop.Invocations);

        await AssertFocusScene(navigator, "start", "Start (start)");
        await AssertFocusScene(navigator, "road", "Road (road)");
        await AssertFocusScene(navigator, "END", "End");
    }

    [Fact]
    public async Task Reloading_with_different_scenes_focuses_the_current_buttons()
    {
        var navigator = Show(null);
        var reloaded = View with { Nodes = [Node("cave", "Cave (cave)"), Node("start", "Start (start)")] };

        navigator.Render(parameters => parameters.Add(component => component.View, reloaded));

        var expectedCave = await ButtonFor(navigator, "cave");
        var expectedStart = await ButtonFor(navigator, "start");
        var previousFocusCount = JSInterop.Invocations.Count;
        await Button(navigator, "Start (start)").KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal(expectedCave,
            JSInterop.VerifyFocusAsyncInvoke(previousFocusCount + 1).Last().Arguments[0]);

        await Button(navigator, "Cave (cave)").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Assert.Equal(expectedStart,
            JSInterop.VerifyFocusAsyncInvoke(previousFocusCount + 2).Last().Arguments[0]);
    }

    [Fact]
    public void An_empty_map_renders_an_empty_group()
    {
        var navigator = Show(null, View with { Nodes = [] });

        Assert.Empty(Buttons(navigator));
    }

    private IRenderedComponent<MapSceneNavigator> Show(string? selected, MapView? view = null) =>
        Render<MapSceneNavigator>(parameters => parameters
            .Add(component => component.View, view ?? View)
            .Add(component => component.SelectedNodeId, selected)
            .Add(component => component.OnFocus, (string scene) => _focused.Add(scene))
            .Add(component => component.OnBlur, (string scene) => _blurred.Add(scene)));

    private static IEnumerable<AngleSharp.Dom.IElement> Buttons(IRenderedComponent<MapSceneNavigator> navigator) =>
        navigator.FindAll("button");

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<MapSceneNavigator> navigator, string name) =>
        Buttons(navigator).Single(button => button.TextContent == name);

    private async Task AssertFocusScene(IRenderedComponent<MapSceneNavigator> navigator, string scene, string buttonText)
    {
        var before = JSInterop.Invocations.Count;
        await navigator.InvokeAsync(() => navigator.Instance.FocusSceneAsync(scene));
        var focus = JSInterop.VerifyFocusAsyncInvoke(before + 1).Last().Arguments[0];
        Assert.Equal(await ButtonFor(navigator, scene), focus);
        Assert.Contains(buttonText, navigator.FindAll("button").Select(button => button.TextContent));
    }

    private async Task<ElementReference> ButtonFor(IRenderedComponent<MapSceneNavigator> navigator, string scene)
    {
        var before = JSInterop.Invocations.Count;
        await navigator.InvokeAsync(() => navigator.Instance.FocusSceneAsync(scene));
        return Assert.IsType<ElementReference>(
            JSInterop.VerifyFocusAsyncInvoke(before + 1).Last().Arguments[0]);
    }

    private static string Scene(string name) => View.Nodes.First(node => !node.Stub && node.Name == name).Id;

    private static MapNode Node(string id, string name, bool stub = false) =>
        new(id, id == "END" ? null : id, MapView.MainArea, [name], name, null, id == "start", id == "END", false, false,
            true, stub, []);
}
