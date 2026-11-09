using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class ScenarioTransferTests : BunitContext
{
    [Fact]
    public void Inputs_write_back_to_the_model()
    {
        using var model = ComponentStory.Start();
        var transfer = Show(model);

        transfer.Find("input[aria-label='Scenario file path']").Change("/tmp/run.twscenario");
        transfer.Find("input[type=checkbox]").Change(true);
        transfer.Find("input[aria-label='Player save file path']").Change("/tmp/player.twsave");

        Assert.Equal("/tmp/run.twscenario", model.ScenarioPath);
        Assert.True(model.ConfirmOverwrite);
        Assert.Equal("/tmp/player.twsave", model.PlayerSavePath);
    }

    [Fact]
    public void Each_button_raises_its_own_action()
    {
        using var model = ComponentStory.Start();
        var raised = new List<string>();
        var transfer = Render<ScenarioTransfer>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.OnExport, () => raised.Add("export"))
            .Add(component => component.OnImport, () => raised.Add("import"))
            .Add(component => component.OnAdopt, () => raised.Add("adopt"))
            .Add(component => component.OnPlayerSaveImport, () => raised.Add("player save")));

        Button(transfer, "Export synthetic scenario").Click();
        Button(transfer, "Import exact scenario").Click();
        Button(transfer, "Adopt scenario to accepted source").Click();
        Button(transfer, "Fork compatible player save").Click();

        Assert.Equal(["export", "import", "adopt", "player save"], raised);
    }

    [Fact]
    public void Export_needs_a_run_but_imports_do_not()
    {
        using var model = ComponentStory.Load();

        var transfer = Show(model);

        Assert.True(Button(transfer, "Export synthetic scenario").HasAttribute("disabled"));
        Assert.False(Button(transfer, "Import exact scenario").HasAttribute("disabled"));
        Assert.False(Button(transfer, "Adopt scenario to accepted source").HasAttribute("disabled"));
        Assert.False(Button(transfer, "Fork compatible player save").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Shows_the_scenario_status_only_when_there_is_one()
    {
        using var model = ComponentStory.Start();
        var transfer = Show(model);
        Assert.Empty(transfer.FindAll("p[role=status]"));

        // Importing a missing file is a real failure path that sets no status.
        model.ScenarioPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.twscenario");
        await model.ImportScenario();
        Assert.StartsWith("Scenario operation failed:", model.Error);
        Assert.Null(model.ScenarioStatus);
        transfer.Render();
        Assert.Empty(transfer.FindAll("p[role=status]"));

        var directory = Directory.CreateTempSubdirectory("talewright-scenario-");
        try
        {
            model.ScenarioPath = Path.Combine(directory.FullName, "run.twscenario");
            await model.ExportScenario();
            Assert.Null(model.Error);
            Assert.NotNull(model.ScenarioStatus);
            transfer.Render();

            Assert.Equal(model.ScenarioStatus, transfer.Find("p[role=status]").TextContent);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_player_save_fork_is_usable_alone()
    {
        using var model = ComponentStory.Load();
        var imports = 0;
        var fork = Render<PlayerSaveFork>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.OnImport, () => imports++));

        fork.Find("input[aria-label='Player save file path']").Change("save.twsave");
        fork.Find("button").Click();

        Assert.Equal("save.twsave", model.PlayerSavePath);
        Assert.Equal(1, imports);
        Assert.Equal("Fork compatible player save", fork.Find("button").TextContent);
    }

    private IRenderedComponent<ScenarioTransfer> Show(WorkbenchModel model) =>
        Render<ScenarioTransfer>(parameters => parameters.Add(component => component.Model, model));

    private static IElement Button(IRenderedComponent<ScenarioTransfer> transfer, string text) =>
        transfer.FindAll("button").Single(button => button.TextContent == text);
}
