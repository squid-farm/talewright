using Bunit;
using Microsoft.AspNetCore.Components;
using Talewright.Core.Identity;
using Talewright.Debugger;
using Talewright.Talemark.Model;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SceneConnectionsTests : BunitContext
{
    [Fact]
    public async Task Renders_ordered_incoming_and_outgoing_connections_with_the_taken_marker()
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
        SceneId? inspected = null;
        var connections = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, story)
            .Add(component => component.Scene, story.GetScene(new SceneId("cave")))
            .Add(component => component.Run, run)
            .Add(component => component.OnInspectScene,
                EventCallback.Factory.Create<SceneId>(this, scene => inspected = scene)));

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
        Assert.Null(outgoing.QuerySelector("button"));

        await incoming[0].QuerySelector("button")!.ClickAsync(new());
        Assert.Equal(new SceneId("start"), inspected);
    }

    [Fact]
    public void Shows_status_for_current_entry_choices_and_hub_offers_only()
    {
        using var model = DebuggerDecisionStory.Start();
        var scene = model.Story!.GetScene(new SceneId("hall"));
        var run = MapRunView.Build(model.Story, MapView.Build(model.Story), model.Session!);
        var connections = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.Run, run)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.CurrentScene, scene.Id));
        var outgoing = connections.FindAll("[aria-label='Outgoing connections'] li");

        AssertStatus(outgoing, "“Available”", "Available");
        AssertStatus(outgoing, "“Shown”", "Shown, unavailable");
        AssertStatus(outgoing, "“Hidden”", "Hidden");
        var checkRows = outgoing.Where(row => row.TextContent.Contains("Climb", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, checkRows.Length);
        Assert.All(checkRows, row => Assert.Contains("Shown, unavailable", row.TextContent));
        AssertStatus(outgoing, "Shown offer", "Shown, unavailable");
        AssertStatus(outgoing, "Limited offer", "Dropped by hub limit");
        AssertStatus(outgoing, "Hidden offer", "Hidden");

        var otherScene = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, model.Story.GetScene(new SceneId("cave")))
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.CurrentScene, scene.Id));
        Assert.Empty(otherScene.FindAll(".scene-connection-status"));
    }

    [Fact]
    public void Does_not_reuse_a_choice_decision_from_an_earlier_scene_entry()
    {
        using var model = DebuggerDecisionStory.Start(DebuggerDecisionStory.ReentryTale);
        var scene = model.Story!.GetScene(new SceneId("hall"));

        var firstEntry = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.CurrentScene, scene.Id));
        AssertStatus(firstEntry.FindAll("[aria-label='Outgoing connections'] li"), "Shown", "Shown, unavailable");

        var loop = model.Session!.Current.Choices.Single(choice => choice.Text == "Loop");
        model.Choose(loop.Key);
        Assert.Null(model.Error);
        Assert.Equal("hall", model.Session.Current.Scene!.Value.Value);

        var latestEntry = Render<SceneConnections>(parameters => parameters
            .Add(component => component.Story, model.Story)
            .Add(component => component.Scene, scene)
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.CurrentScene, model.Session.Current.Scene));
        AssertStatus(latestEntry.FindAll("[aria-label='Outgoing connections'] li"), "Shown", "Not reached");
    }

    private static void AssertStatus(IReadOnlyList<AngleSharp.Dom.IElement> rows, string rowText, string status)
    {
        var row = rows.Single(item => item.TextContent.Contains(rowText, StringComparison.Ordinal));
        Assert.Equal(status, row.QuerySelector(".scene-connection-status")?.TextContent);
    }
}
