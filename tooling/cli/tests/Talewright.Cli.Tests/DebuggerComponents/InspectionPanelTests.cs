using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class InspectionPanelTests : BunitContext
{
    [Fact]
    public void One_filter_drives_both_the_state_table_and_the_event_log()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var panel = Render<InspectionPanel>(parameters => parameters.Add(component => component.Inspection, model.Inspection!));
        Assert.Equal(6, panel.FindComponent<EventLog>().FindAll("li").Count);

        panel.Find("input[aria-label='Filter inspection']").Input("cave");

        Assert.Equal("cave", panel.Find("input[aria-label='Filter inspection']").GetAttribute("value"));
        Assert.Equal("cave", panel.FindComponent<StateTable>().Instance.Filter);
        Assert.Equal(["cave visits: 1"], panel.FindComponent<StateTable>().FindAll("li").Select(item => item.TextContent));
        Assert.Equal(["2: SceneEntered { Scene = cave }"], panel.FindComponent<EventLog>().FindAll("li").Select(item => item.TextContent));
    }

    [Fact]
    public void Hosts_branch_explanations_with_their_own_filter()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var panel = Render<InspectionPanel>(parameters => parameters.Add(component => component.Inspection, model.Inspection!));

        panel.Find("input[aria-label='Filter inspection']").Input("cave");

        var branches = panel.FindComponent<BranchesTool>();
        Assert.Same(model.Inspection, branches.Instance.Inspection);
        Assert.Equal(12, DecisionListTests.Summaries(branches).Count);
    }
}
