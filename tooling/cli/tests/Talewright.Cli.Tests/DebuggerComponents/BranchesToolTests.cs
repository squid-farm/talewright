using Bunit;
using Microsoft.AspNetCore.Components;
using Talewright.Core.Identity;
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

    [Fact]
    public async Task Keyboard_action_inspects_a_choice_without_selecting_it()
    {
        using var model = ComponentStory.Start();
        var choiceDecision = model.Inspection!.Decisions.Single(decision =>
            decision.Kind == DecisionKind.Choice && decision.Subject == "step:4:1");
        var selections = new List<InspectorSelection>();
        var currentScene = model.Session!.Current.Scene;
        var eventCount = model.Inspection.Events.Count;
        var tool = Render<BranchesTool>(parameters => parameters
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.Story, model.Story)
            .Add(component => component.FocusedDecision, choiceDecision)
            .Add(component => component.OnInspectSelection,
                EventCallback.Factory.Create<InspectorSelection>(this, selection => selections.Add(selection))));

        var details = Assert.Single(tool.FindAll("details[open]"));
        Assert.Contains("Choice hall step:4:1", details.QuerySelector("summary")!.TextContent);
        Assert.DoesNotContain(model.Session.Current.Choices, choice => choice.Text == "Prepared");
        await details.QuerySelector("button")!.ClickAsync(new());

        Assert.Equal([new InspectorSelection(new SceneId("hall"), "c0")], selections);
        Assert.Equal(currentScene, model.Session.Current.Scene);
        Assert.Equal(eventCount, model.Inspection.Events.Count);
    }

    [Fact]
    public async Task Storylet_decision_inspects_its_target_scene()
    {
        var files = ComponentStory.Files();
        files["main.tale"] = """
            === hall
            @hub travel
            ~ var:count += 1
            * [Finish] -> END
            === cave
            @tags travel
            The destination.
            * [Return] -> hall
            """;
        using var model = ComponentStory.Load(files);
        model.Start();
        var storyletDecision = model.Inspection!.Decisions.Single(decision =>
            decision.Kind == DecisionKind.Storylet && decision.Subject == "cave");
        var selections = new List<InspectorSelection>();
        var tool = Render<BranchesTool>(parameters => parameters
            .Add(component => component.Inspection, model.Inspection)
            .Add(component => component.Story, model.Story)
            .Add(component => component.FocusedDecision, storyletDecision)
            .Add(component => component.OnInspectSelection,
                EventCallback.Factory.Create<InspectorSelection>(this, selection => selections.Add(selection))));

        var decision = Assert.Single(tool.FindAll("details[open]"));
        await decision.QuerySelector("button")!.ClickAsync(new());

        Assert.Equal([new InspectorSelection(new SceneId("cave"))], selections);
    }
}
