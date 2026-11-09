using Bunit;
using Talewright.Debugger;
using Talewright.Rules.Diagnostics;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class BranchesToolTests : BunitContext
{
    [Fact]
    public void Offers_every_decision_kind()
    {
        using var model = ComponentStory.Start();

        var tool = Render<BranchesTool>(parameters => parameters.Add(component => component.Inspection, model.Inspection!));

        Assert.Equal(["All", .. Enum.GetNames<DecisionKind>()],
            tool.FindAll("select[aria-label='Decision kind'] option").Select(option => option.TextContent));
        Assert.Equal(["", .. Enum.GetNames<DecisionKind>()],
            tool.FindAll("select[aria-label='Decision kind'] option").Select(option => option.GetAttribute("value")));
        Assert.DoesNotContain("Decision recording unavailable", tool.Markup);
    }

    [Fact]
    public void Filter_and_kind_narrow_the_decision_list()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var tool = Render<BranchesTool>(parameters => parameters.Add(component => component.Inspection, model.Inspection!));
        Assert.Equal(12, DecisionListTests.Summaries(tool).Count);

        tool.Find("input[aria-label='Filter decisions']").Input("cave");
        Assert.Equal("cave", tool.FindComponent<DecisionList>().Instance.Filter);
        Assert.Equal(6, DecisionListTests.Summaries(tool).Count);

        tool.Find("select[aria-label='Decision kind']").Change("Choice");
        Assert.Equal(DecisionKind.Choice, tool.FindComponent<DecisionList>().Instance.Kind);
        Assert.Equal(["Choice cave step:11:1: Offered (Unconditional)", "Choice cave step:12:1: Offered (Unconditional)"],
            DecisionListTests.Summaries(tool));

        tool.Find("select[aria-label='Decision kind']").Change("");
        Assert.Null(tool.FindComponent<DecisionList>().Instance.Kind);
        Assert.Equal(6, DecisionListTests.Summaries(tool).Count);
    }

    [Fact]
    public void Explains_runs_that_did_not_record_decisions()
    {
        var inspection = EventLogTests.LongRun(1, captureDecisions: false);
        Assert.False(inspection.DecisionsCaptured);

        var tool = Render<BranchesTool>(parameters => parameters.Add(component => component.Inspection, inspection));

        Assert.Contains("Decision recording unavailable for this run.", tool.Markup);
        Assert.Empty(DecisionListTests.Summaries(tool));
    }
}
