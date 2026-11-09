using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;
using Talewright.Player.Components;
using Talewright.Talemark.Model;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SetupToolTests : BunitContext
{
    [Fact]
    public void Picking_a_scene_updates_the_target_field()
    {
        using var model = ComponentStory.Load();
        var tool = Show(model);
        Assert.Equal("hall", Target(tool).GetAttribute("value"));

        tool.FindAll("button").Single(button => button.TextContent == "Select cave Cave").Click();

        Assert.Equal("cave", model.Target);
        Assert.Equal("cave", Target(tool).GetAttribute("value"));
    }

    [Fact]
    public void Target_and_seed_inputs_write_back_to_the_model()
    {
        using var model = ComponentStory.Load();
        var tool = Show(model);

        Target(tool).Change("cave");
        tool.Find("input[aria-label='Seed']").Change("42");

        Assert.Equal("cave", model.Target);
        Assert.Equal("42", model.Seed);
        Assert.Same(model.Numbers, tool.FindComponent<SetupFields>().Instance.Numbers);
    }

    [Fact]
    public void Start_raises_its_action()
    {
        using var model = ComponentStory.Load();
        var starts = 0;
        var tool = Show(model, start: () => starts++);

        Button(tool, "Start selected scene").Click();

        Assert.Equal(1, starts);
    }

    [Fact]
    public void A_story_without_character_creation_has_no_creator()
    {
        using var model = ComponentStory.Load();

        var tool = Show(model);

        Assert.Empty(tool.FindComponents<CharacterCreator>());
        Assert.DoesNotContain(tool.FindAll("button"), button => button.TextContent == "Clear character selection");
        Assert.Empty(tool.FindAll("p[role=status]"));
    }

    [Fact]
    public async Task Character_creation_asks_for_a_character_until_one_is_selected()
    {
        using var model = ComponentStory.Load(creation: true);
        var clears = 0;
        var tool = Show(model, clear: () => clears++);

        var creator = tool.FindComponent<CharacterCreator>().Instance;
        Assert.Same(model.Story, creator.Story);
        Assert.Null(creator.InitialSelection);
        Assert.Equal("Create a character before starting the selected scene. Start story above confirms the character only.",
            tool.Find("p[role=status]").TextContent);

        Button(tool, "Clear character selection").Click();
        Assert.Equal(1, clears);

        var character = new CharacterSelection { FirstName = "Bo", LastName = "", Stats = new Dictionary<string, int> { ["str"] = 12 } };
        await tool.InvokeAsync(() => creator.OnCreated.InvokeAsync(character));

        Assert.Null(model.Error);
        Assert.Equal(character.FirstName, model.Character!.FirstName);
        Assert.Equal(character.LastName, model.Character.LastName);
        Assert.Equal(character.Stats, model.Character.Stats);

        Assert.Equal("Character selected. Start the selected scene below.", tool.Find("p[role=status]").TextContent);
        Assert.Equal(model.Character, tool.FindComponent<CharacterCreator>().Instance.InitialSelection);
    }

    [Fact]
    public void Clearing_the_character_replaces_the_creator()
    {
        using var model = ComponentStory.Load(creation: true);
        model.SelectCharacter(new CharacterSelection { FirstName = "Bo", LastName = "", Stats = new Dictionary<string, int> { ["str"] = 12 } });
        Assert.Null(model.Error);
        var tool = Show(model, clear: model.ClearCharacter);
        var creator = tool.FindComponent<CharacterCreator>().Instance;

        Button(tool, "Clear character selection").Click();
        // The owning Workbench rerenders after its clear handler; stand in for that here.
        tool.Render();

        // CreationRevision keys the creator, so stale form state cannot survive a clear.
        Assert.NotSame(creator, tool.FindComponent<CharacterCreator>().Instance);
        Assert.Null(tool.FindComponent<CharacterCreator>().Instance.InitialSelection);
        Assert.StartsWith("Create a character before starting", tool.Find("p[role=status]").TextContent);
    }

    private IRenderedComponent<SetupTool> Show(WorkbenchModel model, Action? start = null, Action? clear = null) =>
        Render<SetupTool>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.Story, model.Story!)
            .Add(component => component.OnStart, start ?? (() => { }))
            .Add(component => component.OnClearCharacter, clear ?? (() => { })));

    private static IElement Target(IRenderedComponent<SetupTool> tool) => tool.Find("input[aria-label='Target scene ID']");

    private static IElement Button(IRenderedComponent<SetupTool> tool, string text) =>
        tool.FindAll("button").Single(button => button.TextContent == text);
}
