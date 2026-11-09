using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;
using Talewright.Rules.Diagnostics;
using Talewright.Runtime;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class DecisionListTests : BunitContext
{
    [Fact]
    public void Summarizes_each_decision_in_order()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        var list = Show(model.Inspection!);

        Assert.Equal(
        [
            "Trigger counted: Primed (InitialCondition)",
            "Trigger hall counted: Suppressed (ConditionFalse)",
            "Choice hall step:4:1: Excluded (ConditionFalse)",
            "Choice hall step:5:1: Offered (Unconditional)",
            "Choice hall step:6:1: Offered (Unconditional)",
            "Trigger hall counted: Suppressed (ConditionFalse)",
            "Trigger cave counted: Suppressed (ConditionFalse)",
            "Trigger cave counted: Fired (RisingEdge)",
            "Trigger cave counted: Suppressed (NotRising)",
            "EntryEffect cave step:9:1: Applied (Unconditional)",
            "Choice cave step:11:1: Offered (Unconditional)",
            "Choice cave step:12:1: Offered (Unconditional)",
        ], Summaries(list));
    }

    [Fact]
    public void Scene_steps_show_their_source_and_triggers_do_not()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        var list = Show(model.Inspection!);

        Assert.StartsWith("Source: main.tale:4:", Paragraphs(Decision(list, "Choice hall step:4:1"))[0]);
        Assert.StartsWith("Source: main.tale:9:", Paragraphs(Decision(list, "EntryEffect cave step:9:1"))[0]);
        Assert.Equal(["Events [4, 6). Pass: 0."], Paragraphs(Decision(list, "Trigger cave counted: Fired")));
    }

    [Fact]
    public void Lists_the_events_each_decision_produced()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        var list = Show(model.Inspection!);

        Assert.Equal(
        [
            "VariableChanged { Variable = count, OldValue = 0, NewValue = 1 }",
            "TriggerFired { TriggerId = counted }",
            "FlagChanged { Flag = ready, Value = True }",
        ], Events(Decision(list, "EntryEffect cave step:9:1")));
        var fired = Decision(list, "Trigger cave counted: Fired");
        Assert.Equal(["TriggerFired { TriggerId = counted }", "FlagChanged { Flag = ready, Value = True }"], Events(fired));
        // The condition trace is a separate list ahead of the events.
        Assert.Equal(2, Lists(fired).Count);
        Assert.Equal("var:count >= 1", Lists(fired)[0].QuerySelector("code")!.TextContent);
        // Empty event ranges list nothing.
        Assert.Empty(Decision(list, "Choice hall step:5:1").QuerySelectorAll("ul"));
    }

    [Fact]
    public void Filters_by_subject_or_scene_and_by_kind()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var inspection = model.Inspection!;

        Assert.Equal(6, Summaries(Show(inspection, "CAVE")).Count);
        Assert.Equal(["Choice hall step:4:1: Excluded (ConditionFalse)"], Summaries(Show(inspection, "step:4")));
        Assert.Equal(5, Summaries(Show(inspection, kind: DecisionKind.Choice)).Count);
        Assert.Equal(["Choice cave step:11:1: Offered (Unconditional)", "Choice cave step:12:1: Offered (Unconditional)"],
            Summaries(Show(inspection, "cave", DecisionKind.Choice)));
        Assert.Empty(Summaries(Show(inspection, kind: DecisionKind.Check)));
    }

    [Fact]
    public void Shows_only_the_most_recent_hundred_matches()
    {
        var inspection = EventLogTests.LongRun(150);
        Assert.True(inspection.Decisions.Count > 100);

        var summaries = Summaries(Show(inspection));

        Assert.Equal(100, summaries.Count);
        Assert.Equal(inspection.Decisions.TakeLast(100).Select(Summary), summaries);
    }

    private IRenderedComponent<DecisionList> Show(AuthorInspection inspection, string filter = "", DecisionKind? kind = null) =>
        Render<DecisionList>(parameters => parameters
            .Add(component => component.Inspection, inspection)
            .Add(component => component.Filter, filter)
            .Add(component => component.Kind, kind));

    private static string Summary(RuleDecision decision) =>
        Normalize($"{decision.Kind} {decision.Scene} {decision.Subject}: {decision.Outcome} ({decision.Reason})");

    internal static List<string> Summaries<T>(IRenderedComponent<T> list) where T : Microsoft.AspNetCore.Components.IComponent =>
        list.FindAll("details > summary").Select(summary => Normalize(summary.TextContent)).ToList();

    private static IElement Decision(IRenderedComponent<DecisionList> list, string summary) =>
        list.FindAll("details").Single(details => Normalize(details.QuerySelector("summary")!.TextContent).StartsWith(summary, StringComparison.Ordinal));

    private static List<IElement> Lists(IElement decision) => decision.Children.Where(child => child.LocalName == "ul").ToList();

    private static List<string> Events(IElement decision) => Lists(decision)[^1].Children.Select(item => item.TextContent).ToList();

    private static List<string> Paragraphs(IElement decision) =>
        decision.QuerySelectorAll("p").Select(paragraph => Normalize(paragraph.TextContent)).ToList();

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
