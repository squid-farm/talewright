using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SourceToolTests : BunitContext
{
    [Fact]
    public void Shows_the_accepted_source_of_the_current_scene()
    {
        using var model = ComponentStory.Start();

        var tool = Render<SourceTool>(parameters => parameters.Add(component => component.Model, model));

        var view = tool.FindComponent<SourceView>().Instance;
        Assert.Same(model.CurrentSource!.Text, view.Source!.Text);
        Assert.Equal(model.CurrentSource.Range, view.Source.Range);
        Assert.Contains("Accepted file: main.tale. Scene hall source range: lines 1 to 6 (inclusive).", tool.Markup);
        Assert.Empty(tool.FindAll("p[role=status]"));
    }

    [Fact]
    public void Follows_the_run_into_another_scene()
    {
        using var model = ComponentStory.Start();
        var tool = Render<SourceTool>(parameters => parameters.Add(component => component.Model, model));

        ComponentStory.Choose(model, "Explore");
        tool.Render();

        Assert.Contains("Scene cave source range: lines 7 to 12 (inclusive).", tool.Markup);
        Assert.Equal(["7", "8", "9", "10", "11", "12"],
            tool.FindAll("tr.source-scene th").Select(cell => cell.TextContent));
    }

    [Fact]
    public void Passes_pending_and_restart_required_source_states_through()
    {
        using var model = ComponentStory.Start();
        model.SourceChanged();
        model.RequireRestart();

        var tool = Render<SourceTool>(parameters => parameters.Add(component => component.Model, model));

        var view = tool.FindComponent<SourceView>().Instance;
        Assert.True(view.Pending);
        Assert.True(view.Rejected);
        Assert.True(view.RestartRequired);
        Assert.Equal(
            ["Disk changes pending compilation/adoption. Accepted source remains displayed.",
             "Disk source is stale or restart-required, not adopted. This view still shows accepted source."],
            tool.FindAll("p[role=status]").Select(status => status.TextContent));
        Assert.Contains("Scene hall source range", tool.Markup);
    }

    [Fact]
    public void Explains_a_model_without_a_run()
    {
        using var model = ComponentStory.Load();

        var tool = Render<SourceTool>(parameters => parameters.Add(component => component.Model, model));

        Assert.Null(tool.FindComponent<SourceView>().Instance.Source);
        Assert.StartsWith("Source mapping unavailable for this passage", tool.Find("p[role=status]").TextContent);
        Assert.Empty(tool.FindAll("table"));
    }
}
