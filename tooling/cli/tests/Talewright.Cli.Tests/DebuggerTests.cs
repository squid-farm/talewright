using Talewright.Debugger;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;

namespace Talewright.Cli.Tests;

public sealed class DebuggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-debug-test-{Guid.NewGuid():N}");

    public DebuggerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "story.talewright.yaml"), """
            id: debug
            title: Debug
            start: scene1
            flags: [ready]
            vars: {count: 0}
            items:
              rope: {name: Rope}
            factions:
              guild: {name: Guild}
            characters:
              player: {first-name: Mara}
              guide: {first-name: Robin}
            """);
        File.WriteAllText(Path.Combine(_root, "main.tale"), string.Join("\n", Enumerable.Range(1, 1000).Select(i => $$"""
            === scene{{i}}
            @title Chapter {{i}}
            * [Prepared] (requires: flag:ready) -> END
            * [Wait] -> END
            """)));
    }

    [Fact]
    public void Scene_search_setup_and_inspection_work_without_playing_the_prefix()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        Assert.Null(model.Error);
        Assert.Equal("scene1000", Assert.Single(model.FindScenes("Chapter 1000")).Id.Value);
        model.Target = "scene1000";
        model.Seed = "42";
        var flag = Assert.Single(model.Flags);
        flag.Enabled = true;
        flag.Value = true;
        foreach (var field in model.Numbers)
        {
            if (field.Label is "guide:str" or "guide inventory:rope" or "var:count" or "faction:guild")
            {
                field.Enabled = true;
                field.Value = field.Kind == SetupNumberKind.Inventory ? "3" : "15";
            }
        }
        model.Start();
        Assert.Null(model.Error);
        Assert.Equal("scene1000", model.Session!.Current.Scene?.Value);
        Assert.Equal(0, model.Session.State.VisitCount(new("scene1")));
        Assert.Equal(15, model.Session.State.GetBaseStat(new("guide"), new("str")));
        Assert.Equal(3, model.Session.State.GetItemCount(new("guide"), new("rope")));
        Assert.Equal(15, model.Session.State.GetVariable(new("count")));
        Assert.Equal(15, model.Session.State.GetStanding(new("guild")));
        Assert.Equal(2, model.Session.Current.Choices.Count);
        Assert.True(model.Inspection!.DecisionsCaptured);
        model.Choose("c0");
        Assert.True(model.Session.Current.IsEnded);
    }

    [Fact]
    public void Live_fields_apply_atomically_navigate_jump_and_retain_commands_across_reload()
    {
        var path = Path.Combine(_root, "main.tale");
        const string tale = """
            === scene1
            Count {var:count}.
            * [Prepared] (requires: flag:ready) -> target
            * [Wait] -> scene1
            === target
            ~ var:count += 1
            Target {var:count}.
            * [Return] -> scene1
            """;
        File.WriteAllText(path, tale);
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        Assert.Equal("0", model.EditNumbers.Single(field => field.Label == "var:count").Value);
        var flag = Assert.Single(model.EditFlags);
        flag.Enabled = flag.Value = true;
        var field = model.EditNumbers.Single(field => field.Label == "var:count");
        field.Enabled = true;
        field.Value = "7";
        model.ApplyEdits();
        Assert.Null(model.Error);
        Assert.Equal(7, model.Session!.State.GetVariable(new("count")));
        Assert.Contains("Count 7.", model.Session.Current.Document.ToPlainText());
        Assert.Equal(2, model.Session.Current.Choices.Count);
        Assert.All(model.EditNumbers, field => Assert.False(field.Enabled));
        Assert.False(Assert.Single(model.EditFlags).Enabled);
        Assert.True(Assert.Single(model.EditFlags).Value);
        model.Target = "target";
        model.JumpPreservingState();
        Assert.Equal(8, model.Session.State.GetVariable(new("count")));
        var events = model.Session.Events;
        model.Back();
        Assert.Equal(7, model.Session.State.GetVariable(new("count")));
        Assert.Equal("7", model.EditNumbers.Single(field => field.Label == "var:count").Value);
        File.WriteAllText(path, tale.Replace("Target {var:count}.", "Revised {var:count}."));
        Assert.False(model.ApplySource(SourceCompilation.Load(_root)));
        Assert.Contains("redo commands", model.Error);
        Assert.False(model.RestartRequired);
        model.Forward();
        Assert.True(model.ApplySource(SourceCompilation.Load(_root)));
        Assert.Null(model.Error);
        Assert.Equal(events, model.Session!.Events);
        Assert.Equal(2, model.Session.Commands.Count);
        Assert.Contains("Revised 8.", model.Session.Current.Document.ToPlainText());
        Assert.Equal("8", model.EditNumbers.Single(field => field.Label == "var:count").Value);
        model.Reload();
        Assert.Null(model.Session);
        Assert.Empty(model.EditNumbers);
    }

    [Fact]
    public void Failed_live_inputs_keep_draft_and_active_timeline_while_fresh_start_is_distinct()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        var session = model.Session!;
        var frame = session.Current;
        var field = model.EditNumbers.Single(value => value.Label == "var:count");
        field.Enabled = true;
        field.Value = "not an integer";
        model.ApplyEdits();
        Assert.Contains("32-bit integer", model.Error);
        Assert.Same(frame, session.Current);
        Assert.Empty(session.Commands);
        Assert.True(field.Enabled);
        Assert.Equal("not an integer", field.Value);
        field.Value = "7";
        model.ApplyEdits();
        Assert.Null(model.Error);
        model.Target = "missing";
        model.JumpPreservingState();
        Assert.Contains("missing", model.Error);
        Assert.Single(session.Commands);
        model.Target = "scene1000";
        model.Start();
        Assert.Null(model.Error);
        Assert.Empty(model.Session!.Commands);
        Assert.Equal(0, model.Session.State.GetVariable(new("count")));
        Assert.Equal(0, model.Session.State.VisitCount(new("scene1")));
    }

    [Fact]
    public void Failed_setup_or_reload_preserves_the_last_good_playtest_and_recovery_is_explicit()
    {
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        var original = model.Session;
        model.Seed = "-1";
        model.Start();
        Assert.Contains("Seed", model.Error);
        Assert.Same(original, model.Session);
        model.Seed = "7";
        var number = model.Numbers[0];
        number.Enabled = true;
        number.Value = "999999999999";
        model.Start();
        Assert.Contains("32-bit", model.Error);
        Assert.Same(original, model.Session);
        number.Value = "1";
        model.Target = "missing";
        model.Start();
        Assert.Contains("missing", model.Error);
        Assert.Same(original, model.Session);
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== scene1\n-> missing");
        model.Reload();
        Assert.True(model.SourceRejected);
        Assert.Same(original, model.Session);
        Assert.NotEmpty(model.Diagnostics);
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== scene1\nRecovered.\n-> END");
        model.Target = "scene1";
        model.Reload();
        Assert.Null(model.Session);
        Assert.False(model.SourceRejected);
        Assert.Null(model.Error);
        model.Start();
        Assert.True(model.Session!.Current.IsEnded);
    }

    [Fact]
    public void Creation_selection_is_required_and_setup_overrides_only_enabled_fields()
    {
        File.AppendAllText(Path.Combine(_root, "story.talewright.yaml"), """

            creation:
              name: true
              stats:
                str: [8, 12, 16]
            """);
        var model = new WorkbenchModel(_root);
        model.Reload();
        model.Start();
        Assert.Contains("Create a character", model.Error);
        var story = model.Story!;
        model.SelectCharacter(story.Creation!.Default(story.World.Characters[EntityId.Player]) with
        {
            FirstName = "Alex", Stats = new Dictionary<string, int> { ["str"] = 16 },
        });
        model.Start();
        Assert.Null(model.Error);
        Assert.Equal(16, model.Session!.State.GetBaseStat(EntityId.Player, new("str")));
        Assert.Equal("Alex", model.Session.State.GetCharacterDefinition(EntityId.Player).FirstName);
        var stat = model.Numbers.Single(v => v.Label == "player:str");
        stat.Enabled = true;
        stat.Value = "12";
        model.Start();
        Assert.Equal(12, model.Session.State.GetBaseStat(EntityId.Player, new("str")));
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("debug", "folder", "--unknown")]
    [InlineData("debug", "folder", "--runtime", "linux-x64")]
    public void Invalid_invocations_use_existing_error_contract(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(args, output, error));
        Assert.Contains("TWCLI001", error.ToString());
    }

    [Fact]
    public void Debug_help_and_missing_directory_do_not_launch_native_host()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandLine.Run(["debug", "--help"], output, error));
        Assert.Contains("story-root", output.ToString());
        Assert.Equal(2, CommandLine.Run(["debug", Path.Combine(_root, "missing")], output, error));
        Assert.Contains("TWCLI002", error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
