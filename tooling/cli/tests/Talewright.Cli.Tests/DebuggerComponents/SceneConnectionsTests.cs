using Bunit;
using Talewright.Core.Identity;
using Talewright.Debugger;
using Talewright.Talemark.Model;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SceneConnectionsTests : BunitContext
{
    [Fact]
    public void Renders_ordered_incoming_and_outgoing_connections_with_the_taken_marker()
    {
        var files = ComponentStory.Files();
        files[Talewright.Talemark.Compilation.StorySource.ManifestFileName] =
            files[Talewright.Talemark.Compilation.StorySource.ManifestFileName]
                .Replace("start: hall", "start: start", StringComparison.Ordinal);
        files["main.tale"] = """
            === start
            @hub road
            * [Loop] -> start
            * [Go] -> cave
            === camp
            @tags road
            @hub road
            * [Travel] -> cave
            === cave
            @title Cave
            @tags road
            @requires flag:ready
            * [Finish] -> END
            """;
        using var model = ComponentStory.Load(files);
        model.Start();
        ComponentStory.Choose(model, "Go");

        var story = model.Story!;
        var run = MapRunView.Build(story, MapView.Build(story), model.Session!);
        var connections = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, story)
            .Add(component => component.Scene, story.GetScene(new SceneId("cave")))
            .Add(component => component.Run, run));

        Assert.Equal("4 in · 1 out", connections.Find("summary").TextContent.Trim());
        Assert.True(connections.Find("details").HasAttribute("open"));
        var incoming = connections.FindAll("[aria-label='Incoming connections'] li");
        Assert.Equal(4, incoming.Count);
        Assert.Contains("← start", incoming[0].TextContent);
        Assert.Contains("Choice c1 “Go”", incoming[0].TextContent);
        Assert.Equal("taken", incoming[0].QuerySelector("strong")?.TextContent);
        Assert.Contains("Hub offer of Cave (cave) via #road, if flag:ready", incoming[1].TextContent);
        Assert.Contains("← camp", incoming[2].TextContent);
        Assert.Contains("Choice c0 “Travel”", incoming[2].TextContent);
        Assert.Contains("← camp", incoming[3].TextContent);
        Assert.Contains("Hub offer of Cave (cave) via #road, if flag:ready", incoming[3].TextContent);

        var outgoing = Assert.Single(connections.FindAll("[aria-label='Outgoing connections'] li"));
        Assert.Contains("→ END", outgoing.TextContent);
        Assert.Contains("Choice c0 “Finish” to END", outgoing.TextContent);
        Assert.Null(outgoing.QuerySelector("strong"));
    }
}
