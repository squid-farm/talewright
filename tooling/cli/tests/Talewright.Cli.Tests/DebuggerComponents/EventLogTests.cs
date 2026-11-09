using Bunit;
using Talewright.Debugger;
using Talewright.Runtime;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class EventLogTests : BunitContext
{
    [Fact]
    public void Lists_events_with_their_offsets()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        var log = Show(model.Inspection!);

        Assert.Equal("Events", log.Find("summary").TextContent);
        Assert.Equal(
        [
            "0: SceneEntered { Scene = hall }",
            "1: ChoiceMade { Scene = hall, ChoiceKey = c1 }",
            "2: SceneEntered { Scene = cave }",
            "3: VariableChanged { Variable = count, OldValue = 0, NewValue = 1 }",
            "4: TriggerFired { TriggerId = counted }",
            "5: FlagChanged { Flag = ready, Value = True }",
        ], Items(log));
    }

    [Fact]
    public void Filter_keeps_original_offsets_and_ignores_case()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");

        Assert.Equal(["2: SceneEntered { Scene = cave }"], Items(Show(model.Inspection!, "CAVE")));
        Assert.Equal(["3: VariableChanged { Variable = count, OldValue = 0, NewValue = 1 }", "4: TriggerFired { TriggerId = counted }"],
            Items(Show(model.Inspection!, "count")));
        Assert.Empty(Items(Show(model.Inspection!, "nothing matches")));
    }

    [Fact]
    public void Shows_only_the_most_recent_two_hundred_matches()
    {
        var inspection = LongRun(150);
        Assert.Equal(301, inspection.Events.Count);

        var all = Items(Show(inspection));
        Assert.Equal(200, all.Count);
        Assert.StartsWith("101: ", all[0]);
        Assert.StartsWith("300: ", all[^1]);

        var choices = Items(Show(inspection, "ChoiceMade"));
        Assert.Equal(150, choices.Count);
        Assert.StartsWith("1: ChoiceMade", choices[0]);
    }

    /// <summary>A one-room run of <paramref name="choices"/> choices: an entry, then a choice and re-entry each time.</summary>
    internal static AuthorInspection LongRun(int choices, bool captureDecisions = true)
    {
        using var model = ComponentStory.Load(ComponentStory.Rooms(1));
        model.Start();
        var stay = Assert.Single(model.Session!.Current.Choices).Key;
        // Each author command replays the timeline, so build long runs through linear player history.
        var history = new StoryHistory(model.Story!, model.Session.Seed);
        for (var i = 0; i < choices; i++)
            history.Choose(stay);
        return history.ForkAuthor(captureDecisions).Inspect();
    }

    private IRenderedComponent<EventLog> Show(AuthorInspection inspection, string filter = "") =>
        Render<EventLog>(parameters => parameters
            .Add(component => component.Inspection, inspection)
            .Add(component => component.Filter, filter));

    private static List<string> Items(IRenderedComponent<EventLog> log) =>
        log.FindAll("li").Select(item => item.TextContent).ToList();
}
