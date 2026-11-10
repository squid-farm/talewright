using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapTabTests : BunitContext
{
    public MapTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<MapExportDialog>();
    }

    [Fact]
    public void Draws_the_compiled_map_with_the_current_run_once()
    {
        using var model = ComponentStory.Start();

        var map = Show(model);

        var render = Assert.Single(JSInterop.Invocations["talewrightMap.render"]);
        var view = Assert.IsType<MapView>(render.Arguments[3]);
        Assert.Equal(["hall", "cave"], view.Nodes.Where(node => node.Scene is not null).Select(node => node.Scene));
        Assert.Equal(map.FindComponent<MapRoute>().Instance.Run, render.Arguments[4]);
        Assert.NotNull(render.Arguments[4]);
        Assert.Null(render.Arguments[5]);
        Assert.IsType<DotNetObjectReference<MapTab>>(render.Arguments[6]);
        Assert.Empty(map.FindAll("[role=alert]"));
        Assert.False(Button(map, "Export SVG").HasAttribute("disabled"));
        Assert.False(Button(map, "Export PNG").HasAttribute("disabled"));

        map.Render();
        Assert.Single(JSInterop.Invocations["talewrightMap.render"]);
        Assert.Empty(JSInterop.Invocations["talewrightMap.updateRun"]);
    }

    [Fact]
    public void Without_a_run_there_is_no_route()
    {
        using var model = ComponentStory.Load();

        var map = Show(model);

        Assert.Null(Assert.Single(JSInterop.Invocations["talewrightMap.render"]).Arguments[4]);
        Assert.Null(map.FindComponent<MapRoute>().Instance.Run);
    }

    [Fact]
    public void A_drawing_failure_is_reported_and_blocks_export()
    {
        JSInterop.SetupVoid("talewrightMap.render", _ => true).SetException(new JSException("no canvas"));
        using var model = ComponentStory.Start();

        var map = Show(model);

        Assert.Equal("The map could not be drawn: no canvas", map.Find("p[role=alert]").TextContent);
        Assert.True(Button(map, "Export SVG").HasAttribute("disabled"));
        Assert.True(Button(map, "Export PNG").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Shows_the_status_the_map_reports()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);
        Assert.Equal("Laying out the map…", map.Find("p.map-status").TextContent);

        await map.InvokeAsync(() => map.Instance.SetMapStatus("2 scenes laid out."));

        Assert.Equal("2 scenes laid out.", map.Find("p.map-status").TextContent);
    }

    [Fact]
    public void Fit_asks_the_map_to_fit()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        Button(map, "Fit map").Click();

        Assert.Single(Assert.Single(JSInterop.Invocations["talewrightMap.fit"]).Arguments);
    }

    [Fact]
    public void Focusing_a_scene_selects_it_and_previews_it_with_the_run_state()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        SceneButton(map, "cave").Focus();

        Assert.Equal("cave", Assert.Single(JSInterop.Invocations["talewrightMap.setFocused"]).Arguments[1]);
        Assert.Equal("cave", map.FindComponent<MapSceneDetails>().Instance.SelectedNodeId);
        Assert.Equal("cave", map.FindComponent<MapSceneNavigator>().Instance.SelectedNodeId);
        var preview = map.Find("section[aria-label='Scene preview']");
        Assert.Equal("Scene preview: Cave", preview.QuerySelector("h3")!.TextContent);
        Assert.Contains("using the current playtest state.", preview.TextContent);
        Assert.Contains("The cave.", preview.TextContent);
        Assert.Equal(["1. Return", "2. Leave"], preview.QuerySelectorAll("ul > li").Select(item => item.TextContent));

        SceneButton(map, "cave").Blur();

        Assert.Equal("cave", Assert.Single(JSInterop.Invocations["talewrightMap.clearFocused"]).Arguments[1]);
        // Leaving the navigator keeps the selection so its details stay readable.
        Assert.NotNull(map.Find("section[aria-label='Scene preview']"));
    }

    [Fact]
    public void Previews_use_setup_values_without_a_run()
    {
        using var model = ComponentStory.Load();
        var map = Show(model);

        SceneButton(map, "hall").Focus();

        var preview = map.Find("section[aria-label='Scene preview']");
        Assert.Equal("Scene preview: Great Hall", preview.QuerySelector("h3")!.TextContent);
        Assert.Contains("using scene setup values.", preview.TextContent);
        Assert.Contains("Count 0.", preview.TextContent);
        Assert.Equal(["1. Explore", "2. Wait"], preview.QuerySelectorAll("ul > li").Select(item => item.TextContent));
    }

    [Fact]
    public void Preview_lists_shown_but_unavailable_choices()
    {
        var files = ComponentStory.Files();
        files["main.tale"] = """
            === hall
            @title Great Hall
            * [Restricted] (requires false, shown "Not ready") -> cave
            * [Wait] -> hall
            === cave
            * [Leave] -> END
            """;
        using var model = ComponentStory.Load(files);
        var map = Show(model);

        SceneButton(map, "hall").Focus();

        var preview = map.Find("section[aria-label='Scene preview']");
        Assert.Equal(["1. Not ready (unavailable)", "2. Wait"],
            preview.QuerySelectorAll("ul > li").Select(item => item.TextContent));
    }

    [Fact]
    public async Task Selecting_the_end_or_an_unknown_scene_shows_no_preview()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);
        await map.InvokeAsync(() => map.Instance.SelectScene("cave"));
        Assert.NotNull(map.Find("section[aria-label='Scene preview']"));

        await map.InvokeAsync(() => map.Instance.SelectScene("END"));

        Assert.Empty(map.FindAll("section[aria-label='Scene preview']"));
        Assert.Equal("END", map.FindComponent<MapSceneDetails>().Instance.SelectedNodeId);
        await Assert.ThrowsAsync<ArgumentException>(() => map.InvokeAsync(() => map.Instance.SelectScene("nowhere")));
    }

    [Fact]
    public async Task Scene_details_highlight_lines_and_jump_to_their_targets()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);
        SceneButton(map, "hall").Focus();
        IElement Explore() => map.FindAll(".map-details button.map-item")
            .Single(button => button.TextContent.EndsWith("to cave, main.tale line 5", StringComparison.Ordinal));

        Explore().MouseEnter();
        var line = Assert.Single(JSInterop.Invocations["talewrightMap.highlight"]).Arguments[1];
        Assert.StartsWith("d", Assert.IsType<string>(line));

        Explore().Click();

        // The detail's target is focused through the scene navigator, which keeps keyboard order intact.
        var focused = JSInterop.VerifyFocusAsyncInvoke().Arguments[0];
        var navigator = map.FindComponent<MapSceneNavigator>();
        await map.InvokeAsync(() => navigator.Instance.FocusSceneAsync("cave"));
        await map.InvokeAsync(() => navigator.Instance.FocusSceneAsync("hall"));
        var focuses = JSInterop.VerifyFocusAsyncInvoke(3);
        Assert.Equal(focused, focuses[1].Arguments[0]);
        Assert.NotEqual(focused, focuses[2].Arguments[0]);
    }

    [Fact]
    public void Map_call_failures_are_reported()
    {
        JSInterop.SetupVoid("talewrightMap.setFocused", _ => true).SetException(new JSException("lost"));
        JSInterop.SetupVoid("talewrightMap.fit", _ => true).SetException(new JSException("too small"));
        using var model = ComponentStory.Start();
        var map = Show(model);

        SceneButton(map, "cave").Focus();
        Assert.Equal("The focused map scene could not be updated: lost", map.Find("p[role=alert]").TextContent);

        Button(map, "Fit map").Click();
        Assert.Equal("The map could not be fitted: too small", map.Find("p[role=alert]").TextContent);
    }

    [Fact]
    public void A_new_inspection_updates_the_route_without_redrawing()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        ComponentStory.Choose(model, "Explore");
        map.Render(parameters => Parameters(parameters, model));

        var update = Assert.Single(JSInterop.Invocations["talewrightMap.updateRun"]);
        Assert.Equal(map.FindComponent<MapRoute>().Instance.Run, update.Arguments[1]);
        Assert.Single(JSInterop.Invocations["talewrightMap.render"]);
    }

    [Fact]
    public void A_route_update_failure_is_reported()
    {
        JSInterop.SetupVoid("talewrightMap.updateRun", _ => true).SetException(new JSException("stale"));
        using var model = ComponentStory.Start();
        var map = Show(model);

        ComponentStory.Choose(model, "Explore");
        map.Render(parameters => Parameters(parameters, model));

        Assert.Equal("The map route could not be updated: stale", map.Find("p[role=alert]").TextContent);
    }

    [Fact]
    public void A_new_story_redraws_and_drops_a_selection_it_no_longer_has()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);
        SceneButton(map, "cave").Focus();
        using var rooms = ComponentStory.Load(ComponentStory.Rooms(2));

        map.Render(parameters => Parameters(parameters, rooms));

        Assert.Equal(2, JSInterop.Invocations["talewrightMap.render"].Count);
        Assert.Null(map.FindComponent<MapSceneDetails>().Instance.SelectedNodeId);
        Assert.Empty(map.FindAll("section[aria-label='Scene preview']"));
        Assert.Equal(["room001", "room002"],
            Assert.IsType<MapView>(JSInterop.Invocations["talewrightMap.render"][1].Arguments[3]).Nodes
                .Where(node => node.Scene is not null).Select(node => node.Scene));
    }

    [Fact]
    public void A_recompiled_story_keeps_a_selection_it_still_has()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);
        SceneButton(map, "cave").Focus();
        using var recompiled = ComponentStory.Load();

        map.Render(parameters => Parameters(parameters, recompiled));

        Assert.Equal("cave", Assert.IsType<string>(JSInterop.Invocations["talewrightMap.render"][1].Arguments[5]));
        Assert.Equal("Scene preview: Cave", map.Find("section[aria-label='Scene preview'] h3").TextContent);
        Assert.Contains("using scene setup values.", map.Find("section[aria-label='Scene preview']").TextContent);
    }

    [Fact]
    public void Export_reports_a_missing_save_dialog()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        Button(map, "Export PNG").Click();

        Assert.Equal("Map export failed: The map export dialog is not attached to the debugger window.",
            map.Find(".map-export-controls p[role=status]").TextContent);
        Assert.Empty(JSInterop.Invocations["talewrightMap.exportMap"]);
        Assert.False(Button(map, "Export PNG").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Rejects_export_data_without_a_matching_export()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            map.InvokeAsync(() => map.Instance.SaveMapExport("svg", null!)));

        Assert.Equal("There is no matching map export in progress.", error.Message);
    }

    [Fact]
    public void Png_size_checks_use_the_export_limits()
    {
        using var model = ComponentStory.Start();
        var map = Show(model);

        Assert.Equal(MapExportLimits.CheckPngSize(400, 300), map.Instance.CheckPngExportSize(400, 300));
        Assert.Equal(MapExportLimits.CheckPngSize(100_000, 100_000), map.Instance.CheckPngExportSize(100_000, 100_000));
    }

    private IRenderedComponent<MapTab> Show(WorkbenchModel model) =>
        Render<MapTab>(parameters => Parameters(parameters, model));

    private static void Parameters(ComponentParameterCollectionBuilder<MapTab> parameters, WorkbenchModel model) =>
        parameters
            .Add(component => component.Story, model.Story!)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.PreviewState, model.PreviewState);

    private static IElement SceneButton(IRenderedComponent<MapTab> map, string scene)
    {
        var name = map.FindComponent<MapSceneNavigator>().Instance.View.Nodes.Single(node => node.Scene == scene && !node.Stub).Name;
        return map.FindAll(".map-scenes button").Single(button => button.TextContent == name);
    }

    private static IElement Button(IRenderedComponent<MapTab> map, string text) =>
        map.FindAll("button").Single(button => button.TextContent == text);
}
