using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class TimelineToolTests : BunitContext
{
    [Fact]
    public void A_fresh_playtest_is_at_the_origin_and_cannot_move()
    {
        using var model = ComponentStory.Start();

        var timeline = Show(model);

        Assert.StartsWith("Position 0 / 0.", timeline.Find("section[aria-label='Author timeline'] p").TextContent);
        Assert.True(Button(timeline, "Back command").HasAttribute("disabled"));
        Assert.True(Button(timeline, "Forward command").HasAttribute("disabled"));
        Assert.Single(timeline.FindComponents<CommandList>());
    }

    [Fact]
    public void Position_and_buttons_follow_the_cursor()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        ComponentStory.Choose(model, "Return");
        model.Back();

        var timeline = Show(model);

        Assert.StartsWith("Position 1 / 2.", timeline.Find("section p").TextContent);
        Assert.False(Button(timeline, "Back command").HasAttribute("disabled"));
        Assert.False(Button(timeline, "Forward command").HasAttribute("disabled"));
    }

    [Fact]
    public void Back_and_forward_raise_their_callbacks()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        model.Back();
        var calls = new List<string>();
        var timeline = Show(model, () => calls.Add("back"), () => calls.Add("forward"));

        Button(timeline, "Forward command").Click();
        Assert.Equal(["forward"], calls);

        model.Forward();
        timeline.Render(parameters => parameters.Add(component => component.Session, model.Session!));
        Button(timeline, "Back command").Click();
        Assert.Equal(["forward", "back"], calls);
    }

    private IRenderedComponent<TimelineTool> Show(WorkbenchModel model, Action? back = null, Action? forward = null) =>
        Render<TimelineTool>(parameters => parameters
            .Add(component => component.Session, model.Session!)
            .Add(component => component.OnBack, back ?? (() => { }))
            .Add(component => component.OnForward, forward ?? (() => { })));

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<TimelineTool> timeline, string text) =>
        timeline.FindAll("button").Single(button => button.TextContent == text);
}
