using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class WorkbenchTests : BunitContext
{
    private static readonly TimeSpan Reload = TimeSpan.FromSeconds(10);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("talewright-workbench-");

    public WorkbenchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<MapExportDialog>();
        ComponentStory.WriteTo(_root.FullName);
    }

    [Fact]
    public void Loads_the_story_from_disk_ready_to_set_up()
    {
        var workbench = Show();

        Assert.Equal(_root.FullName, workbench.Find("main > p").TextContent);
        Assert.Equal("Debugger components", workbench.Find("main > h2").TextContent);
        Assert.Equal(["Play", "Map"], workbench.FindAll("[role=tab]").Select(tab => tab.TextContent));
        Assert.Equal("Debugger components", workbench.FindComponent<SetupTool>().Instance.Story.Title);
        Assert.NotNull(workbench.FindComponent<ScenarioTransfer>());
        Assert.Empty(workbench.FindAll("section[aria-label='Synthetic playtest']"));
        Assert.Empty(workbench.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Starting_shows_the_playtest_tools()
    {
        var workbench = Show();

        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        var playtest = workbench.Find("section[aria-label='Synthetic playtest']");
        var model = Services.GetRequiredService<WorkbenchModel>();
        Assert.Equal($"Synthetic history: skipped scenes are unvisited. This does not prove normal reachability. Seed: {model.Session!.Seed}. Rolls: 0.",
            playtest.QuerySelector("p[role=status]")!.TextContent);
        Assert.Same(model.Session, workbench.FindComponent<TimelineTool>().Instance.Session);
        Assert.Same(model.Session, workbench.FindComponent<LiveEditTool>().Instance.Session);
        Assert.Same(model.Session, workbench.FindComponent<InitializationEvents>().Instance.Session);
        Assert.Same(model.Session, workbench.FindComponent<SceneViewer>().Instance.Session);
        Assert.Same(model.Inspection, workbench.FindComponent<InspectionPanel>().Instance.Inspection);
        Assert.Contains("Scene hall source range", workbench.FindComponent<SourceTool>().Markup);
        Assert.Equal("hall Great Hall", workbench.Find("section[aria-label='Current passage'] h3").TextContent);
    }

    [Fact]
    public async Task Playtest_actions_rerender_the_workbench()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        await Choice(workbench, "Explore").ClickAsync(new MouseEventArgs());
        Assert.Equal("cave Cave", Passage(workbench));
        Assert.Contains("Scene cave source range", workbench.FindComponent<SourceTool>().Markup);
        Assert.Contains("cave visits: 1", workbench.FindComponent<StateTable>().Markup);

        await Button(workbench, "Back command").ClickAsync(new MouseEventArgs());
        Assert.Equal("hall Great Hall", Passage(workbench));

        await Button(workbench, "Forward command").ClickAsync(new MouseEventArgs());
        Assert.Equal("cave Cave", Passage(workbench));

        await workbench.Find("input[aria-label='Edit var:count']").ChangeAsync(new ChangeEventArgs { Value = true });
        await workbench.Find("input[aria-label='Edit value var:count']").ChangeAsync(new ChangeEventArgs { Value = "5" });
        await Button(workbench, "Apply live edit batch").ClickAsync(new MouseEventArgs());
        Assert.Contains("var:count (integer) = 5", workbench.FindComponent<StateTable>().Markup);

        await Choice(workbench, "Leave").ClickAsync(new MouseEventArgs());
        Assert.True(Button(workbench, "Apply live edit batch").HasAttribute("disabled"));
        await Button(workbench, "Play again").ClickAsync(new MouseEventArgs());
        Assert.Equal("hall Great Hall", Passage(workbench));
    }

    [Fact]
    public async Task Setup_failures_are_shown_as_alerts()
    {
        var workbench = Show();

        await workbench.Find("input[aria-label='Target scene ID']").ChangeAsync(new ChangeEventArgs { Value = "nowhere" });
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        Assert.Contains("nowhere", workbench.Find("p[role=alert]").TextContent);
        Assert.Empty(workbench.FindAll("section[aria-label='Synthetic playtest']"));
    }

    [Fact]
    public async Task The_map_tab_is_only_mounted_once_opened_and_then_kept()
    {
        var workbench = Show();
        Assert.Empty(workbench.FindComponents<MapTab>());
        Assert.False(Tab(workbench, "map-tab").HasAttribute("aria-controls"));
        AssertSelected(workbench, map: false);

        await Tab(workbench, "map-tab").ClickAsync(new MouseEventArgs());
        AssertSelected(workbench, map: true);
        Assert.Equal("map-panel", Tab(workbench, "map-tab").GetAttribute("aria-controls"));
        var map = workbench.FindComponent<MapTab>().Instance;
        Assert.Single(JSInterop.Invocations["talewrightMap.render"]);

        await Tab(workbench, "play-tab").ClickAsync(new MouseEventArgs());
        AssertSelected(workbench, map: false);
        Assert.Same(map, workbench.FindComponent<MapTab>().Instance);
        Assert.True(workbench.Find("#map-panel").HasAttribute("hidden"));
        Assert.Single(JSInterop.Invocations["talewrightMap.render"]);
    }

    [Fact]
    public async Task Arrow_home_and_end_keys_move_between_tabs_and_focus_them()
    {
        var workbench = Show();

        await Tab(workbench, "play-tab").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        AssertSelected(workbench, map: true);
        AssertFocus(workbench, 1, "map-tab");

        await Tab(workbench, "map-tab").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" });
        AssertSelected(workbench, map: false);
        AssertFocus(workbench, 2, "play-tab");

        await Tab(workbench, "play-tab").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        AssertSelected(workbench, map: true);
        AssertFocus(workbench, 3, "map-tab");
        await Tab(workbench, "map-tab").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        AssertSelected(workbench, map: true);
        AssertFocus(workbench, 4, "map-tab");
        await Tab(workbench, "map-tab").KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        AssertSelected(workbench, map: false);
        AssertFocus(workbench, 5, "play-tab");

        await Tab(workbench, "play-tab").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        await Tab(workbench, "play-tab").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        AssertSelected(workbench, map: false);
        JSInterop.VerifyFocusAsyncInvoke(5);
    }

    [Fact]
    public void A_missing_requested_scene_is_explained()
    {
        var workbench = Show(requestedScene: "missing");

        Assert.Contains(workbench.FindAll("p[role=status]"),
            status => status.TextContent == "Requested scene 'missing' was not found. The configured start scene is selected instead.");
        Assert.Equal("hall", workbench.Find("input[aria-label='Target scene ID']").GetAttribute("value"));
    }

    [Fact]
    public async Task Compiler_errors_keep_the_last_good_story_and_report_diagnostics()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        File.WriteAllText(Path.Combine(_root.FullName, "main.tale"), ComponentStory.Tale.Replace("-> cave", "-> nowhere"));
        await Button(workbench, "Rescan / retry watcher").ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() => Assert.Contains(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Source not adopted.", StringComparison.Ordinal)), Reload);
        Assert.Equal("Story has compiler errors. Fix and save, or select Rescan / retry watcher. Any previous run is unchanged.",
            workbench.Find("p[role=alert]").TextContent);
        Assert.Contains("nowhere", workbench.FindComponent<Talewright.Player.Components.DiagnosticList>().Markup);
        Assert.Equal("hall Great Hall", Passage(workbench));
    }

    [Fact]
    public async Task Yaml_changes_wait_for_an_explicit_restart()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        File.WriteAllText(Path.Combine(_root.FullName, Talewright.Talemark.Compilation.StorySource.ManifestFileName),
            ComponentStory.Yaml.Replace("title: Debugger components", "title: Renamed story"));
        workbench.WaitForAssertion(() => Assert.Contains(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Restart required.", StringComparison.Ordinal)), Reload);
        Assert.Equal("Debugger components", workbench.Find("main > h2").TextContent);
        Assert.NotEmpty(workbench.FindAll("section[aria-label='Synthetic playtest']"));

        await Button(workbench, "Restart from source").ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() => Assert.Equal("Renamed story", workbench.Find("main > h2").TextContent), Reload);
        Assert.DoesNotContain(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Restart required.", StringComparison.Ordinal));
        Assert.Empty(workbench.FindAll("section[aria-label='Synthetic playtest']"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _root.Delete(recursive: true);
    }

    private IRenderedComponent<Workbench> Show(string? requestedScene = null)
    {
        Services.AddSingleton(new WorkbenchModel(_root.FullName, requestedScene));
        var workbench = Render<Workbench>();
        // The story title renders before the initial restart finishes; wait for the restart itself.
        workbench.WaitForAssertion(() => Assert.Contains(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Source restarted.", StringComparison.Ordinal)), Reload);
        return workbench;
    }

    private static string Passage(IRenderedComponent<Workbench> workbench) =>
        workbench.Find("section[aria-label='Current passage'] h3").TextContent;

    private static IElement Tab(IRenderedComponent<Workbench> workbench, string id) => workbench.Find($"#{id}");

    private static void AssertSelected(IRenderedComponent<Workbench> workbench, bool map)
    {
        Assert.Equal(map ? "false" : "true", Tab(workbench, "play-tab").GetAttribute("aria-selected"));
        Assert.Equal(map ? "true" : "false", Tab(workbench, "map-tab").GetAttribute("aria-selected"));
        Assert.Equal(map ? "-1" : "0", Tab(workbench, "play-tab").GetAttribute("tabindex"));
        Assert.Equal(map ? "0" : "-1", Tab(workbench, "map-tab").GetAttribute("tabindex"));
        Assert.Equal(map, workbench.Find("#play-panel").HasAttribute("hidden"));
    }

    private void AssertFocus(IRenderedComponent<Workbench> workbench, int count, string tabId)
    {
        var fieldName = tabId == "map-tab" ? "_mapTab" : "_playTab";
        var reference = Assert.IsType<ElementReference>(
            typeof(Workbench).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(workbench.Instance));
        Assert.Equal(reference, JSInterop.VerifyFocusAsyncInvoke(count).Last().Arguments[0]);
    }

    private static IElement Choice(IRenderedComponent<Workbench> workbench, string text) =>
        workbench.FindAll("ol[aria-label='Story choices'] button").Single(button => button.TextContent.Contains(text, StringComparison.Ordinal));

    private static IElement Button(IRenderedComponent<Workbench> workbench, string text) =>
        workbench.FindAll("button").Single(button => button.TextContent == text);
}
