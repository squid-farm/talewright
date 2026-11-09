using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class LiveEditToolTests : BunitContext
{
    [Fact]
    public void Edits_use_live_labels_and_raise_their_actions()
    {
        using var model = ComponentStory.Start();
        var applied = 0;
        var jumped = 0;
        var tool = Show(model, () => applied++, () => jumped++);

        var fields = tool.FindComponent<SetupFields>().Instance;
        Assert.Same(model.EditNumbers, fields.Numbers);
        Assert.Same(model.EditFlags, fields.Flags);
        Assert.Same(model.EditTexts, fields.Texts);
        Assert.Equal("Live edit values", tool.Find("details > summary").TextContent);
        Assert.NotNull(tool.Find("input[aria-label='Edit var:count']"));
        Assert.NotNull(tool.Find("input[aria-label='Edit value var:count']"));

        Button(tool, "Apply live edit batch").Click();
        Button(tool, "Jump retaining state").Click();

        Assert.Equal(1, applied);
        Assert.Equal(1, jumped);
        Assert.DoesNotContain("Ended runs cannot be edited", tool.Markup);
    }

    [Fact]
    public void Field_input_reaches_the_model_edits()
    {
        using var model = ComponentStory.Start();
        var tool = Show(model);

        tool.Find("input[aria-label='Edit var:count']").Change(true);
        tool.Find("input[aria-label='Edit value var:count']").Change("4");

        var count = model.EditNumbers.Single(field => field.Label == "var:count");
        Assert.True(count.Enabled);
        Assert.Equal("4", count.Value);
    }

    [Fact]
    public void An_ended_run_cannot_be_edited_or_jumped()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        ComponentStory.Choose(model, "Leave");

        var tool = Show(model);

        Assert.True(Button(tool, "Apply live edit batch").HasAttribute("disabled"));
        Assert.True(Button(tool, "Jump retaining state").HasAttribute("disabled"));
        Assert.Contains("Ended runs cannot be edited or jumped retaining state.", tool.Markup);
    }

    private IRenderedComponent<LiveEditTool> Show(WorkbenchModel model, Action? apply = null, Action? jump = null) =>
        Render<LiveEditTool>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Session, model.Session!)
            .Add(component => component.OnApplyEdits, apply ?? (() => { }))
            .Add(component => component.OnJumpPreservingState, jump ?? (() => { })));

    private static IElement Button(IRenderedComponent<LiveEditTool> tool, string text) =>
        tool.FindAll("button").Single(button => button.TextContent == text);
}
