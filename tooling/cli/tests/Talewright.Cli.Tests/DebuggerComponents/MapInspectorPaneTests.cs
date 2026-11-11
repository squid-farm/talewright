using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Talewright.Debugger;
using Talewright.Player.Components;
using Talewright.Player.Preferences;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapInspectorPaneTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public MapInspectorPaneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Talewright.Player/Preferences/PlayerPreferences.js");
        Services.AddScoped<PlayerPreferencesStore>();
    }

    [Fact]
    public void Selected_scene_details_preview_and_route_render_in_the_inspector()
    {
        using var model = ComponentStory.Start();
        var view = MapView.Build(model.Story!);
        var run = MapRunView.Build(model.Story!, view, model.Session!);
        var inspector = Show(model, view, run, "cave");

        var preview = inspector.Find("section[aria-label='Scene preview']");
        Assert.Equal("Scene preview: Cave", preview.QuerySelector("h3")!.TextContent);
        Assert.Contains("using the current playtest state.", preview.TextContent);
        Assert.Contains("The cave.", preview.TextContent);
        Assert.Equal(["1. Return", "2. Leave"], preview.QuerySelectorAll("ol[aria-label='Story choices'] button")
            .Select(button => Text(button.TextContent)));
        Assert.All(preview.QuerySelectorAll("ol[aria-label='Story choices'] button"),
            button => Assert.True(button.HasAttribute("disabled")));
        Assert.False(inspector.FindComponent<StoryStage>().Instance.ShowCharacterSheet);
        Assert.Contains("Diverts:", inspector.Markup);
        Assert.Contains("Current run route", inspector.Markup);
        Assert.Contains("Started at hall", inspector.Markup);
    }

    [Fact]
    public void Preview_uses_setup_values_when_there_is_no_playtest()
    {
        using var model = ComponentStory.Load();
        var view = MapView.Build(model.Story!);
        var inspector = Show(model, view, null, "hall");

        var preview = inspector.Find("section[aria-label='Scene preview']");
        Assert.Equal("Scene preview: Great Hall", preview.QuerySelector("h3")!.TextContent);
        Assert.Contains("using scene setup values.", preview.TextContent);
        Assert.Contains("Count 0.", preview.TextContent);
        Assert.Equal(["1. Explore", "2. Wait"], preview.QuerySelectorAll("ol[aria-label='Story choices'] button")
            .Select(button => Text(button.TextContent)));
    }

    [Fact]
    public void Preview_shows_unavailable_shown_choices_as_disabled()
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
        var view = MapView.Build(model.Story!);
        var inspector = Show(model, view, null, "hall");

        var choices = inspector.Find("section[aria-label='Scene preview']")
            .QuerySelectorAll("ol[aria-label='Story choices'] button");
        Assert.Equal(["1. Not ready (Unavailable)", "2. Wait"], choices.Select(button => Text(button.TextContent)));
        Assert.All(choices, button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public void End_selection_has_details_but_no_scene_preview()
    {
        using var model = ComponentStory.Start();
        var view = MapView.Build(model.Story!);
        var inspector = Show(model, view, null, "END");

        Assert.Empty(inspector.FindAll("section[aria-label='Scene preview']"));
        Assert.Contains("END", inspector.Find("section[aria-label='Scene details']").TextContent);
        Assert.Contains("Current run route", inspector.Markup);
    }

    private IRenderedComponent<MapInspectorPane> Show(
        WorkbenchModel model,
        MapView view,
        MapRunView? run,
        string selected) =>
        Render<MapInspectorPane>(parameters => parameters
            .Add(component => component.Story, model.Story!)
            .Add(component => component.View, view)
            .Add(component => component.SelectedNodeId, selected)
            .Add(component => component.PreviewState, model.PreviewState)
            .Add(component => component.Session, model.Session)
            .Add(component => component.Run, run));

    private static string Text(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
