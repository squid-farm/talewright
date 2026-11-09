using System.Net;
using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class CommandListTests : BunitContext
{
    [Fact]
    public void A_fresh_playtest_has_an_empty_history()
    {
        using var model = ComponentStory.Start();

        var commands = Show(model);

        Assert.Equal("Command history and consequences", commands.Find("details > summary").TextContent);
        Assert.Empty(commands.FindAll("ol > li"));
    }

    [Fact]
    public void Command_list_keeps_applied_and_redo_state_and_command_consequences()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var key = model.Session!.Commands.OfType<Talewright.Runtime.AuthorChoice>().Single().Key;

        var applied = Show(model).Markup;
        Assert.Contains($"Choice: {key} (applied)", applied);

        var consequence = WebUtility.HtmlEncode(model.Session!.Results[0].Events[0].ToString());
        Assert.Contains(consequence, applied);
        model.Back();
        var redo = Show(model).Markup;
        Assert.Contains($"Choice: {key} (redo)", redo);
    }

    [Fact]
    public void Live_edits_list_actual_value_changes()
    {
        using var model = ComponentStory.Start();
        ComponentStory.EditCount(model, 7);

        var item = Assert.Single(Show(model).FindAll("ol > li"));

        Assert.StartsWith("Live edit batch (applied)", item.TextContent.Trim());
        Assert.Contains("var:count: 0 to 7 (actual after cascades)",
            item.QuerySelectorAll("ul > li").Select(change => change.TextContent));
    }

    [Fact]
    public void Preserve_state_jumps_name_their_target_and_bypass()
    {
        using var model = ComponentStory.Start();
        model.Target = "cave";
        model.JumpPreservingState();
        Assert.Null(model.Error);

        var item = Assert.Single(Show(model).FindAll("ol > li"));

        Assert.StartsWith("Preserve-state jump: cave (eligibility bypassed) (applied)", item.TextContent.Trim());
    }

    private IRenderedComponent<CommandList> Show(WorkbenchModel model) =>
        Render<CommandList>(parameters => parameters.Add(component => component.Session, model.Session!));
}
