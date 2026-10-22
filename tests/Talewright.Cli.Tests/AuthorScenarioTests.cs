using System.Text.Json.Nodes;
using Talewright.Core.Identity;
using Talewright.Debugger;
using Talewright.Runtime;
using Talewright.Runtime.Saves;

namespace Talewright.Cli.Tests;

public sealed class AuthorScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-scenario-test-{Guid.NewGuid():N}");
    private readonly string _story;
    private readonly string _output;

    public AuthorScenarioTests()
    {
        _story = Path.Combine(_root, "story");
        _output = Path.Combine(_root, "author", "portable.twscenario");
        Directory.CreateDirectory(_story);
        File.WriteAllText(Path.Combine(_story, "story.yaml"), """
            id: scenario-ui
            title: Portable UI
            start: start
            flags: [ready]
            vars: {count: 0}
            characters:
              player: {first-name: Tester}
            """);
        File.WriteAllText(Path.Combine(_story, "main.tale"), """
            === start
            Count {var:count}.
            * [Next] -> target
            === target
            Target {var:count}.
            * [Return] -> start
            """);
    }

    private WorkbenchModel Start()
    {
        var model = new WorkbenchModel(_story);
        model.Reload();
        model.Seed = "42";
        var field = model.Numbers.Single(field => field.Id == "count" && field.Kind == SetupNumberKind.Variable);
        field.Enabled = true;
        field.Value = "7";
        model.Start();
        Assert.Null(model.Error);
        model.ScenarioPath = _output;
        return model;
    }

    [Fact]
    public async Task Files_roundtrip_preserves_redo_origin_fields_source_map_and_isolated_storage()
    {
        var model = Start();
        model.Session!.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 19 } });
        model.Choose("c0");
        model.Back();
        var expected = model.Session.Events;
        await model.ExportScenario();
        Assert.Null(model.Error);
        var bytes = await File.ReadAllBytesAsync(_output);
        var source = await File.ReadAllBytesAsync(Path.Combine(_story, "main.tale"));
        model.Start();
        await model.ImportScenario();
        Assert.Null(model.Error);
        Assert.Contains("Exact synthetic scenario", model.ScenarioStatus);
        Assert.Equal(expected, model.Session!.Events);
        Assert.True(model.Session.CanForward);
        Assert.Equal("7", model.Numbers.Single(field => field.Id == "count" && field.Kind == SetupNumberKind.Variable).Value);
        Assert.Equal("19", model.EditNumbers.Single(field => field.Id == "count" && field.Kind == SetupNumberKind.Variable).Value);
        model.Forward();
        Assert.Equal("target", model.CurrentSource!.Scene.Value);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_output));
        Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(_story, "main.tale")));
        Assert.DoesNotContain(_story, AuthorScenarioFiles.DefaultDirectory(model.Story!.Id));
        Assert.DoesNotContain(FileSaveStore.DefaultRoot + Path.DirectorySeparatorChar, AuthorScenarioFiles.DefaultDirectory(model.Story.Id));
    }

    [Fact]
    public async Task Existing_export_requires_one_shot_confirmation_and_failures_keep_file_and_run()
    {
        var model = Start();
        await model.ExportScenario();
        var bytes = await File.ReadAllBytesAsync(_output);
        model.Choose("c0");
        var session = model.Session;
        await model.ExportScenario();
        Assert.Contains("Scenario operation failed", model.Error);
        Assert.Null(model.ScenarioStatus);
        Assert.Same(session, model.Session);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_output));
        model.ConfirmOverwrite = true;
        await model.ExportScenario();
        Assert.Null(model.Error);
        Assert.False(model.ConfirmOverwrite);
        Assert.NotEqual(bytes, await File.ReadAllBytesAsync(_output));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_output)!));
        await model.ExportScenario();
        Assert.NotNull(model.Error);
    }

    [Fact]
    public async Task Import_failure_preserves_every_active_surface_and_input_bytes()
    {
        var model = Start();
        await model.ExportScenario();
        var data = JsonNode.Parse(await File.ReadAllBytesAsync(_output))!;
        data["commands"] = JsonNode.Parse("""[{"kind":"jump","target":"removed"}]""");
        await File.WriteAllTextAsync(_output, data.ToJsonString());
        var bad = await File.ReadAllBytesAsync(_output);
        var session = model.Session;
        var inspection = model.Inspection;
        var source = model.CurrentSource;
        var numbers = model.Numbers;
        var edits = model.EditNumbers;
        model.Target = "draft-target";
        model.Seed = "draft-seed";
        await model.ImportScenario();
        Assert.Contains("removed", model.Error);
        Assert.Same(session, model.Session);
        Assert.Same(inspection, model.Inspection);
        Assert.Equal(source, model.CurrentSource);
        Assert.Same(numbers, model.Numbers);
        Assert.Same(edits, model.EditNumbers);
        Assert.Equal("draft-target", model.Target);
        Assert.Equal("draft-seed", model.Seed);
        Assert.Equal(bad, await File.ReadAllBytesAsync(_output));
    }

    [Fact]
    public async Task Explicit_adoption_reports_changes_and_preserves_original_scenario()
    {
        var model = Start();
        await model.ExportScenario();
        var bytes = await File.ReadAllBytesAsync(_output);
        var path = Path.Combine(_story, "main.tale");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Count ", "Changed count "));
        Assert.True(model.ApplySource(SourceCompilation.Load(_story)));
        var session = model.Session;
        await model.ImportScenario();
        Assert.NotNull(model.Error);
        Assert.Same(session, model.Session);
        await model.AdoptScenario();
        Assert.Null(model.Error);
        Assert.Contains("New synthetic scenario", model.ScenarioStatus);
        Assert.Contains("diverged", model.ScenarioStatus);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_output));
        model.ScenarioPath = Path.Combine(_root, "adopted.twscenario");
        await model.ExportScenario();
        await model.ImportScenario();
        Assert.Null(model.Error);
    }

    [Fact]
    public async Task Player_save_fork_is_read_only_and_must_pass_normal_compatibility()
    {
        var model = Start();
        var save = new StoryHistory(model.Story!, 42);
        save.Choose("c0");
        save.Back();
        var bytes = save.Save();
        model.PlayerSavePath = Path.Combine(_root, "player.json");
        await File.WriteAllBytesAsync(model.PlayerSavePath, bytes);
        await model.ImportPlayerSave();
        Assert.Null(model.Error);
        Assert.True(model.Session!.IsSynthetic);
        Assert.True(model.Session.CanForward);
        Assert.Empty(model.Session.Setup.Variables);
        model.Forward();
        model.Session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("count")] = 44 } });
        await model.ExportScenario();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(model.PlayerSavePath));
        var session = model.Session;
        var malformed = JsonNode.Parse(bytes)!;
        malformed["contentFingerprint"] = "other-source";
        await File.WriteAllTextAsync(model.PlayerSavePath, malformed.ToJsonString());
        await model.ImportPlayerSave();
        Assert.NotNull(model.Error);
        Assert.Same(session, model.Session);
    }

    [Fact]
    public async Task Export_rejects_story_and_player_locations_including_symlinked_parents()
    {
        var model = Start();
        foreach (var path in new[]
        {
            Path.Combine(_story, "main.tale"),
            Path.Combine(_story, "nested", "test.twscenario"),
            Path.Combine(_root, "player.json"),
            Path.Combine(FileSaveStore.DefaultRoot, "never-written.twscenario"),
        })
        {
            model.ScenarioPath = path;
            model.ConfirmOverwrite = true;
            await model.ExportScenario();
            Assert.NotNull(model.Error);
        }
        var link = Path.Combine(_root, "link");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, _story);
            model.ScenarioPath = Path.Combine(link, "test.twscenario");
            await model.ExportScenario();
            Assert.Contains("outside", model.Error);
            Assert.False(File.Exists(Path.Combine(_story, "test.twscenario")));
            var playerCopy = Path.Combine(_root, "player-copy.json");
            var originalSave = new StoryHistory(model.Story!, 42).Save();
            await File.WriteAllBytesAsync(playerCopy, originalSave);
            var fileLink = Path.Combine(_root, "disguised.twscenario");
            File.CreateSymbolicLink(fileLink, playerCopy);
            model.ScenarioPath = fileLink;
            model.ConfirmOverwrite = true;
            await model.ExportScenario();
            Assert.Contains("non-scenario file", model.Error);
            Assert.Equal(originalSave, await File.ReadAllBytesAsync(playerCopy));
        }
        Assert.False(Directory.Exists(Path.Combine(_story, "nested")));
    }

    [Fact]
    public async Task Bounded_file_reads_reject_empty_and_over_limit_without_replacing_active_run()
    {
        var model = Start();
        var session = model.Session;
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        foreach (var size in new[] { 0, AuthorScenario.MaxBytes + 1 })
        {
            await using (var file = File.Create(_output)) { file.SetLength(size); }
            await model.ImportScenario();
            Assert.Contains("2 MiB", model.Error);
            Assert.Same(session, model.Session);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
