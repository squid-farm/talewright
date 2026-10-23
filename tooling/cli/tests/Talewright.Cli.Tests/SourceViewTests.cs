using Talewright.Debugger;
using Talewright.Tale.Compilation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Talewright.Cli.Tests;

public sealed class SourceViewTests
{
    private const string Yaml = "id: view\ntitle: Source View\nstart: first\ncharacters:\n  player: {first-name: Tester}\n";
    private const string First = "=== first\nFirst source.\n* [Go] -> second";
    private const string Second = "// second file\n=== second\nSecond source.\n* [Return] -> first";

    private static SourceCompilation Compile(string first = First, string second = Second, string yaml = Yaml)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = yaml, ["first.tale"] = first, ["nested/second.tale"] = second,
        });
        return new(source, StoryCompiler.Compile(source), null);
    }

    private static WorkbenchModel Start()
    {
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(Compile(), restart: true));
        Assert.Null(model.CurrentSource);
        model.Start();
        return model;
    }

    [Fact]
    public void Source_follows_choices_jumps_back_forward_and_fresh_starts_across_files()
    {
        var model = Start();
        Assert.Equal(First, model.CurrentSource!.Text);
        Assert.Equal(new SceneSourceRange("first.tale", 1, 3), model.CurrentSource.Range);
        model.Choose("c0");
        Assert.Equal(Second, model.CurrentSource!.Text);
        Assert.Equal(new SceneSourceRange("nested/second.tale", 2, 4), model.CurrentSource.Range);
        model.Back();
        Assert.Equal(First, model.CurrentSource!.Text);
        model.Forward();
        Assert.Equal(Second, model.CurrentSource!.Text);
        model.Target = "first";
        model.JumpPreservingState();
        Assert.Equal(First, model.CurrentSource!.Text);
        model.Target = "second";
        model.Start();
        Assert.Equal(Second, model.CurrentSource!.Text);
    }

    [Fact]
    public void Accepted_reload_replaces_text_and_ranges_together_rejections_keep_the_accepted_snapshot()
    {
        var model = Start();
        model.Choose("c0");
        var updated = "// new preamble\n" + Second.Replace("Second source.", "Accepted update.");
        Assert.True(model.ApplySource(Compile(second: updated)));
        var accepted = model.CurrentSource;
        Assert.Equal(updated, accepted!.Text);
        Assert.Equal(new SceneSourceRange("nested/second.tale", 3, 5), accepted.Range);

        Assert.False(model.ApplySource(Compile(second: "=== second\n-> missing")));
        Assert.Equal(accepted, model.CurrentSource);
        Assert.True(model.SourceRejected);
        Assert.False(model.SourcePending);

        Assert.False(model.ApplySource(Compile(first: First.Replace("First source.", "Changed consumed scene."))));
        Assert.Equal(accepted, model.CurrentSource);
        Assert.True(model.RestartRequired);
    }

    [Fact]
    public void Pending_yaml_and_superseded_candidates_cannot_replace_source()
    {
        var model = Start();
        var accepted = model.CurrentSource;
        model.SourceChanged();
        Assert.True(model.SourcePending);
        Assert.Equal(accepted, model.CurrentSource);
        Assert.False(model.ApplySource(Compile(first: "// stale\n" + First), commit: _ => false));
        Assert.Equal(accepted, model.CurrentSource);
        Assert.True(model.SourcePending);
        Assert.False(model.ApplySource(Compile(first: "// new\n" + First, yaml: Yaml + "# changed")));
        Assert.Equal(accepted, model.CurrentSource);
        Assert.False(model.SourcePending);
        Assert.True(model.RestartRequired);
        Assert.True(model.ApplySource(Compile(first: "// new\n" + First, yaml: Yaml + "# changed"), restart: true));
        Assert.Null(model.CurrentSource);
        model.Start();
        Assert.Equal("// new\n" + First, model.CurrentSource!.Text);
        Assert.Equal(2, model.CurrentSource.Range.StartLine);
    }

    [Fact]
    public void Unmapped_models_do_not_guess_from_supplied_source_or_a_previous_compilation()
    {
        var model = Start();
        var compiled = Compile();
        var unmapped = new SourceCompilation(compiled.Source,
            new(compiled.Compilation!.Story, compiled.Compilation.Diagnostics), null);
        Assert.True(model.ApplySource(unmapped, restart: true));
        model.Start();
        Assert.NotNull(model.Session);
        Assert.Null(model.AcceptedSourceMap);
        Assert.Null(model.CurrentSource);
    }

    [Fact]
    public void Viewer_uses_compiler_provenance_not_an_unrelated_source_argument()
    {
        var model = Start();
        var actual = Compile(first: "// actual\n" + First);
        var unrelated = Compile(first: "// unrelated\n" + First);
        Assert.True(model.ApplySource(actual with { Source = unrelated.Source }, restart: true));
        model.Start();
        Assert.Equal("// actual\n" + First, model.CurrentSource!.Text);
    }

    [Fact]
    public async Task Unmapped_view_explains_absence_and_pending_stale_states_without_showing_a_file()
    {
        var html = await Render(new()
        {
            [nameof(SourceView.Pending)] = true,
            [nameof(SourceView.Rejected)] = true,
            [nameof(SourceView.RestartRequired)] = true,
        });
        Assert.Contains("Source mapping unavailable", html);
        Assert.Contains("C#-defined scene", html);
        Assert.Contains("Disk changes pending", html);
        Assert.Contains("not adopted", html);
        Assert.DoesNotContain("<table", html);
    }

    [Fact]
    public async Task Large_file_renders_at_most_100_numbered_rows_near_scene_and_encodes_source_as_text()
    {
        var text = string.Join("\n", Enumerable.Range(1, 1200).Select(i => $"// line {i} <script>alert()</script>"));
        var html = await Render(new()
        {
            [nameof(SourceView.Source)] = new AcceptedSceneSource(new("late"), new("large.tale", 1000, 1100), text),
        });
        Assert.Contains("lines 1000 to 1100 (inclusive)", html);
        Assert.Contains("Showing accepted lines 997 to 1096 of 1200", html);
        Assert.Equal(100, html.Split("scope=\"row\"").Length - 1);
        Assert.Equal(97, html.Split("class=\"source-scene\"").Length - 1);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("contenteditable", html);
        Assert.DoesNotContain("<textarea", html);
    }

    private static async Task<string> Render(Dictionary<string, object?> parameters)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SourceView>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }
}
