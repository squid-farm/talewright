using Bunit;
using Talewright.Debugger;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class StatusLineTests : BunitContext
{
    [Fact]
    public void Clean_restart_includes_a_missing_scene_notice_in_the_single_status_message()
    {
        using var model = Load(requestedScene: "missing");

        var statusLine = Render(model);

        AssertSingleMessage(statusLine, "status",
            "Source restarted. Compatible setup retained and revalidated; select Start selected scene for a fresh run. "
            + "Requested scene 'missing' was not found. The configured start scene is selected instead.");
    }

    [Fact]
    public void Successful_source_adoption_is_the_only_status_message()
    {
        using var model = Load();
        var files = ComponentStory.Files();
        files["main.tale"] = files["main.tale"].Replace("The cave.", "An updated cave.", StringComparison.Ordinal);

        Assert.True(model.ApplySource(Compile(files)));

        var statusLine = Render(model);

        AssertSingleMessage(statusLine, "status", "Talemark source adopted. Ready for scene setup.");
    }

    [Fact]
    public void Restart_reports_disabled_overrides_in_the_single_status_message()
    {
        using var model = Load();
        model.Texts.Single(field => field.Id.Value == "weather").Enabled = true;
        var files = ComponentStory.Files();
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
            .Replace("  weather: \"sunny\"\n", "", StringComparison.Ordinal);

        Assert.True(model.ApplySource(Compile(files), restart: true));

        AssertSingleMessage(Render(model), "status",
            "Source restarted. Compatible setup retained and revalidated; select Start selected scene for a fresh run. "
            + "Disabled removed or incompatible setup overrides: var:weather.");
    }

    [Fact]
    public void In_place_reload_reports_disabled_overrides_in_the_single_status_message()
    {
        using var model = ComponentStory.Load();
        var weather = model.Texts.Single(field => field.Id.Value == "weather");
        weather.Enabled = true;
        weather.Value = "rain";
        model.Start();
        var files = ComponentStory.Files();
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
            .Replace("  weather: \"sunny\"\n", "", StringComparison.Ordinal);

        Assert.True(model.ApplySource(Compile(files), yamlChanged: true));

        AssertSingleMessage(Render(model), "status",
            "History changed; continuing from here with current state. "
            + "Disabled removed or incompatible setup overrides: var:weather.");
    }

    [Fact]
    public void Divergent_history_is_adopted_with_one_continuation_status_message()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var files = ComponentStory.Files();
        files["main.tale"] = files["main.tale"]
            .Replace("* [Explore] -> cave", "* [Explore] -> hall", StringComparison.Ordinal)
            .Replace("The cave.", "The updated cave.", StringComparison.Ordinal);

        Assert.True(model.ApplySource(Compile(files)));

        var statusLine = Render(model);

        Assert.False(model.RestartRequired);
        Assert.False(model.SourceRejected);
        Assert.False(model.Session!.CanBack);
        Assert.Contains("The updated cave.", model.Session.Current.Document.ToPlainText());
        AssertSingleMessage(statusLine, "status", "History changed; continuing from here with current state.");
    }

    [Fact]
    public void Compiler_rejection_keeps_its_error_alert_and_suppresses_the_redundant_scene_notice()
    {
        using var model = Load(requestedScene: "missing");
        var files = ComponentStory.Files();
        files["main.tale"] = files["main.tale"].Replace("-> cave", "-> nowhere", StringComparison.Ordinal);

        Assert.False(model.ApplySource(Compile(files)));

        var statusLine = Render(model);

        AssertSingleMessage(statusLine, "alert",
            "Story has compiler errors. Fix and save, or select Rescan / retry watcher. Any previous run is unchanged.");
        Assert.DoesNotContain("could not be verified", statusLine.Markup);
    }

    [Fact]
    public void Restart_required_wins_over_rejection_and_reload_status()
    {
        using var model = Load();
        model.RequireRestart();

        var statusLine = Render(model);

        Assert.True(model.SourceRejected);
        Assert.NotNull(model.ReloadStatus);
        AssertSingleMessage(statusLine, "status",
            "Restart required. The last good compiled story/run is still displayed. Correct or disable incompatible setup before restarting.");
    }

    [Fact]
    public void Watcher_failure_error_wins_over_restart_required_status()
    {
        using var model = Load();
        model.WatchFailed(new IOException("The watcher stopped unexpectedly"));

        var statusLine = Render(model);

        Assert.True(model.RestartRequired);
        AssertSingleMessage(statusLine, "alert",
            "Source watcher failed: The watcher stopped unexpectedly. Select Rescan / retry watcher. The last good run is unchanged.");
    }

    [Fact]
    public void Reload_failure_is_rendered_as_one_complete_error_alert()
    {
        using var model = Load();
        var source = new StorySource(ComponentStory.Files());

        Assert.False(model.ApplySource(new(source, null, "The source could not be read")));

        var statusLine = Render(model);

        Assert.True(model.SourceRejected);
        AssertSingleMessage(statusLine, "alert",
            "Could not reload story: The source could not be read. Any previous run is unchanged.");
    }

    private IRenderedComponent<StatusLine> Render(WorkbenchModel model) =>
        Render<StatusLine>(parameters => parameters.Add(component => component.Model, model));

    private static WorkbenchModel Load(string? requestedScene = null)
    {
        var source = new StorySource(ComponentStory.Files());
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var model = new WorkbenchModel("unused", requestedScene);
        Assert.True(model.ApplySource(new(source, compilation, null), restart: true));
        return model;
    }

    private static SourceCompilation Compile(Dictionary<string, string> files)
    {
        var source = new StorySource(files);
        return new(source, StoryCompiler.Compile(source), null);
    }

    private static void AssertSingleMessage(IRenderedComponent<StatusLine> statusLine, string role, string text)
    {
        var messages = statusLine.FindAll(".debugger-status-line p[role='status'], .debugger-status-line p[role='alert']");
        var message = Assert.Single(messages);
        Assert.Equal(role, message.GetAttribute("role"));
        Assert.Equal(text, message.TextContent);
    }
}
