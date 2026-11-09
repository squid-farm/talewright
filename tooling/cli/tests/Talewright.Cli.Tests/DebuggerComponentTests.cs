using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Talewright.Debugger;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests;

public sealed class DebuggerComponentTests
{
    [Fact]
    public async Task Map_scene_details_render_accessible_divert_and_hub_offer_lists()
    {
        var view = new MapView("Components", [
            new MapNode("start", "start", MapView.MainArea, ["start"], "Start (start)", null,
                true, false, true, false, true, false,
                [
                    new MapItem("d0", "line0", "road", "Divert to road, main.tale line 3", false),
                    new MapItem("o0", "line1", "well", "Hub offer of Well (well) via #water", true),
                ]),
        ], [], [], 0);

        var html = await Render<MapSceneDetails>(new()
        {
            [nameof(MapSceneDetails.View)] = view,
            [nameof(MapSceneDetails.SelectedNodeId)] = "start",
        });

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
    public async Task Command_list_keeps_applied_and_redo_state_and_command_consequences()
    {
        using var model = Start();
        model.Choose("c0");

        var applied = await Render<CommandList>(new()
        {
            [nameof(CommandList.Session)] = model.Session!,
        });
        Assert.Contains("Choice: c0 (applied)", applied);

        var consequence = WebUtility.HtmlEncode(model.Session!.Results[0].Events[0].ToString());
        Assert.Contains(consequence, applied);
        model.Back();
        var redo = await Render<CommandList>(new()
        {
            [nameof(CommandList.Session)] = model.Session!,
        });
        Assert.Contains("Choice: c0 (redo)", redo);
    }

    private static WorkbenchModel Start()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: debugger-component-test
                  title: Debugger component test
                  start: start
                characters:
                  player: {first-name: Tester}
                """,
            ["main.tale"] = """
                === start
                * [Continue] -> next
                === next
                -> END
                """,
        });
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var model = new WorkbenchModel("unused");
        Assert.True(model.ApplySource(new SourceCompilation(source, compilation, null), restart: true));
        model.Start();
        Assert.Null(model.Error);
        return model;
    }

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return component.ToHtmlString();
        });
    }
}
