using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public sealed class ShownChoiceRuntimeTests
{
    [Fact]
    public void Frame_keeps_available_choices_actionable_and_shown_entries_numbered_in_order()
    {
        var story = Compile("""
            === hub
            @hub shop
            @hub-limit 3
            * [Earn] { var:credits += 5 } -> hub
            * [Exit] -> END
            * [Restricted] (requires false, shown "Need {var:credits} credits") -> END
            * [Fallback label] (requires false, shown) -> END

            === available-high
            @title Available high
            @tags shop
            @priority 10
            -> END

            === available-low
            @title Available low
            @tags shop
            @priority 1
            -> END

            === unavailable-high
            @title Unavailable high
            @tags shop
            @priority 100
            @requires false, shown "Need {var:credits} from hub"
            -> END

            === unavailable-low
            @title Unavailable low
            @tags shop
            @priority 50
            @requires false, shown "Later"
            -> END
            """, """
            game: {id: shown-runtime, title: Shown runtime, start: hub}
            characters:
              player: {first-name: Ada}
            vars: {credits: 25}
            """);

        var history = new StoryHistory(story, 42);
        var frame = history.Current;

        Assert.Equal(
            ["Need 25 from hub", "Available high", "Available low", "Earn", "Exit", "Need 25 credits", "Fallback label"],
            frame.Menu.Select(entry => entry.Text));
        Assert.Equal(Enumerable.Range(1, frame.Menu.Count), frame.Menu.Select(entry => entry.Number));
        Assert.Equal(
            [false, true, true, true, true, false, false],
            frame.Menu.Select(entry => entry.IsAvailable));
        Assert.Equal(
            ["storylet:available-high", "storylet:available-low", "c0", "c1"],
            frame.Choices.Select(choice => choice.Key));
        Assert.DoesNotContain(frame.Menu, entry => entry.Key == "storylet:unavailable-low");

        var unavailable = Assert.Single(frame.Menu, entry => entry.Key == "c2");
        var eventCount = history.Events.Count;
        Assert.Throws<InvalidOperationException>(() => history.Choose(unavailable.Key));
        Assert.Equal(eventCount, history.Events.Count);
        Assert.Equal(0, history.Cursor);

        var session = new StorySession(story, 42);
        var sessionFrame = session.Start();
        var sessionEventCount = session.Events.Count;
        Assert.Throws<InvalidOperationException>(() => session.Choose(unavailable.Key));
        Assert.Equal(sessionEventCount, session.Events.Count);
        Assert.Equal(sessionFrame, session.Current);

        history.Choose("c0");
        Assert.Contains(history.Current.Menu, entry => entry.Key == "c2" && entry.Text == "Need 30 credits");
        Assert.Contains(history.Current.Menu, entry => entry.Key == "storylet:unavailable-high" && entry.Text == "Need 30 from hub");
    }

    [Fact]
    public void A_frame_with_only_shown_unavailable_choices_is_not_ended()
    {
        var story = Compile("""
            === start
            * [Restricted] (requires false, shown "Not yet") -> END
            """, """
            game: {id: shown-runtime, title: Shown runtime, start: start}
            characters:
              player: {first-name: Ada}
            """);

        var session = new StorySession(story, 42);
        var frame = session.Start();

        Assert.False(frame.IsEnded);
        Assert.Empty(frame.Choices);
        var entry = Assert.Single(frame.Menu);
        Assert.Equal(("c0", "Not yet", false), (entry.Key, entry.Text, entry.IsAvailable));
        Assert.Throws<InvalidOperationException>(() => session.Choose("c0"));
    }

    [Fact]
    public void Author_decision_reports_a_shown_unavailable_choice_requirement_as_false()
    {
        var story = Compile("""
            === start
            * [Restricted] (requires false, shown "Not yet") -> END
            """, """
            game: {id: shown-runtime, title: Shown runtime, start: start}
            characters:
              player: {first-name: Ada}
            """);

        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);

        var decision = Assert.Single(session.Decisions, entry => entry.Kind == DecisionKind.Choice);
        Assert.Equal(DecisionOutcome.Offered, decision.Outcome);
        Assert.Equal(DecisionReason.ConditionFalse, decision.Reason);
    }

    [Fact]
    public void Hub_decisions_distinguish_shown_unavailable_offers_and_apply_the_limit()
    {
        var story = Compile("""
            === hub
            @hub shop
            @hub-limit 2
            * [Leave] -> END

            === available
            @tags shop
            @priority 10
            -> END

            === hidden
            @tags shop
            @priority 100
            @requires false
            -> END

            === shown-selected
            @tags shop
            @priority 9
            @requires false, shown "Not yet"
            -> END

            === shown-limited
            @tags shop
            @priority 8
            @requires false, shown "Later"
            -> END
            """);

        var session = AuthorSession.Create(story, story.Start, captureDecisions: true, seed: 42);
        var allDecisions = session.Decisions.Where(decision => decision.Kind == DecisionKind.Storylet).ToArray();
        Assert.Equal(DecisionReason.SelfExcluded, Assert.Single(allDecisions, decision => decision.Subject == "hub").Reason);
        var decisions = allDecisions.Where(decision => decision.Subject != "hub").ToArray();

        Assert.Equal(4, decisions.Length);
        var available = Assert.Single(decisions, decision => decision.Subject == "available");
        Assert.Equal((DecisionOutcome.Selected, DecisionReason.PriorityOrder), (available.Outcome, available.Reason));
        Assert.Equal((10, 1, 2), (available.Priority, available.Rank, available.Limit));

        var hidden = Assert.Single(decisions, decision => decision.Subject == "hidden");
        Assert.Equal((DecisionOutcome.Excluded, DecisionReason.ConditionFalse), (hidden.Outcome, hidden.Reason));
        Assert.Equal((100, 2), (hidden.Priority, hidden.Limit));

        var shownSelected = Assert.Single(decisions, decision => decision.Subject == "shown-selected");
        Assert.Equal((DecisionOutcome.Offered, DecisionReason.ConditionFalse), (shownSelected.Outcome, shownSelected.Reason));
        Assert.False(Assert.IsType<ExpressionTrace>(shownSelected.Expression).Result?.Bool);
        Assert.Equal((9, 2), (shownSelected.Priority, shownSelected.Limit));

        var shownLimited = Assert.Single(decisions, decision => decision.Subject == "shown-limited");
        Assert.Equal((DecisionOutcome.Excluded, DecisionReason.HubLimit), (shownLimited.Outcome, shownLimited.Reason));
        Assert.False(Assert.IsType<ExpressionTrace>(shownLimited.Expression).Result?.Bool);
        Assert.Equal((8, 2), (shownLimited.Priority, shownLimited.Limit));
    }

    [Fact]
    public void Hub_limit_fills_available_scenes_before_shown_unavailable_and_once_scenes_stay_hidden()
    {
        var story = Compile("""
            === hub
            @hub shop
            @hub-limit 2
            * [Exit] -> END

            === once
            @tags shop
            @priority 10
            @once
            -> hub

            === available
            @tags shop
            @priority 1
            -> END

            === blocked
            @title Blocked
            @tags shop
            @priority 100
            @requires false, shown
            -> END
            """);

        var history = new StoryHistory(story, 42);
        Assert.Equal(
            ["storylet:once", "storylet:available", "c0"],
            history.Current.Menu.Select(entry => entry.Key));
        Assert.DoesNotContain(history.Current.Menu, entry => entry.Key == "storylet:blocked");

        history.Choose("storylet:once");
        history.Choose(StorySession.ContinueKey);

        Assert.Equal(
            ["storylet:blocked", "storylet:available", "c0"],
            history.Current.Menu.Select(entry => entry.Key));
        Assert.Equal("Blocked", Assert.Single(history.Current.Menu, entry => entry.Key == "storylet:blocked").Text);
        Assert.False(Assert.Single(history.Current.Menu, entry => entry.Key == "storylet:blocked").IsAvailable);
    }

    [Fact]
    public void Hub_choices_merge_by_priority_and_generated_limit_does_not_count_written_choices()
    {
        var story = Compile("""
            === hub
            @hub shop
            @hub-limit 4
            * [Written first] -> END
            * [Written second] -> END

            === generated-positive
            @tags shop
            @priority 5
            -> END

            === generated-zero-first
            @tags shop
            -> END

            === generated-zero-second
            @tags shop
            -> END

            === generated-negative
            @tags shop
            @priority -1
            -> END

            === generated-below-cap
            @tags shop
            @priority -2
            -> END
            """);

        var history = new StoryHistory(story, 42);

        Assert.Equal(
            ["storylet:generated-positive", "c0", "c1", "storylet:generated-zero-first",
                "storylet:generated-zero-second", "storylet:generated-negative"],
            history.Current.Menu.Select(entry => entry.Key));
        Assert.DoesNotContain(history.Current.Menu, entry => entry.Key == "storylet:generated-below-cap");
    }

    [Fact]
    public void Equal_zero_priorities_keep_written_choices_before_generated_choices()
    {
        var story = Compile("""
            === hub
            @hub shop
            * [Written first] -> END
            * [Written second] -> END

            === generated-first
            @tags shop
            @requires false, shown
            -> END

            === generated-second
            @tags shop
            -> END
            """);

        var history = new StoryHistory(story, 42);

        Assert.Equal(
            ["c0", "c1", "storylet:generated-first", "storylet:generated-second"],
            history.Current.Menu.Select(entry => entry.Key));
    }

    [Fact]
    public void Save_from_before_shown_was_added_restores_with_the_new_unavailable_menu_entry()
    {
        const string manifest = """
            game: {id: shown-save, title: Shown save, start: start}
            characters:
              player: {first-name: Ada}
            """;
        const string before = """
            === start
            * [Go] -> shop

            === shop
            * [Leave] -> END
            * [Restricted] (requires false) -> END
            """;
        const string after = """
            === start
            * [Go] -> shop

            === shop
            * [Leave] -> END
            * [Restricted] (requires false, shown "Not yet") -> END
            """;

        var options = new CompilerOptions { CompatibilityId = "shown-save-v1" };
        var original = new StoryHistory(Compile(before, manifest, options), 42);
        original.Choose("c0");
        var saved = original.Save();

        var restored = StoryHistory.Restore(Compile(after, manifest, options), saved);

        Assert.Equal(original.Events, restored.Events);
        Assert.Equal(1, restored.Cursor);
        Assert.Equal(["c0"], restored.Current.Choices.Select(choice => choice.Key));
        Assert.Equal(
            [("c0", "Leave", true), ("c1", "Not yet", false)],
            restored.Current.Menu.Select(entry => (entry.Key, entry.Text, entry.IsAvailable)));
    }

    private static StoryDefinition Compile(string tale, string? manifest = null, CompilerOptions? options = null)
    {
        manifest ??= """
            game: {id: shown-runtime, title: Shown runtime, start: hub}
            characters:
              player: {first-name: Ada}
            """;
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest,
            ["main.tale"] = tale,
        }), options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
