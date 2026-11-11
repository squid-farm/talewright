using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class StateEditorTests : BunitContext
{
    [Fact]
    public void Shows_current_values_as_editable_fields()
    {
        using var model = ComponentStory.Start();
        var editor = Show(model);

        Assert.Equal("0", Field(editor, "var:count").GetAttribute("value"));
        Assert.Equal("sunny", Field(editor, "var:weather").GetAttribute("value"));
        Assert.Equal("1", Field(editor, "faction:river").GetAttribute("value"));
        Assert.Equal("1", Field(editor, "player inventory:lantern").GetAttribute("value"));
        Assert.False(Field(editor, "flag:ready").HasAttribute("checked"));
        Assert.Contains("player: Ada Vale", editor.FindAll("summary").Select(summary => summary.TextContent).First(text => text.StartsWith("player")));
        Assert.True(Button(editor, "Apply edits").HasAttribute("disabled"));
        Assert.True(Button(editor, "Revert edits").HasAttribute("disabled"));
    }

    [Fact]
    public void Editing_a_value_marks_it_pending_and_applies_through_the_callback()
    {
        using var model = ComponentStory.Start();
        var applied = 0;
        var editor = Show(model, () => applied++);

        Field(editor, "var:count").Input("4");
        Field(editor, "flag:ready").Change(true);

        var count = model.EditNumbers.Single(field => field.Label == "var:count");
        Assert.True(count.Enabled);
        Assert.Equal("4", count.Value);
        Assert.True(model.EditFlags.Single().Enabled);
        Assert.Equal("2 edits pending", editor.Find(".state-pending").TextContent);
        Assert.Equal(2, editor.FindAll("tr.state-edited").Count);
        Assert.Contains(editor.FindAll("tr.state-edited"), row => row.TextContent.Contains("var:count") && row.TextContent.Contains("was 0"));

        Button(editor, "Apply edits").Click();
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Returning_a_value_to_its_current_state_clears_the_pending_edit()
    {
        using var model = ComponentStory.Start();
        var editor = Show(model);

        Field(editor, "var:count").Input("4");
        Field(editor, "var:count").Input("0");

        Assert.Equal(0, model.PendingEdits);
        Assert.Empty(editor.FindAll(".state-pending"));
    }

    [Fact]
    public void Revert_restores_current_values()
    {
        using var model = ComponentStory.Start();
        var editor = Show(model);

        Field(editor, "var:count").Input("4");
        Button(editor, "Revert edits").Click();

        Assert.Equal(0, model.PendingEdits);
        Assert.Equal("0", Field(editor, "var:count").GetAttribute("value"));
    }

    [Fact]
    public void Applying_edits_updates_the_live_run()
    {
        using var model = ComponentStory.Start();
        var editor = Show(model, model.ApplyEdits);

        Field(editor, "var:weather").Input("rain");
        Button(editor, "Apply edits").Click();
        editor.Render(parameters => parameters.Add(component => component.Inspection, model.Inspection!));

        Assert.Null(model.Error);
        Assert.Equal("rain", Field(editor, "var:weather").GetAttribute("value"));
        Assert.Equal(0, model.PendingEdits);
    }

    [Fact]
    public void Filter_narrows_the_rows()
    {
        using var model = ComponentStory.Start();
        var editor = Show(model);

        editor.Find("input[aria-label='Filter state']").Input("weather");

        Assert.NotNull(Field(editor, "var:weather"));
        Assert.Empty(editor.FindAll("input[aria-label='Edit var:count']"));
        Assert.Empty(editor.FindAll("input[aria-label='Edit faction:river']"));
    }

    [Fact]
    public void Visits_and_triggers_are_read_only()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var editor = Show(model);

        var visits = editor.FindAll("details").Single(details => details.QuerySelector("summary")!.TextContent == "Scene visits");
        Assert.Contains("cave1", visits.TextContent.Replace(" ", "").Replace("\n", ""));
        Assert.Empty(visits.QuerySelectorAll("input"));
        var triggers = editor.FindAll("details").Single(details => details.QuerySelector("summary")!.TextContent == "Triggers");
        Assert.Contains("Fired", triggers.TextContent);
    }

    [Fact]
    public void An_ended_run_cannot_be_edited()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        ComponentStory.Choose(model, "Leave");

        var editor = Show(model);

        Assert.True(Button(editor, "Apply edits").HasAttribute("disabled"));
        Assert.True(Field(editor, "var:count").HasAttribute("disabled"));
        Assert.Contains("Ended runs cannot be edited.", editor.Markup);
    }

    private IRenderedComponent<StateEditor> Show(WorkbenchModel model, Action? apply = null) =>
        Render<StateEditor>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Session, model.Session!)
            .Add(component => component.Inspection, model.Inspection!)
            .Add(component => component.OnApplyEdits, apply ?? (() => { })));

    private static IElement Field(IRenderedComponent<StateEditor> editor, string label) =>
        editor.Find($"input[aria-label='Edit {label}']");

    private static IElement Button(IRenderedComponent<StateEditor> editor, string text) =>
        editor.FindAll("button").Single(button => button.TextContent == text);
}
