using System.Text;
using System.Text.Json.Nodes;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public class TextAuthorScenarioTests
{
    private const string Identity = "talewright-builtins-v1";
    private static StoryDefinition Story(bool implicitDeclarations)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                id: text-scenarios
                title: Text scenarios
                start: a
                characters:
                  player: { first-name: Ada }
                vars:
                  count: 0
                """ + (implicitDeclarations ? "" : "\n  weather: \"sunny\"\n  copy: \"\""),
            ["main.tale"] = """
                === a
                Weather [{var:weather}]. Copy [{var:copy}].
                * [Copy] { var:copy = var:weather; var:weather = "later" } -> b
                === b
                Weather [{var:weather}]. Copy [{var:copy}].
                * [Return] -> a
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_setup_edits_inspection_and_scenarios_replay_exactly_without_touching_player(bool implicitDeclarations)
    {
        var story = Story(implicitDeclarations);
        var player = new StoryHistory(story, 42);
        var playerBytes = player.Save();
        var values = new Dictionary<VarId, string> { [new("weather")] = "12", [new("copy")] = "" };
        var session = AuthorSession.Create(story, new("a"), captureDecisions: true,
            setup: new() { TextVariables = values }, seed: 42);
        values[new("weather")] = "not adopted";
        Assert.NotNull(session.Checkpoint);
        Assert.Equal("12", session.Checkpoint.State.GetTextVariable(new("weather")));
        var snapshot = session.Inspect();
        Assert.Contains(snapshot.TextVariables, pair => pair.Key == new VarId("copy") && pair.Value == "");
        session.Edit(new() { TextVariables = new Dictionary<VarId, string> { [new("weather")] = "雪\n \"quoted\" \\ path" } });
        session.Choose("c0");
        session.Back();
        var bytes = AuthorScenario.Export(session, Identity);
        var copy = AuthorScenario.Import(story, bytes, Identity).Session;
        Assert.Equal(session.Events, copy.Events);
        Assert.Equal(session.Current.Document.ToPlainText(), copy.Current.Document.ToPlainText());
        Assert.Equal(session.Inspect().TextVariables, copy.Inspect().TextVariables);
        Assert.Equal("12", snapshot.TextVariables.Single(pair => pair.Key == new VarId("weather")).Value);
        Assert.True(copy.CanForward);
        session.Forward();
        copy.Forward();
        Assert.Equal(session.Events, copy.Events);
        Assert.Equal("雪\n \"quoted\" \\ path", copy.State.GetTextVariable(new("copy")));
        Assert.Equal(AuthorScenario.Export(session, Identity), AuthorScenario.Export(copy, Identity));
        copy.Edit(new() { TextVariables = new Dictionary<VarId, string> { [new("weather")] = "" } });
        Assert.Equal("", copy.State.GetTextVariable(new("weather")));
        Assert.Equal("later", session.State.GetTextVariable(new("weather")));
        Assert.Equal(playerBytes, player.Save());
    }

    [Fact]
    public void Invalid_typed_setup_is_explicit_and_does_not_mutate_an_active_session()
    {
        var story = Story(false);
        var session = AuthorSession.Create(story, new("a"));
        var frame = session.Current;
        var events = session.Events;
        foreach (var setup in new AuthorSetup[]
        {
            new() { Variables = new Dictionary<VarId, int> { [new("weather")] = 1 } },
            new() { TextVariables = new Dictionary<VarId, string> { [new("count")] = "1" } },
            new() { TextVariables = new Dictionary<VarId, string> { [new("weather")] = null! } },
            new() { TextVariables = new Dictionary<VarId, string> { [new("unknown")] = "" } },
        })
        {
            Assert.Throws<ArgumentException>(() => session.Edit(setup));
            Assert.Same(frame, session.Current);
            Assert.Equal(events, session.Events);
            Assert.Empty(session.Commands);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Invalid_serialized_text_values_are_rejected_before_adoption(string invalid)
    {
        var story = Story(false);
        var active = AuthorSession.Create(story, new("a"));
        var before = active.Events;
        var json = JsonNode.Parse(AuthorScenario.Export(active, Identity))!;
        json["setup"]!["textVariables"]!["weather"] = JsonNode.Parse(invalid);
        Assert.Throws<InvalidDataException>(() =>
            AuthorScenario.Import(story, Encoding.UTF8.GetBytes(json.ToJsonString()), Identity));
        Assert.Equal(before, active.Events);
        Assert.Empty(active.Commands);
    }
}
