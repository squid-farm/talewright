using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class OrderedSceneTests
{
    private static readonly string Manifest = BuildManifest().Build();

    private static ManifestFaker BuildManifest(bool includeArrived = false)
    {
        var manifest = new ManifestFaker("order", "Order", "start", 42) { Randomness = "off" };
        manifest.Character("player", "Ada");
        manifest.Number("gold", 10);
        manifest.Flag("ready");
        manifest.Flag("rewarded");
        manifest.Flag("broke");
        manifest.Item("rope", "Rope");
        manifest.Trigger("broke", "var:gold == 0", EffectFaker.Set(new TaleFlagRef("broke")));
        manifest.Trigger("reward", "flag:ready", EffectFaker.Set(new TaleFlagRef("rewarded")), once: true);
        if (includeArrived)
        {
            manifest.Trigger("arrived", "visited(start)", EffectFaker.Increase(new TaleVariableRef("gold"), 7), once: true);
        }

        return manifest;
    }

    private static StoryDefinition Compile(string tale, string? manifest = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest ?? Manifest,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static string Text(StoryFrame frame) => frame.Document.ToPlainText();

    [Fact]
    public void Every_prose_line_sees_only_earlier_effects_and_trigger_results()
    {
        var session = new StorySession(Compile("""
            === start
            Before {var:gold}.
            ~ var:gold += 1
            {if var:gold == 11}
            Middle {var:gold}.
            {/if}
            ~ set flag:ready; var:gold += 1
            After {var:gold}: {if flag:rewarded}rewarded{else}missing{/if}.
            -> END
            """));
        Assert.Equal("Before 10.\n\nMiddle 11.\n\nAfter 12: rewarded.", Text(session.Start()));
        Assert.Single(session.Events.OfType<TriggerFired>());
    }

    [Theory]
    [InlineData(false, 17)]
    [InlineData(true, 13)]
    public void Block_condition_is_decided_once_even_when_its_first_effect_changes_it(bool ready, int gold)
    {
        var setup = ready ? "~ set flag:ready\n" : "";
        var session = new StorySession(Compile($$"""
            === start
            {{setup}}{if not flag:ready}
            ~ set flag:ready
            ~ var:gold += 7
            Rewarded {var:gold}.
            {else}
            ~ clear flag:ready
            ~ var:gold += 3
            Alternate {var:gold}.
            {/if}
            -> END
            """));
        var frame = session.Start();
        Assert.Equal(gold, session.State.GetVariable(new("gold")));
        Assert.Contains(ready ? "Alternate 13." : "Rewarded 17.", Text(frame));
    }

    [Fact]
    public void Visit_trigger_results_are_visible_before_the_first_authored_line()
    {
        var story = Compile("=== start\nGold {var:gold}.\n* [Again] -> start",
            BuildManifest(includeArrived: true).Build());
        var session = new StorySession(story);
        Assert.Equal("Gold 17.", Text(session.Start()));
        Assert.Equal("Gold 17.", Text(session.Choose("c0")));
        Assert.Equal(2, session.State.VisitCount(new("start")));
        Assert.Single(session.Events.OfType<TriggerFired>());
    }

    [Theory]
    [InlineData("~ var:gold -= 10; var:gold += 7", false)]
    [InlineData("~ var:gold -= 10\n~ var:gold += 7", true)]
    public void Triggers_see_completed_authored_batches_not_intermediate_values(string effects, bool broke)
    {
        var session = new StorySession(Compile($"=== start\n{effects}\n{{if flag:broke}}Broke{{else}}Safe{{/if}}\n-> END"));
        Assert.Equal(broke ? "Broke" : "Safe", Text(session.Start()));
        Assert.Equal(7, session.State.GetVariable(new("gold")));
        Assert.Equal(broke, session.Events.OfType<TriggerFired>().Any(e => e.TriggerId == "broke"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Choice_and_check_outcome_effects_each_have_atomic_trigger_boundaries(bool check)
    {
        var action = check
            ? "* [Pay] skill-check athletics dc 1\nsuccess: { var:gold -= 10; var:gold += 7 } -> next\nfailure: -> next"
            : "* [Pay] { var:gold -= 10; var:gold += 7 } -> next";
        var session = new StorySession(Compile($"=== start\n{action}\n=== next\n{{if flag:broke}}Broke{{else}}Safe{{/if}}\n-> END"));
        session.Start();
        Assert.Contains("Safe", Text(session.Choose("c0")));
        Assert.Equal(7, session.State.GetVariable(new("gold")));
        Assert.DoesNotContain(session.Events, e => e is TriggerFired { TriggerId: "broke" });
    }

    [Theory]
    [InlineData(1, false, 12)]
    [InlineData(99, false, 9)]
    [InlineData(1, true, 12)]
    [InlineData(99, true, 9)]
    public void Mid_scene_check_applies_its_outcome_at_its_line_and_only_stops_with_a_target(int dc, bool divert, int gold)
    {
        var target = divert ? " -> next" : "";
        var session = new StorySession(Compile($$"""
            === start
            Before {var:gold}.
            ::: skill-check athletics dc {{dc}}
            success: { var:gold += 2 }{{target}}
            failure: { var:gold -= 1 }{{target}}
            :::
            After {var:gold}.
            * [Leave] -> next
            === next
            End.
            -> END
            """));
        var frame = session.Start();
        Assert.StartsWith("Before 10.", Text(frame));
        Assert.Equal(gold, session.State.GetVariable(new("gold")));
        Assert.Equal(divert, !Text(frame).Contains($"After {gold}.", StringComparison.Ordinal));
        Assert.Equal(divert ? "continue" : "c0", Assert.Single(frame.Choices).Key);
        Assert.Single(session.Events.OfType<SkillCheckResolved>());
    }

    [Theory]
    [InlineData(true, "continue", "early")]
    [InlineData(false, "c0", "later")]
    public void Conditional_divert_is_an_immediate_exit_before_the_final_choices(bool ready, string key, string next)
    {
        var session = new StorySession(Compile($$"""
            === start
            {{(ready ? "~ set flag:ready" : "")}}
            Before.
            {if flag:ready}
            -> early
            {/if}
            ~ var:gold += 1
            Later.
            * [Leave] -> later
            === early
            -> END
            === later
            -> END
            """));
        var frame = session.Start();
        Assert.Equal(ready ? 10 : 11, session.State.GetVariable(new("gold")));
        Assert.Equal(!ready, Text(frame).Contains("Later.", StringComparison.Ordinal));
        Assert.Equal(next, session.Choose(key).Scene!.Value.Value);
    }

    [Fact]
    public void Notes_are_inserted_where_each_batch_happens_and_keep_that_batchs_values()
    {
        var session = new StorySession(Compile("""
            === start
            Before.
            ~ give player rope; player:hp -= 2
            Middle.
            ~ take player rope; player:hp -= 1
            After.
            -> END
            """));
        var blocks = session.Start().Document.Blocks;
        Assert.Collection(blocks,
            block => Assert.IsType<RenderedParagraph>(block),
            block => Assert.Contains("Gained:", Assert.IsType<RenderedNote>(block).Text),
            block => Assert.Contains("(8)", Assert.IsType<RenderedNote>(block).Text),
            block => Assert.IsType<RenderedParagraph>(block),
            block => Assert.Contains("Lost:", Assert.IsType<RenderedNote>(block).Text),
            block => Assert.Contains("(7)", Assert.IsType<RenderedNote>(block).Text),
            block => Assert.IsType<RenderedParagraph>(block));
    }

    [Fact]
    public void Executor_collects_choices_at_their_line_even_for_a_model_with_a_later_effect()
    {
        var compiled = Compile("""
            === start
            ~ var:gold += 1
            * [Gold {var:gold}] (requires: var:gold == 10) -> END
            """);
        var scene = compiled.Scenes[0];
        var unordered = Copy(compiled, [scene.Steps[1], scene.Steps[0]]);
        var session = new StorySession(unordered);
        var frame = session.Start();
        Assert.Equal("Gold 10", Assert.Single(frame.Choices).Text);
        Assert.Equal(11, session.State.GetVariable(new("gold")));
        Assert.True(session.Choose("c0").IsEnded);
    }

    [Fact]
    public void Step_traces_have_source_spans_and_replay_through_checkpoints_and_history()
    {
        var story = Compile("""
            === start
            {if not flag:ready}
            ~ set flag:ready
            ~ var:gold += 7
            {/if}
            ::: skill-check athletics dc 1
            success: { var:gold += 1 }
            failure: { var:gold -= 1 }
            :::
            Gold {var:gold}.
            * [Again] -> start
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        session.Choose("c0");
        var events = session.Events.ToArray();
        var text = Text(session.Current);
        var inspection = session.Inspect();
        Assert.All(session.Decisions.Where(d => d.Kind != DecisionKind.Trigger), decision =>
        {
            Assert.StartsWith("step:", decision.Subject);
            var source = Assert.IsType<Talewright.Talemark.Analysis.SourceRange>(inspection.SourceFor(decision));
            Assert.Equal("main.tale", source.Start.File);
        });
        Assert.Equal(events, session.Checkpoint!.Reenter().Events);
        session.Back();
        session.Forward();
        Assert.Equal(events, session.Events);
        Assert.Equal(text, Text(session.Current));
        session.Back();
        var scenario = AuthorScenario.Export(session, "talewright-builtins-v1");
        var imported = AuthorScenario.Import(story, scenario, "talewright-builtins-v1").Session;
        Assert.Equal(session.Events, imported.Events);
        Assert.Equivalent(session.Decisions, imported.Decisions, strict: true);
        Assert.True(imported.CanForward);
        imported.Forward();
        Assert.Equal(events, imported.Events);
        Assert.Equal(text, Text(imported.Current));
        var history = new StoryHistory(story);
        history.Choose("c0");
        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(Text(history.Current), Text(restored.Current));
    }

    [Theory]
    [InlineData(false, DecisionReason.EarlierDivert)]
    [InlineData(true, DecisionReason.EntryCheck)]
    public void Skipped_step_reason_follows_the_actual_exit_through_nested_blocks(bool checkExits, DecisionReason reason)
    {
        var target = checkExits ? " -> next" : "";
        var story = Compile($$"""
            === start
            {if true}
            ::: skill-check athletics dc 1
            success:{{target}}
            failure:{{target}}
            :::
            {/if}
            -> next
            * [Skipped] -> END
            === next
            -> END
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        Assert.Equal(reason, Assert.Single(session.Decisions, decision => decision.Kind == DecisionKind.Choice).Reason);
    }

    [Fact]
    public void Live_edit_keeps_a_guard_decided_before_its_own_effect_and_refreshes_later_text_and_choices()
    {
        var story = Compile("""
            === start
            {if not flag:ready}
            ~ set flag:ready
            You take the relic.
            {/if}
            Gold {var:gold}.
            * [Spend] (requires: var:gold > 10) -> start
            * [Wait] -> start
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        Assert.Equal(["Wait"], session.Current.Choices.Select(choice => choice.Text));
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("gold")] = 20 } });
        var text = Text(session.Current);
        Assert.Contains("You take the relic.", text);
        Assert.Contains("Gold 20.", text);
        Assert.Equal(["Spend", "Wait"], session.Current.Choices.Select(choice => choice.Text));
        Assert.Single(session.Decisions, decision => decision.Kind == DecisionKind.Branch);
    }

    [Fact]
    public void Live_edit_does_not_rewrite_text_shown_before_an_effect()
    {
        var story = Compile("""
            === start
            Before {var:gold}.
            ~ var:gold += 1
            After {var:gold}.
            * [Wait] -> start
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        session.Edit(new() { Variables = new Dictionary<VarId, int> { [new("gold")] = 50 } });
        var text = Text(session.Current);
        Assert.StartsWith("Before 10.", text);
        Assert.Contains("After 50.", text);
        Assert.DoesNotContain("Before 50.", text);
    }

    [Fact]
    public void Live_edit_rejects_newly_reached_effects_atomically()
    {
        var story = Compile("""
            === start
            {if flag:ready}
            ~ var:gold += 5
            {/if}
            * [Wait] -> start
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        var before = session.Events.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => session.Edit(new()
        {
            Flags = new Dictionary<FlagId, bool> { [new("ready")] = true },
        }));
        Assert.Contains("did not run in this passage", error.Message);
        Assert.Equal(before, session.Events);
        Assert.Equal(10, session.State.GetVariable(new("gold")));
    }

    [Fact]
    public void Live_edit_rejects_newly_reached_checks_atomically()
    {
        var story = Compile("""
            === start
            {if flag:ready}
            ::: skill-check athletics dc 1
            success: { var:gold += 1 }
            failure: { var:gold -= 1 }
            :::
            {/if}
            * [Wait] -> start
            """);
        var session = AuthorSession.Create(story, story.Start, captureDecisions: true);
        var before = session.Events.ToArray();
        var frame = session.Current;
        var error = Assert.Throws<InvalidOperationException>(() => session.Edit(new()
        {
            Flags = new Dictionary<FlagId, bool> { [new("ready")] = true },
        }));
        Assert.Contains("did not run in this passage", error.Message);
        Assert.Equal(before, session.Events);
        Assert.Same(frame, session.Current);
        Assert.Equal(0, session.State.RollCount);
    }

    [Fact]
    public void A_divert_does_not_become_a_fallback_in_an_arbitrarily_ordered_model()
    {
        var compiled = Compile("=== start\n~ var:gold += 1\n* [Leave] -> END");
        var steps = compiled.Scenes[0].Steps;
        var session = new StorySession(Copy(compiled,
            [steps[1], new DivertStep("step:4:1", steps[0].Source, DivertTarget.End), steps[0]]));
        var frame = session.Start();
        Assert.True(frame.IsEnded);
        Assert.Empty(frame.Choices);
        Assert.Equal(10, session.State.GetVariable(new("gold")));
    }

    [Fact]
    public void Source_only_reload_updates_step_locations_without_changing_consumed_meaning()
    {
        const string tale = "=== start\nBefore {var:gold}.\n~ var:gold += 1\n* [Again] -> start";
        var session = AuthorSession.Create(Compile(tale), new("start"), captureDecisions: true);
        session.Choose("c0");
        var candidate = session.Reload(Compile("\n" + tale));
        Assert.Equal(session.Events, candidate.Events);
        Assert.Equal(Text(session.Current), Text(candidate.Current));
        var choice = candidate.Decisions.Last(decision => decision.Kind == DecisionKind.Choice);
        Assert.Equal("step:5:1", choice.Subject);
        Assert.Equal(5, candidate.Inspect().SourceFor(choice)!.Start.Line);
    }

    private static StoryDefinition Copy(StoryDefinition story, IReadOnlyList<SceneStep> steps) => new()
    {
        Id = story.Id, Title = story.Title, Version = story.Version, Start = story.Start,
        Randomness = story.Randomness, Seed = story.Seed, World = story.World,
        Triggers = story.Triggers, Predicates = story.Predicates,
        Scenes = [new SceneDefinition { Id = story.Start, Steps = steps }],
    };
}
