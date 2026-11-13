using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Talewright.Core.Identity;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class WorkbenchTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    private readonly BunitJSModuleInterop _settingsModule;
    private readonly BunitJSModuleInterop _layoutModule;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _root.Delete(recursive: true);
    }

    private static readonly TimeSpan Reload = TimeSpan.FromSeconds(10);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("talewright-workbench-");

    public WorkbenchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<MapExportDialog>();
        JSInterop.SetupModule("./_content/Talewright.Player/Preferences/PlayerPreferences.js");
        _settingsModule = JSInterop.SetupModule("./_content/Talewright.Player/Components/ReaderSettingsDialog.razor.js");
        _layoutModule = JSInterop.SetupModule("./debugger/workspace.js");
        _layoutModule.Setup<DebuggerWorkspace.WorkspaceLayout>("loadLayout")
            .SetResult(new(70, 68, false, false, 1400, true, true));
        Services.AddScoped<Talewright.Player.Preferences.PlayerPreferencesStore>();
        ComponentStory.WriteTo(_root.FullName);
    }

    [Fact]
    public async Task Loads_the_story_from_disk_ready_to_set_up()
    {
        var workbench = Show();

        Assert.NotNull(workbench.Find(".debugger-workspace"));
        Assert.Equal("Debugger components", workbench.FindComponent<SetupTool>().Instance.Story.Title);
        Assert.Equal(["Scene", "Map"], workbench.FindAll("#play-tab, #map-tab").Select(tab => tab.TextContent.Trim()));
        Assert.Empty(workbench.FindComponents<ScenarioTransfer>());
        Assert.Empty(workbench.FindAll("section[aria-label='Synthetic playtest']"));
        Assert.Empty(workbench.FindAll("[role=alert]"));

        await Button(workbench, "Files").ClickAsync(new MouseEventArgs());
        await Button(workbench, "Portable author scenarios").ClickAsync(new MouseEventArgs());
        Assert.NotNull(workbench.FindComponent<ScenarioTransfer>());
        Assert.Equal("Portable author scenarios", workbench.Find("[role=dialog]").GetAttribute("aria-label"));
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
        Assert.Same(model.Session, workbench.FindComponent<StateEditor>().Instance.Session);
        Assert.Same(model.Session, workbench.FindComponent<InitializationEvents>().Instance.Session);
        Assert.Same(model.Session, workbench.FindComponent<SceneViewer>().Instance.Session);
        Assert.Same(model.Inspection, workbench.FindComponent<BranchesTool>().Instance.Inspection);
        Assert.Same(model.Inspection, workbench.FindComponent<EventLog>().Instance.Inspection);
        Assert.Contains("Scene hall source range", workbench.FindComponent<SourceTool>().Markup);
        Assert.Equal("hall Great Hall", workbench.Find("h3.debugger-scene-heading").TextContent);
    }

    [Fact]
    public async Task Scene_connections_expansion_is_saved_with_the_workspace_layout()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        var details = workbench.Find(".scene-connections details");
        Assert.True(details.HasAttribute("open"));
        await details.QuerySelector("summary")!.ClickAsync(new MouseEventArgs());

        Assert.False(workbench.Find(".scene-connections details").HasAttribute("open"));
        var savedLayout = Assert.IsType<DebuggerWorkspace.WorkspaceLayout>(
            _layoutModule.Invocations["saveLayout"].Last().Arguments[0]);
        Assert.False(savedLayout.ConnectionsExpanded);
    }

    [Fact]
    public async Task Inspector_navigation_shows_source_and_sets_a_target_without_advancing_the_run()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var current = model.Session!.Current;
        var eventCount = model.Inspection!.Events.Count;

        var outgoing = workbench.FindAll("[aria-label='Outgoing connections'] li")
            .Single(row => row.TextContent.Contains("Choice c1 “Explore”", StringComparison.Ordinal));
        await outgoing.QuerySelector("button")!.ClickAsync(new MouseEventArgs());

        Assert.Equal(new SceneId("cave"), workbench.FindComponent<InspectorPane>().Instance.Selection!.Scene);
        Assert.Same(current, model.Session.Current);
        Assert.Equal(eventCount, model.Inspection.Events.Count);

        await Button(workbench, "Back to current scene").ClickAsync(new MouseEventArgs());
        Assert.Equal(new SceneId("hall"), workbench.FindComponent<InspectorPane>().Instance.Selection!.Scene);
        Assert.Same(current, model.Session.Current);

        outgoing = workbench.FindAll("[aria-label='Outgoing connections'] li")
            .Single(row => row.TextContent.Contains("Choice c1 “Explore”", StringComparison.Ordinal));
        await outgoing.QuerySelector("button")!.ClickAsync(new MouseEventArgs());
        await Button(workbench, "Show source").ClickAsync(new MouseEventArgs());
        Assert.False(workbench.Find("#tool-source-panel").HasAttribute("hidden"));
        Assert.Contains("Scene cave source range", workbench.FindComponent<SourceTool>().Markup);
        Assert.Same(current, model.Session.Current);
        Assert.Equal(eventCount, model.Inspection.Events.Count);

        ComponentStory.Choose(model, "Wait");
        workbench.Render();
        Assert.Equal(new SceneId("cave"), workbench.FindComponent<SourceTool>().FindComponent<SourceView>().Instance.Source!.Scene);
        await workbench.FindAll("button").Single(button => button.TextContent.Trim() == "Follow current scene")
            .ClickAsync(new MouseEventArgs());
        Assert.Equal(new SceneId("hall"), workbench.FindComponent<SourceTool>().FindComponent<SourceView>().Instance.Source!.Scene);

        outgoing = workbench.FindAll("[aria-label='Outgoing connections'] li")
            .Single(row => row.TextContent.Contains("Choice c1 “Explore”", StringComparison.Ordinal));
        await outgoing.QuerySelector("button")!.ClickAsync(new MouseEventArgs());
        await Button(workbench, "Use as target").ClickAsync(new MouseEventArgs());
        Assert.Equal("cave", model.Target);
        Assert.False(workbench.Find("#tool-setup-panel").HasAttribute("hidden"));
        var beforeStart = model.Session!.Current;

        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        Assert.Equal(new SceneId("cave"), model.Session!.Current.Scene);
        Assert.NotSame(beforeStart, model.Session.Current);
    }

    [Fact]
    public async Task Map_connection_selection_updates_and_centers_the_shared_inspector_selection()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var current = model.Session!.Current;

        await Button(workbench, "Show on map").ClickAsync(new MouseEventArgs());
        Assert.False(workbench.Find("#map-panel").HasAttribute("hidden"));
        Assert.Equal(new SceneId("hall"), workbench.FindComponent<InspectorPane>().Instance.Selection!.Scene);

        var outgoing = workbench.FindAll("[aria-label='Outgoing connections'] li")
            .Single(row => row.TextContent.Contains("Choice c1 “Explore”", StringComparison.Ordinal));
        await outgoing.QuerySelector("button")!.ClickAsync(new MouseEventArgs());

        Assert.Equal("cave", workbench.FindComponent<MapInspectorPane>().Instance.SelectedNodeId);
        Assert.Equal("cave", workbench.FindComponent<MapTab>().Instance.CurrentSelection);
        Assert.Equal(new SceneId("cave"), workbench.FindComponent<InspectorPane>().Instance.Selection!.Scene);
        Assert.Same(current, model.Session.Current);
        Assert.Equal("cave", JSInterop.Invocations["talewrightMap.setFocused"].Last().Arguments[1]);
    }

    [Fact]
    public async Task Branch_decision_inspects_a_disabled_choice_and_opens_its_explanation()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var session = model.Session!;
        var inspection = model.Inspection!;
        var current = session.Current;
        var eventCount = inspection.Events.Count;
        Assert.DoesNotContain(session.Current.Choices, choice => choice.Text == "Prepared");

        await workbench.Find("#tool-branches-tab").ClickAsync(new MouseEventArgs());
        var decision = workbench.FindAll("#tool-branches-panel details")
            .Single(details => details.QuerySelector("summary")!.TextContent.Contains("Choice hall step:4:1", StringComparison.Ordinal));
        var inspectChoice = decision.QuerySelector("button")!;
        Assert.Equal("button", inspectChoice.LocalName);
        Assert.Equal("Inspect choice", inspectChoice.TextContent.Trim());
        await inspectChoice.ClickAsync(new MouseEventArgs());

        var selection = workbench.FindComponent<InspectorPane>().Instance.Selection!;
        Assert.Equal(new SceneId("hall"), selection.Scene);
        Assert.Equal("c0", selection.ChoiceKey);
        Assert.Contains("Choice c0", workbench.FindComponent<ChoiceInspector>().Markup);
        Assert.Same(current, session.Current);
        Assert.Equal(eventCount, inspection.Events.Count);

        await workbench.FindAll("button").Single(button => button.TextContent.Trim() == "Show branch explanation")
            .ClickAsync(new MouseEventArgs());
        Assert.False(workbench.Find("#tool-branches-panel").HasAttribute("hidden"));
        var expanded = Assert.Single(workbench.FindAll("#tool-branches-panel details[open]"));
        Assert.Contains("Choice hall step:4:1", expanded.QuerySelector("summary")!.TextContent);
    }

    [Fact]
    public async Task Branch_decision_inspects_a_shown_unavailable_choice_without_advancing_or_changing_events()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "main.tale"), DebuggerDecisionStory.Tale);
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var session = model.Session!;
        var inspection = model.Inspection!;
        var current = session.Current;
        var eventCount = inspection.Events.Count;

        await workbench.Find("#tool-branches-tab").ClickAsync(new MouseEventArgs());
        var decision = workbench.FindAll("#tool-branches-panel details")
            .Single(details => details.QuerySelector("summary")!.TextContent.Contains("Choice hall step:9:1", StringComparison.Ordinal));
        Assert.Contains("Shown, unavailable", decision.QuerySelector("summary")!.TextContent);
        await decision.QuerySelector("button")!.ClickAsync(new MouseEventArgs());

        var inspector = workbench.FindComponent<ChoiceInspector>();
        Assert.Contains("Shown, unavailable", inspector.Markup);
        Assert.Contains("Need 0 keys", inspector.Markup);
        Assert.Contains("flag:ready", inspector.Markup);
        Assert.Same(current, session.Current);
        Assert.Equal(eventCount, inspection.Events.Count);
    }

    [Fact]
    public async Task Pane_visibility_toggles_restore_and_persist_the_layout()
    {
        var workbench = Show();
        Assert.Equal(2, workbench.FindAll("[role=separator]").Count);

        await Button(workbench, "Inspector").ClickAsync(new MouseEventArgs());
        Assert.Single(workbench.FindAll("[role=separator]"));
        await Button(workbench, "Inspector").ClickAsync(new MouseEventArgs());
        Assert.Equal(2, workbench.FindAll("[role=separator]").Count);

        await Button(workbench, "Tools").ClickAsync(new MouseEventArgs());
        Assert.Single(workbench.FindAll("[role=separator]"));
        var savedLayout = Assert.IsType<DebuggerWorkspace.WorkspaceLayout>(
            _layoutModule.Invocations["saveLayout"].Last().Arguments[0]);
        Assert.False(savedLayout.InspectorCollapsed);
        Assert.True(savedLayout.ToolsCollapsed);

        await Button(workbench, "Tools").ClickAsync(new MouseEventArgs());
        Assert.Equal(2, workbench.FindAll("[role=separator]").Count);
    }

    [Fact]
    public async Task Source_status_opens_problems_and_restores_collapsed_tools()
    {
        var workbench = Show();
        await Button(workbench, "Tools").ClickAsync(new MouseEventArgs());
        Assert.Empty(workbench.FindComponents<ToolTabs>());

        Assert.Equal("Source up to date: Open Problems", workbench.Find(".source-status").GetAttribute("aria-label"));
        await workbench.Find(".source-status").ClickAsync(new MouseEventArgs());

        Assert.Equal("true", Button(workbench, "Tools").GetAttribute("aria-pressed"));
        Assert.Equal("true", workbench.Find("#tool-problems-tab").GetAttribute("aria-selected"));
        Assert.False(workbench.Find("#tool-problems-panel").HasAttribute("hidden"));
        var savedLayout = Assert.IsType<DebuggerWorkspace.WorkspaceLayout>(
            _layoutModule.Invocations["saveLayout"].Last().Arguments[0]);
        Assert.False(savedLayout.ToolsCollapsed);

        await workbench.Find("#tool-setup-tab").ClickAsync(new MouseEventArgs());
        var saveCount = _layoutModule.Invocations["saveLayout"].Count;
        await workbench.Find(".source-status").ClickAsync(new MouseEventArgs());
        Assert.Equal("true", workbench.Find("#tool-problems-tab").GetAttribute("aria-selected"));
        Assert.False(workbench.Find("#tool-problems-panel").HasAttribute("hidden"));
        Assert.Equal(saveCount, _layoutModule.Invocations["saveLayout"].Count);
    }

    [Fact]
    public async Task Switching_tool_tabs_preserves_the_mounted_tool_and_its_pending_edits()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        await workbench.Find("#tool-state-tab").ClickAsync(new MouseEventArgs());
        var tool = workbench.FindComponent<StateEditor>().Instance;
        await workbench.Find("input[aria-label='Edit var:count']").InputAsync(new ChangeEventArgs { Value = "42" });

        await workbench.Find("#tool-source-tab").ClickAsync(new MouseEventArgs());
        Assert.True(workbench.Find("#tool-state-panel").HasAttribute("hidden"));
        Assert.Same(tool, workbench.FindComponent<StateEditor>().Instance);

        await workbench.Find("#tool-state-tab").ClickAsync(new MouseEventArgs());
        Assert.False(workbench.Find("#tool-state-panel").HasAttribute("hidden"));
        Assert.Same(tool, workbench.FindComponent<StateEditor>().Instance);
        Assert.Equal("42", workbench.Find("input[aria-label='Edit var:count']").GetAttribute("value"));
        Assert.Contains("1 edit pending", workbench.Find("#tool-state-panel").TextContent);
    }

    [Fact]
    public async Task Initializes_reader_settings_before_use_without_a_mounted_reader()
    {
        var workbench = Show();
        workbench.WaitForAssertion(() => Assert.False(Button(workbench, "Reader settings").HasAttribute("disabled")));
        Assert.Empty(workbench.FindAll("[aria-label='Current passage']"));

        await Button(workbench, "Reader settings").ClickAsync(new MouseEventArgs());
        var dialog = workbench.FindComponent<Talewright.Player.Components.ReaderSettingsDialog>();
        await dialog.Find("#tw-theme").ChangeAsync(new ChangeEventArgs { Value = "Dark" });

        Assert.Single(_settingsModule.Invocations["open"]);
        Assert.Equal("Dark", dialog.Find("#tw-theme").GetAttribute("value"));
    }

    [Fact]
    public async Task Playtest_actions_rerender_the_workbench()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        await Choice(workbench, "Explore").ClickAsync(new MouseEventArgs());
        Assert.Equal("cave Cave", Passage(workbench));
        Assert.Contains("Scene cave source range", workbench.FindComponent<SourceTool>().Markup);
        Assert.Contains(workbench.FindComponent<StateEditor>().FindAll("tr"), row => row.TextContent == "cave1");

        await Button(workbench, "Back command").ClickAsync(new MouseEventArgs());
        Assert.Equal("hall Great Hall", Passage(workbench));

        await Button(workbench, "Forward command").ClickAsync(new MouseEventArgs());
        Assert.Equal("cave Cave", Passage(workbench));

        await workbench.Find("input[aria-label='Edit var:count']").InputAsync(new ChangeEventArgs { Value = "5" });
        await Button(workbench, "Apply edits").ClickAsync(new MouseEventArgs());
        Assert.Equal("5", workbench.Find("input[aria-label='Edit var:count']").GetAttribute("value"));

        await Choice(workbench, "Leave").ClickAsync(new MouseEventArgs());
        Assert.True(Button(workbench, "Apply edits").HasAttribute("disabled"));
        await Button(workbench, "Play again").ClickAsync(new MouseEventArgs());
        Assert.Equal("hall Great Hall", Passage(workbench));
    }

    [Fact]
    public async Task Setup_failures_are_shown_as_alerts()
    {
        var workbench = Show();

        await workbench.InvokeAsync(() =>
            workbench.Find("input[aria-label='Target scene ID']").ChangeAsync(new ChangeEventArgs { Value = "nowhere" }));
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
        Assert.Single(workbench.FindAll("aside.inspector-pane"));
        Assert.Empty(workbench.FindAll("#map-panel .map-side"));
        Assert.Contains("Scene details", workbench.FindComponent<MapInspectorPane>().Markup);

        await Tab(workbench, "play-tab").ClickAsync(new MouseEventArgs());
        AssertSelected(workbench, map: false);
        Assert.Same(map, workbench.FindComponent<MapTab>().Instance);
        Assert.True(workbench.Find("#map-panel").HasAttribute("hidden"));
        Assert.Empty(workbench.FindComponents<MapInspectorPane>());
        Assert.Single(JSInterop.Invocations["talewrightMap.render"]);
    }

    [Fact]
    public async Task Selecting_a_map_scene_inspects_it_until_returning_to_the_scene_tab()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        await Tab(workbench, "map-tab").ClickAsync(new MouseEventArgs());

        var map = workbench.FindComponent<MapTab>();
        await map.InvokeAsync(() => map.Instance.SelectScene("cave"));
        Assert.Equal("cave", workbench.FindComponent<MapInspectorPane>().Instance.SelectedNodeId);
        Assert.Contains("Scene preview: Cave", workbench.FindComponent<MapInspectorPane>().Markup);

        await Tab(workbench, "play-tab").ClickAsync(new MouseEventArgs());
        Assert.Equal("hall", workbench.FindComponent<InspectorPane>().Instance.Selection!.Scene.Value);
        Assert.Empty(workbench.FindComponents<MapInspectorPane>());
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

        var status = workbench.Find(".debugger-status-line p[role=status]");
        Assert.Equal("Source restarted. Compatible setup retained and revalidated; select Start selected scene for a fresh run. "
            + "Requested scene 'missing' was not found. The configured start scene is selected instead.", status.TextContent);
        Assert.Single(workbench.FindAll(".debugger-status-line p[role=status], .debugger-status-line p[role=alert]"));
        Assert.Equal("hall", workbench.Find("input[aria-label='Target scene ID']").GetAttribute("value"));
    }

    [Fact]
    public async Task Compiler_errors_keep_the_last_good_story_and_report_diagnostics()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var lastGoodStory = model.Story;
        var lastGoodSession = model.Session;

        File.WriteAllText(Path.Combine(_root.FullName, "main.tale"), ComponentStory.Tale.Replace("-> cave", "-> nowhere"));
        await Button(workbench, "Files").ClickAsync(new MouseEventArgs());
        await Button(workbench, "Rescan / retry watcher").ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() => Assert.Contains(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Source not adopted.", StringComparison.Ordinal)), Reload);
        Assert.Equal("Story has compiler errors. Fix and save, or select Rescan / retry watcher. Any previous run is unchanged.",
            workbench.Find(".debugger-status-line p[role=alert]").TextContent);
        Assert.Single(workbench.FindAll(".debugger-status-line p[role=status], .debugger-status-line p[role=alert]"));
        Assert.Contains("nowhere", workbench.FindComponent<Talewright.Player.Components.DiagnosticList>().Markup);
        Assert.Equal("hall Great Hall", Passage(workbench));

        var status = workbench.Find(".source-status");
        Assert.Equal("Source not adopted: Open Problems", status.GetAttribute("aria-label"));
        await status.ClickAsync(new MouseEventArgs());

        Assert.Equal("true", workbench.Find("#tool-problems-tab").GetAttribute("aria-selected"));
        Assert.Contains("nowhere", workbench.FindComponent<Talewright.Player.Components.DiagnosticList>().Markup);
        Assert.Same(lastGoodStory, model.Story);
        Assert.Same(lastGoodSession, model.Session);
        Assert.Equal("hall Great Hall", Passage(workbench));
    }

    [Fact]
    public async Task Restart_required_status_restarts_from_source_and_adopts_it()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        Assert.NotNull(model.Session);

        File.WriteAllText(Path.Combine(_root.FullName, Talewright.Talemark.Compilation.StorySource.ManifestFileName),
            ComponentStory.Yaml.Replace("title: Debugger components", "title: Renamed story"));
        workbench.WaitForAssertion(() =>
        {
            Assert.StartsWith("Source compiled, but explicit Restart from source is required.",
                workbench.Find(".debugger-status-line p[role=alert]").TextContent);
            Assert.Equal("Restart required", workbench.Find(".source-status").TextContent);
        }, Reload);

        var status = workbench.Find(".source-status");
        Assert.Equal("Restart required: Restart from source", status.GetAttribute("aria-label"));
        await status.ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() =>
        {
            Assert.Equal("Renamed story", workbench.FindComponent<SetupTool>().Instance.Story.Title);
            Assert.DoesNotContain(workbench.FindAll("p[role=status]"),
                message => message.TextContent.StartsWith("Restart required.", StringComparison.Ordinal));
        }, Reload);
        Assert.Null(model.Session);
        Assert.False(model.RestartRequired);
    }

    [Fact]
    public async Task Restart_required_with_compiler_errors_opens_problems_without_replacing_the_last_good_run()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());
        var model = Services.GetRequiredService<WorkbenchModel>();
        var lastGoodStory = model.Story;
        var lastGoodSession = model.Session;

        File.WriteAllText(Path.Combine(_root.FullName, Talewright.Talemark.Compilation.StorySource.ManifestFileName),
            ComponentStory.Yaml.Replace("title: Debugger components", "title: Renamed story"));
        File.WriteAllText(Path.Combine(_root.FullName, "main.tale"), ComponentStory.Tale.Replace("-> cave", "-> nowhere"));
        await Button(workbench, "Files").ClickAsync(new MouseEventArgs());
        await Button(workbench, "Rescan / retry watcher").ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() =>
        {
            Assert.True(model.RestartRequired);
            Assert.Equal("Restart required", workbench.Find(".source-status").TextContent);
            Assert.Contains("nowhere", workbench.FindComponent<Talewright.Player.Components.DiagnosticList>().Markup);
        }, Reload);

        var status = workbench.Find(".source-status");
        Assert.Equal("Restart required: Open Problems", status.GetAttribute("aria-label"));
        await status.ClickAsync(new MouseEventArgs());

        Assert.Equal("true", workbench.Find("#tool-problems-tab").GetAttribute("aria-selected"));
        Assert.Contains("nowhere", workbench.FindComponent<Talewright.Player.Components.DiagnosticList>().Markup);
        Assert.Same(lastGoodStory, model.Story);
        Assert.Same(lastGoodSession, model.Session);
        Assert.Equal("hall Great Hall", Passage(workbench));
    }

    [Fact]
    public async Task Yaml_changes_wait_for_an_explicit_restart()
    {
        var workbench = Show();
        await Button(workbench, "Start selected scene").ClickAsync(new MouseEventArgs());

        File.WriteAllText(Path.Combine(_root.FullName, Talewright.Talemark.Compilation.StorySource.ManifestFileName),
            ComponentStory.Yaml.Replace("title: Debugger components", "title: Renamed story"));
        workbench.WaitForAssertion(() =>
        {
            Assert.Equal("Source compiled, but explicit Restart from source is required. The last good run is unchanged.",
                workbench.Find(".debugger-status-line p[role=alert]").TextContent);
            Assert.Single(workbench.FindAll(".debugger-status-line p[role=status], .debugger-status-line p[role=alert]"));
        }, Reload);
        Assert.Equal("Debugger components", workbench.FindComponent<SetupTool>().Instance.Story.Title);
        Assert.NotEmpty(workbench.FindAll("section[aria-label='Synthetic playtest']"));

        await Button(workbench, "Files").ClickAsync(new MouseEventArgs());
        await Button(workbench, "Restart from source").ClickAsync(new MouseEventArgs());

        workbench.WaitForAssertion(() => Assert.Equal("Renamed story", workbench.FindComponent<SetupTool>().Instance.Story.Title), Reload);
        Assert.DoesNotContain(workbench.FindAll("p[role=status]"),
            status => status.TextContent.StartsWith("Restart required.", StringComparison.Ordinal));
        Assert.Empty(workbench.FindAll("section[aria-label='Synthetic playtest']"));
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
        workbench.Find("h3.debugger-scene-heading").TextContent;

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
        var fieldName = tabId == "map-tab" ? "_mapTab" : "_sceneTab";
        var reference = Assert.IsType<ElementReference>(
            typeof(DebuggerToolbar).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(workbench.FindComponent<DebuggerToolbar>().Instance));
        Assert.Equal(reference, JSInterop.VerifyFocusAsyncInvoke(count).Last().Arguments[0]);
    }

    private static IElement Choice(IRenderedComponent<Workbench> workbench, string text) =>
        workbench.FindAll("ol[aria-label='Story choices'] button").Single(button => button.TextContent.Contains(text, StringComparison.Ordinal));

    private static IElement Button(IRenderedComponent<Workbench> workbench, string text) =>
        workbench.FindAll("button").Single(button => button.TextContent == text);
}
