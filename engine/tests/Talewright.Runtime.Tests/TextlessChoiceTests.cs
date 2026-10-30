using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public sealed class TextlessChoiceTests
{
    private const string Tale = """
        === start
        Visits {var:visits}.
        * [Open] (requires: flag:ready) -> final
        * -> road
        === road
        The road.
        * (requires: not flag:ready) { set flag:ready; var:visits += 1 } -> start
        * [Stop] -> final
        === final
        The end.
        * -> END
        """;

    private static StoryDefinition Compile(string tale = Tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: {id: textless, title: Textless, start: start, randomness: off, seed: 42}
                characters:
                  player: {first-name: Ada}
                flags: [ready]
                vars: {visits: 0}
                """,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Fact]
    public void Textless_and_synthetic_continuations_share_the_label_but_not_the_choice_key()
    {
        var textless = new StorySession(Compile("=== start\n* -> road\n=== road\n-> END")).Start();
        var synthetic = new StorySession(Compile("=== start\n-> road\n=== road\n-> END")).Start();
        var textlessChoice = Assert.Single(textless.Choices);
        var syntheticChoice = Assert.Single(synthetic.Choices);
        Assert.Equal("c0", textlessChoice.Key);
        Assert.Equal(StorySession.ContinueKey, syntheticChoice.Key);
        Assert.Equal("Continue", textlessChoice.Text);
        Assert.Equal(syntheticChoice.Label, textlessChoice.Label);

        var empty = new StorySession(Compile("=== start\n* [] -> END")).Start();
        Assert.Equal("", Assert.Single(empty.Choices).Text);
    }

    [Fact]
    public void Textless_choices_follow_requirements_order_and_effects_before_reaching_end()
    {
        var session = new StorySession(Compile());
        var first = session.Start();
        Assert.Equal([("c1", "Continue")], first.Choices.Select(choice => (choice.Key, choice.Text)));
        Assert.False(session.State.GetFlag(new FlagId("ready")));

        var road = session.Choose("c1");
        Assert.Equal([("c0", "Continue"), ("c1", "Stop")], road.Choices.Select(choice => (choice.Key, choice.Text)));
        Assert.False(session.State.GetFlag(new FlagId("ready")));
        var returned = session.Choose("c0");
        Assert.True(session.State.GetFlag(new FlagId("ready")));
        Assert.Equal(1, session.State.GetVariable(new VarId("visits")));
        Assert.Equal([("c0", "Open"), ("c1", "Continue")], returned.Choices.Select(choice => (choice.Key, choice.Text)));

        var final = session.Choose("c0");
        Assert.Equal("Continue", Assert.Single(final.Choices).Text);
        Assert.True(session.Choose("c0").IsEnded);
        Assert.Equal(["c1", "c0", "c0", "c0"], session.Events.OfType<ChoiceMade>().Select(choice => choice.ChoiceKey));
    }

    [Fact]
    public void Save_replay_back_and_forward_preserve_textless_choice_keys_labels_and_effects()
    {
        var story = Compile();
        var history = new StoryHistory(story, 42);
        history.Choose("c1");
        history.Choose("c0");
        history.Back();
        var restored = StoryHistory.Restore(story, history.Save());

        Assert.True(restored.CanForward);
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal("Continue", RenderedInline.ToPlainText(
            Assert.IsAssignableFrom<IReadOnlyList<RenderedInline>>(restored.Transcript[0].Chosen)));
        Assert.Equal([("c0", "Continue"), ("c1", "Stop")],
            restored.Current.Choices.Select(choice => (choice.Key, choice.Text)));
        restored.Forward();
        Assert.Equal(1, restored.State.GetVariable(new VarId("visits")));
        Assert.True(restored.State.GetFlag(new FlagId("ready")));
        Assert.Equal(["Continue", "Continue"],
            restored.Transcript.Take(2).Select(frame => RenderedInline.ToPlainText(
                Assert.IsAssignableFrom<IReadOnlyList<RenderedInline>>(frame.Chosen))));
        restored.Choose("c0");
        restored.Choose("c0");
        Assert.True(restored.Current.IsEnded);
        Assert.Equal(["c1", "c0", "c0", "c0"], restored.Events.OfType<ChoiceMade>().Select(choice => choice.ChoiceKey));
    }

    [Fact]
    public void Debugger_inspection_reload_and_scenario_replay_handle_textless_choices()
    {
        var story = Compile();
        var author = AuthorSession.Create(story, new SceneId("start"), captureDecisions: true, seed: 42);
        Assert.Equal("Continue", Assert.Single(author.Current.Choices).Text);
        Assert.Contains(author.Decisions, decision => decision.Kind == DecisionKind.Choice
            && decision.Subject == "step:4:1" && decision.Outcome == DecisionOutcome.Offered);

        author.Choose("c1");
        var updatedStory = Compile(Tale.Replace("The road.", "The updated road.", StringComparison.Ordinal));
        var reloaded = author.Reload(updatedStory);
        Assert.Contains("The updated road.", reloaded.Current.Document.ToPlainText());
        Assert.Equal("Continue", reloaded.Current.Choices[0].Text);
        var bytes = AuthorScenario.Export(reloaded, "talewright-builtins-v1");
        var replay = AuthorScenario.Import(updatedStory, bytes, "talewright-builtins-v1").Session;
        Assert.Equal(reloaded.Events, replay.Events);
        Assert.Equal(reloaded.Current.Choices.Select(choice => (choice.Key, choice.Text)),
            replay.Current.Choices.Select(choice => (choice.Key, choice.Text)));
        replay.Choose("c0");
        Assert.True(replay.State.GetFlag(new FlagId("ready")));
    }
}
