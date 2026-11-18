using System.Text;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class StoryHistoryTests
{
    private const int ReplayChoiceIdLength = 8;
    private static readonly string Yaml = BuildManifest().Build();
    private static readonly string Tale = BuildTale();

    private static ManifestFaker BuildManifest(bool declareVariables = false, bool? undo = null,
        bool saveTrigger = false, bool multipleSavingTriggers = false, bool repeatedSavingTrigger = false)
    {
        var manifest = new ManifestFaker("history", "History", "a")
        {
            Version = "1",
            Undo = undo,
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Tester", stats: new Dictionary<string, int> { ["athletics"] = 2 });
        if (declareVariables)
        {
            manifest.Number("visits", 0);
            manifest.Number("trigger-count", 0);
        }

        manifest.Trigger("first", "var:visits >= 1", EffectFaker.Increase(new TaleVariableRef("trigger-count"), 1),
            once: true, save: saveTrigger);
        if (multipleSavingTriggers)
        {
            manifest.Trigger("second", "var:visits >= 1", EffectFaker.Increase(new TaleVariableRef("trigger-count"), 1),
                once: true, save: true);
        }
        if (repeatedSavingTrigger)
        {
            manifest.Trigger("repeat", "var:visits >= 2", EffectFaker.Increase(new TaleVariableRef("trigger-count"), 1),
                save: true);
        }
        return manifest;
    }

    private static string BuildTale() => string.Join('\n',
        SceneFaker.Header("a"),
        EffectFaker.Line(EffectFaker.Increase(new TaleVariableRef("visits"), 1)),
        $"Visits: {SubstitutionFaker.Variable(new TaleVariableRef("visits"))}; triggers: {SubstitutionFaker.Variable(new TaleVariableRef("trigger-count"))}.",
        SkillCheckFaker.Choice("Try", "athletics", 12, "a", "a",
            EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 1),
            EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 2)),
        ChoiceFaker.Create("END", "Finish"));

    private static string BareDivertChain(int firstTransitions, int? secondTransitions = null,
        string finishChoice = "Finish", bool checkBoundary = false)
    {
        var finalIndex = firstTransitions + (secondTransitions is null ? 0 : secondTransitions.Value + 1);
        var tale = new StringBuilder();
        for (var index = 0; index <= finalIndex; index++)
        {
            var scene = index == 0 ? "a" : $"scene-{index}";
            tale.AppendLine($"=== {scene}");
            if (secondTransitions is not null && index == firstTransitions)
            {
                var target = $"scene-{index + 1}";
                tale.AppendLine(checkBoundary
                    ? SkillCheckFaker.Entry("athletics", 1, successTarget: target, failureTarget: target)
                    : ChoiceFaker.Create(target, "Again"));
            }
            else if (index < finalIndex)
            {
                tale.AppendLine($"-> scene-{index + 1}");
            }
            else
            {
                tale.AppendLine(ChoiceFaker.Create("END", finishChoice));
            }
        }
        return tale.ToString();
    }

    private static StoryDefinition Compile(string? yaml = null, string? tale = null, CompilerOptions? options = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml ?? Yaml, ["main.tale"] = tale ?? Tale,
        }), options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Fact]
    public void Optional_summary_preserves_replay_and_progress_identity()
    {
        var history = new StoryHistory(Compile(), 42);
        history.Choose("c0");
        history.Back();
        var time = new DateTimeOffset(2026, 10, 23, 9, 12, 30, TimeSpan.Zero);
        var summary = history.CaptureSummary(time);
        var bytes = history.Save(summary);
        var entry = StoryHistory.DescribeSave("Named save", bytes);

        Assert.Equal("Named save", entry.Slot);
        Assert.Equal("a", summary.SceneTitle);
        Assert.Equal("Tester", summary.CharacterName);
        Assert.Equal(time, entry.Summary!.SavedAt);
        Assert.Equal(summary, entry.Summary);
        Assert.Null(entry.Warning);
        Assert.Equal(history.ProgressKey, entry.ProgressKey);
        AssertEquivalent(history, StoryHistory.Restore(history.Story, bytes));
        var withoutDetails = StoryHistory.DescribeSave("Without details", history.Save());
        Assert.Null(withoutDetails.Summary);
        Assert.Equal(entry.ProgressKey, withoutDetails.ProgressKey);
        Assert.Null(SaveFormat.Read(history.Save()).Summary);
    }

    [Fact]
    public void Continued_scenes_keep_individual_history_and_restore_the_same_presentation_data()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            Interview.
            * [Path A] -> left
            * [Path B] -> right
            === left
            @continues
            From path A.
            * [Continue A] -> shared
            === right
            @continues
            From path B.
            * [Continue B] -> shared
            === shared
            @continues
            Shared response.
            * [Continue] -> final
            === final
            @continues
            Final response.
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);
        history.Choose("c0");
        history.Choose("c0");
        history.Choose("c0");

        Assert.Equal(["a", "left", "shared", "final"],
            history.Transcript.Select(frame => frame.Frame.Scene!.Value.Value));
        Assert.Equal([false, true, true, true],
            history.Transcript.Select(frame => frame.Frame.ContinuesPrevious));
        Assert.Equal("Continue", Assert.Single(history.Transcript[2].Frame.Choices).Text);

        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Transcript.Select(frame => frame.Frame.Document.ToPlainText()),
            restored.Transcript.Select(frame => frame.Frame.Document.ToPlainText()));
        Assert.Equal(history.Transcript.Select(frame => frame.Frame.ContinuesPrevious),
            restored.Transcript.Select(frame => frame.Frame.ContinuesPrevious));

        history.Back();
        Assert.Equal("shared", history.Current.Scene!.Value.Value);
        Assert.DoesNotContain("Final response.", history.Transcript.SelectMany(frame =>
            frame.Frame.Document.ToPlainText().Split('\n')));
        history.Forward();
        Assert.Equal("final", history.Current.Scene!.Value.Value);

        while (history.CanBack)
            history.Back();
        history.Choose("c1");
        history.Choose("c0");
        Assert.Equal("right", history.Transcript[1].Frame.Scene!.Value.Value);
        Assert.Equal("shared", history.Current.Scene!.Value.Value);
        Assert.True(history.Current.ContinuesPrevious);
    }

    [Fact]
    public void Bare_scene_diverts_auto_accept_and_preserve_ordered_rendered_history_and_saving_triggers()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true, saveTrigger: true).Build(), tale: """
            === a
            @title Arrival
            ~ var:visits += 1
            Arrival {var:visits}.
            {if var:visits >= 1}
            -> middle
            {/if}
            This line must not run.
            === middle
            @title Courtyard
            ~ var:visits += 1
            The courtyard is empty.
            -> final
            === final
            Final visit: {var:visits}.
            * [Continue the story] -> END
            """);

        var history = new StoryHistory(story, 42);

        Assert.Equal("final", history.Current.Scene!.Value.Value);
        Assert.Equal(["Arrival 1.", "The courtyard is empty.", "Final visit: 2."],
            history.Transcript.Select(frame => frame.Frame.Document.ToPlainText()));
        Assert.Equal(["a", "middle", "final"],
            history.Events.OfType<SceneEntered>().Select(entry => entry.Scene.Value));
        Assert.Equal(["continue", "continue"],
            history.Events.OfType<ChoiceMade>().Select(choice => choice.ChoiceKey));
        Assert.Equal([true, true], history.Transcript.Take(2).Select(frame => frame.ChosenAutomatically));
        Assert.All(history.Transcript.Take(2), frame => Assert.Equal("Continue",
            RenderedInline.ToPlainText(frame.Chosen!)));
        Assert.Equal("first", history.TriggerSaveId);
        Assert.Single(history.Events.OfType<TriggerFired>());

        var restored = StoryHistory.Restore(story, history.Save());
        AssertEquivalent(history, restored);
        Assert.Equal(history.Save(), restored.Save());
        Assert.Equal(history.Transcript.Select(frame => frame.ChosenAutomatically),
            restored.Transcript.Select(frame => frame.ChosenAutomatically));
    }

    [Fact]
    public void Empty_scene_diverts_can_end_without_an_interactive_continue()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            Arrival.
            -> empty
            === empty
            -> END
            """);

        var history = new StoryHistory(story, 42);

        Assert.True(history.Current.IsEnded);
        Assert.Equal(["a", "empty"],
            history.Transcript.Select(frame => frame.Frame.Scene?.Value));
        Assert.Equal(["Arrival.", ""], history.Transcript.Select(frame => frame.Frame.Document.ToPlainText()));
        Assert.Equal(["continue"], history.Events.OfType<ChoiceMade>().Select(choice => choice.ChoiceKey));
        Assert.True(history.Transcript[0].ChosenAutomatically);
        Assert.False(history.Transcript[1].ChosenAutomatically);
        Assert.True(history.Transcript[^1].Frame.IsEnded);
        Assert.True(history.Current.IsEnded);
    }

    [Fact]
    public void Check_outcome_continuations_remain_player_visible_and_back_forward_skip_only_automatic_actions()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            ::: skill-check athletics dc 1
            success: -> b
            failure: -> b
            :::
            === b
            ::: skill-check athletics dc 1
            success: -> c
            failure: -> c
            :::
            === c
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);

        Assert.Equal("a", history.Current.Scene!.Value.Value);
        Assert.Equal("Continue", Assert.Single(history.Current.Choices).Text);
        Assert.False(history.Transcript[0].Frame.AutomaticallyContinue);

        history.Choose(StorySession.ContinueKey);
        Assert.Equal("b", history.Current.Scene!.Value.Value);
        Assert.Equal("Continue", Assert.Single(history.Current.Choices).Text);
        Assert.False(history.Transcript[^1].Frame.AutomaticallyContinue);

        history.Choose(StorySession.ContinueKey);
        Assert.Equal("c", history.Current.Scene!.Value.Value);
        Assert.Equal(["a", "b", "c"], history.Transcript.Select(frame => frame.Frame.Scene!.Value.Value));
        Assert.All(history.Transcript.Take(2), frame => Assert.False(frame.ChosenAutomatically));

        history.Back();
        Assert.Equal("b", history.Current.Scene!.Value.Value);
        history.Forward();
        Assert.Equal("c", history.Current.Scene!.Value.Value);
    }

    [Fact]
    public void Choice_check_enters_its_outcome_without_an_extra_continue()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            * [Attempt] skill-check athletics dc 1
              success: -> b
              failure: -> b
            === b
            The outcome is reached.
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);

        Assert.Equal("Attempt", Assert.Single(history.Current.Choices).Text);
        history.Choose("c0");

        Assert.Equal("b", history.Current.Scene!.Value.Value);
        Assert.Contains("The outcome is reached.", history.Current.Document.ToPlainText());
        Assert.Equal("Finish", Assert.Single(history.Current.Choices).Text);
        Assert.False(history.Transcript[0].ChosenAutomatically);
    }

    [Fact]
    public void Entry_check_outcome_to_end_finishes_in_the_current_frame()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            The final result.
            ::: skill-check athletics dc 1
            success: -> END
            failure: -> END
            :::
            """);
        var history = new StoryHistory(story, 42);

        Assert.True(history.Current.IsEnded);
        Assert.Empty(history.Current.Choices);
        Assert.Single(history.Transcript);
        Assert.Contains("The final result.", history.Current.Document.ToPlainText());
    }

    [Fact]
    public void Explicit_textless_choices_remain_interactive()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            * -> b
            * -> END
            === b
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);

        Assert.Equal(["Continue", "Continue"], history.Current.Choices.Select(choice => choice.Text));
        Assert.Single(history.Transcript);
        history.Choose("c0");
        Assert.Equal("b", history.Current.Scene!.Value.Value);
        Assert.False(history.Transcript[0].ChosenAutomatically);
        Assert.Equal("Continue", RenderedInline.ToPlainText(history.Transcript[0].Chosen!));
    }

    [Fact]
    public void Automatic_transition_budget_accepts_exactly_one_thousand_transitions()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(),
            tale: BareDivertChain(StoryHistory.MaxAutomaticTransitions));

        var history = new StoryHistory(story, 42);

        Assert.Null(history.AutomaticContinuationError);
        Assert.Equal("scene-1000", history.Current.Scene!.Value.Value);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions, history.Cursor);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions + 1, history.Transcript.Count);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions,
            history.Events.OfType<ChoiceMade>().Count(choice => choice.ChoiceKey == StorySession.ContinueKey));
        Assert.Equal("Finish", Assert.Single(history.Current.Choices).Text);
    }

    [Fact]
    public void Automatic_transition_budget_stops_before_a_finite_1001st_transition()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(),
            tale: BareDivertChain(StoryHistory.MaxAutomaticTransitions + 1));

        var history = new StoryHistory(story, 42);

        Assert.NotNull(history.AutomaticContinuationError);
        Assert.Equal("scene-1000", history.Current.Scene!.Value.Value);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions, history.Cursor);
        Assert.False(history.Current.IsEnded);
        Assert.Empty(history.Current.Choices);
        Assert.DoesNotContain(history.Events.OfType<SceneEntered>(), entry => entry.Scene.Value == "scene-1001");

        var restored = StoryHistory.Restore(story, history.Save());

        Assert.Equal(history.Cursor, restored.Cursor);
        Assert.Equal(history.Current.Scene, restored.Current.Scene);
        Assert.Equal(history.AutomaticContinuationError, restored.AutomaticContinuationError);
    }

    [Fact]
    public void Restore_rejects_saved_choices_after_the_automatic_transition_budget()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(),
            tale: BareDivertChain(StoryHistory.MaxAutomaticTransitions + 1));
        var history = new StoryHistory(story, 42);
        var invalidSave = Mutate(history.Save(), save => save with
        {
            Choices = [.. save.Choices, "invalid"],
            Cursor = save.Choices.Count + 1,
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
            Continuation = null,
        });

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(story, invalidSave));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Automatic_transition_budget_stops_self_and_multi_scene_runaway_chains(int cycleLength)
    {
        var tale = cycleLength == 1
            ? "=== a\n-> a"
            : "=== a\n-> b\n=== b\n-> a";
        var history = new StoryHistory(
            Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: tale), 42);

        Assert.NotNull(history.AutomaticContinuationError);
        Assert.Contains("1,000 automatic scene transitions", history.AutomaticContinuationError);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions, history.Cursor);
        Assert.False(history.Current.IsEnded);
        Assert.Empty(history.Current.Choices);
        Assert.Equal(StoryHistory.MaxAutomaticTransitions + 1,
            history.Events.OfType<SceneEntered>().Count());
        Assert.Equal(StoryHistory.MaxAutomaticTransitions,
            history.Events.OfType<ChoiceMade>().Count(choice => choice.ChoiceKey == StorySession.ContinueKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_transition_budget_resets_after_each_player_action(bool checkBoundary)
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(),
            tale: BareDivertChain(600, secondTransitions: 600, finishChoice: "Again", checkBoundary: checkBoundary));
        var history = new StoryHistory(story, 42);

        Assert.Null(history.AutomaticContinuationError);
        Assert.Equal("scene-600", history.Current.Scene!.Value.Value);
        Assert.Equal(600, history.Cursor);
        Assert.Equal(checkBoundary ? "Continue" : "Again", Assert.Single(history.Current.Choices).Text);

        history.Choose(checkBoundary ? StorySession.ContinueKey : "c0");

        Assert.Null(history.AutomaticContinuationError);
        Assert.Equal("scene-1201", history.Current.Scene!.Value.Value);
        Assert.Equal(1_201, history.Cursor);
        Assert.Equal(1_202, history.Transcript.Count);
        Assert.False(history.Transcript[600].ChosenAutomatically);
    }

    [Fact]
    public void State_changing_scene_revisits_can_exit_an_automatic_chain_within_budget()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            ~ var:visits += 1
            {if var:visits < 3}
            -> a
            {/if}
            * [Finish] -> END
            """);

        var history = new StoryHistory(story, 42);

        Assert.Null(history.AutomaticContinuationError);
        Assert.Equal("a", history.Current.Scene!.Value.Value);
        Assert.Equal(3, history.State.GetVariable(new VarId("visits")));
        Assert.Equal(2, history.Cursor);
        Assert.Equal(["a", "a", "a"], history.Transcript.Select(frame => frame.Frame.Scene!.Value.Value));
    }

    [Fact]
    public void Old_saves_at_automatic_pauses_advance_under_the_new_bare_divert_rule()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            -> middle
            === middle
            -> final
            === final
            * [Finish] -> END
            """);
        var session = new StorySession(story, 42);
        var initialState = ReplayEvidence.InitialState(story, session.State);
        session.Start();
        var evidence = ReplayEvidence.Start(story, session, null, initialState);
        var oldSave = SaveFormat.Write(new SavedGame
        {
            StoryId = story.Id,
            StoryVersion = story.Version,
            ContentFingerprint = story.ContentFingerprint
                ?? throw new InvalidOperationException("The compiled story must have a content fingerprint."),
            Seed = session.Seed,
            Choices = [],
            Cursor = 0,
            ReplayEvidence = ReplayEvidence.Encode(evidence, 0),
            ReplayChoiceIdentities = [],
        });

        var inserted = new List<ReplayTransitionKind>();
        var restored = StoryHistory.Restore(story, oldSave, inserted.Add);

        Assert.Equal("final", restored.Current.Scene!.Value.Value);
        Assert.Equal(["a", "middle", "final"],
            restored.Transcript.Select(frame => frame.Frame.Scene!.Value.Value));
        Assert.Equal(2, restored.Cursor);
        Assert.False(restored.CanBack);
        Assert.Equal([
            ReplayTransitionKind.InsertedAutomaticTransition,
            ReplayTransitionKind.InsertedAutomaticTransition,
        ], inserted);

        var stored = new StoryHistory(story, 42);
        var replayed = new List<ReplayTransitionKind>();
        StoryHistory.Restore(story, stored.Save(), replayed.Add);
        Assert.Equal([
            ReplayTransitionKind.StoredAutomaticTransition,
            ReplayTransitionKind.StoredAutomaticTransition,
        ], replayed);
    }

    [Fact]
    public void Restore_captures_a_cursor_inside_stored_automatic_transitions_before_redo()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            -> middle
            === middle
            -> final
            === final
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);
        history.Choose("c0");
        var save = SaveFormat.Read(history.Save()) with
        {
            Cursor = 1,
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
            Continuation = null,
        };

        var restored = StoryHistory.Restore(story, SaveFormat.Write(save));

        Assert.Equal(2, restored.Cursor);
        Assert.Equal("final", restored.Current.Scene!.Value.Value);
        Assert.False(restored.Current.IsEnded);
        Assert.True(restored.CanForward);
        restored.Forward();
        Assert.True(restored.Current.IsEnded);
    }

    [Fact]
    public void Cleared_scenes_keep_full_history_and_restore_the_clear_flag()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            Before the break.
            * [Continue] -> next
            === next
            Before the clear.
            * [Begin again] -> reset
            === reset
            @clear
            After the clear.
            * [Continue] -> finish
            === finish
            Finished.
            -> END
            """);
        var history = new StoryHistory(story, 42);
        history.Choose("c0");
        history.Choose("c0");

        Assert.Equal(3, history.Transcript.Count);
        Assert.Equal([false, false, true], history.Transcript.Select(frame => frame.Frame.ClearsPrevious));

        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Transcript.Select(frame => frame.Frame.ClearsPrevious),
            restored.Transcript.Select(frame => frame.Frame.ClearsPrevious));
        Assert.Equal(history.Transcript.Select(frame => frame.Frame.Document.ToPlainText()),
            restored.Transcript.Select(frame => frame.Frame.Document.ToPlainText()));

        history.Back();
        Assert.Equal("next", history.Current.Scene!.Value.Value);
        Assert.False(history.Current.ClearsPrevious);
        history.Forward();
        Assert.Equal("reset", history.Current.Scene!.Value.Value);
        Assert.True(history.Current.ClearsPrevious);
    }

    [Fact]
    public void Ending_from_a_cleared_scene_does_not_repeat_the_clear()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            @clear
            Final scene.
            * [Finish] -> END
            """);
        var history = new StoryHistory(story, 42);
        history.Choose("c0");

        Assert.True(history.Transcript[0].Frame.ClearsPrevious);
        Assert.False(history.Current.ClearsPrevious);
        Assert.True(history.Current.IsEnded);
    }

    [Fact]
    public void Saving_triggers_coalesce_per_step_and_restore_without_repeating_the_save_request()
    {
        var story = Compile(BuildManifest(declareVariables: true, saveTrigger: true, multipleSavingTriggers: true).Build());
        var history = new StoryHistory(story, 42);

        Assert.Equal(["first", "second"], history.Events.OfType<TriggerFired>().Select(trigger => trigger.TriggerId));
        Assert.Equal("second", history.TriggerSaveId);

        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Null(restored.TriggerSaveId);
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.State.GetVariable(new VarId("trigger-count")),
            restored.State.GetVariable(new VarId("trigger-count")));
    }

    [Fact]
    public void Repeated_saving_trigger_is_reported_again_after_a_later_step()
    {
        var history = new StoryHistory(
            Compile(BuildManifest(declareVariables: true, saveTrigger: true, repeatedSavingTrigger: true).Build()), 42);

        Assert.Equal("first", history.TriggerSaveId);
        history.Choose("c0");

        Assert.Equal("repeat", history.TriggerSaveId);
        Assert.Equal(1, history.Events.OfType<TriggerFired>().Count(trigger => trigger.TriggerId == "repeat"));
    }

    [Theory]
    [InlineData(new byte[] { 0xC1 })]
    [InlineData(new byte[] { 0x91 })]
    [InlineData(new byte[] { 0x91, 0xC1 })]
    public void Unreadable_summary_does_not_prevent_loading_replay(byte[] summary)
    {
        var history = new StoryHistory(Compile(), 7);
        var payload = SaveFormat.ToPayload(SaveFormat.Read(history.Save()));
        payload.Summary = summary;
        var bytes = SaveFormat.Pack(SaveFormat.EncodeBody(payload));
        var entry = StoryHistory.DescribeSave("Damaged details", bytes);
        Assert.Null(entry.Summary);
        Assert.Contains("unreadable", entry.Warning);
        Assert.Equal(history.ProgressKey, entry.ProgressKey);
        AssertEquivalent(history, StoryHistory.Restore(history.Story, bytes));
    }

    [Fact]
    public void Catalog_reads_metadata_without_replaying_and_keeps_damaged_slots_visible()
    {
        var history = new StoryHistory(Compile(), 7);
        var bytes = Mutate(history.Save(history.CaptureSummary(DateTimeOffset.UtcNow)),
            save => save with
            {
                Choices = ["no-such-choice"],
                ReplayChoiceIdentities = [new byte[ReplayChoiceIdLength]],
                Cursor = 1,
            });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, bytes));
        Assert.NotNull(StoryHistory.DescribeSave("Invalid replay", bytes).Summary);
        foreach (var broken in new[] { "{broken"u8.ToArray(), bytes[..^4], [.. "TWSV"u8, 3, 0xFF] })
        {
            var damaged = StoryHistory.DescribeSave("Broken", broken);
            Assert.Equal("Broken", damaged.Slot);
            Assert.Null(damaged.ProgressKey);
            Assert.NotNull(damaged.Warning);
        }
    }

    [Fact]
    public void Progress_identity_includes_seed_cursor_and_entire_redo_branch_but_not_save_time()
    {
        var story = Compile();
        var history = new StoryHistory(story, 7);
        var start = history.ProgressKey;
        Assert.NotEqual(start, new StoryHistory(story, 8).ProgressKey);
        history.Choose("c0");
        var advanced = history.ProgressKey;
        Assert.NotEqual(start, advanced);
        history.Back();
        Assert.NotEqual(start, history.ProgressKey); // Retained redo is progress too.
        history.Forward();
        Assert.Equal(advanced, history.ProgressKey);
        history.Back();
        history.Choose("c1");
        Assert.NotEqual(advanced, history.ProgressKey);
        var first = StoryHistory.DescribeSave("one", history.Save(history.CaptureSummary(DateTimeOffset.UtcNow)));
        var later = StoryHistory.DescribeSave("two", history.Save(history.CaptureSummary(DateTimeOffset.UtcNow.AddDays(1))));
        Assert.Equal(first.ProgressKey, later.ProgressKey);
    }

    [Fact]
    public void Save_round_trip_preserves_seed_events_state_frames_and_redo()
    {
        var history = new StoryHistory(Compile(), ulong.MaxValue);
        history.Choose("c0");
        history.Choose("c0");
        history.Choose("c1");
        history.Back();

        var restored = StoryHistory.Restore(history.Story, history.Save());

        AssertEquivalent(history, restored);
        Assert.Equal(ulong.MaxValue, restored.Seed);
        Assert.True(restored.CanForward);
        history.Forward();
        restored.Forward();
        AssertEquivalent(history, restored);
        Assert.True(restored.Current.IsEnded);
    }

    [Fact]
    public void Restore_executes_each_saved_transition_once_including_redo_after_a_middle_cursor()
    {
        var history = new StoryHistory(Compile(), 42);
        history.Choose("c0");
        history.Choose("c0");
        history.Choose("c0");
        history.Choose("c1");
        history.Back();
        var save = SaveFormat.Read(history.Save()) with { Continuation = null };
        var executed = new List<ReplayTransitionKind>();

        var restored = StoryHistory.Restore(history.Story, SaveFormat.Write(save), executed.Add);

        Assert.Equal(save.Choices.Count, executed.Count);
        Assert.All(executed, kind => Assert.Equal(ReplayTransitionKind.SavedChoice, kind));
        Assert.Equal(history.Cursor, restored.Cursor);
        Assert.Equal(history.Events, restored.Events);
        Assert.True(restored.CanForward);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Restored_history_continues_exactly_like_the_live_history(int backSteps)
    {
        var history = new StoryHistory(Compile(), 11);
        for (var i = 0; i < 4; i++)
        {
            history.Choose("c0");
        }

        for (var i = 0; i < backSteps; i++)
        {
            history.Back();
        }

        var restored = StoryHistory.Restore(history.Story, history.Save());

        AssertEquivalent(history, restored);
        Assert.Equal(ChosenLabels(history), ChosenLabels(restored));
        Assert.Equal(history.Save(), restored.Save());
        foreach (var live in new[] { history, restored })
        {
            live.Choose("c0");
            live.Choose("c0");
            live.Back();
        }

        AssertEquivalent(history, restored);
        Assert.Equal(ChosenLabels(history), ChosenLabels(restored));
        Assert.Equal(history.Save(), restored.Save());
    }

    [Fact]
    public void Continue_identity_uses_its_scene_regardless_of_how_it_was_entered()
    {
        var story = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            * [Direct] -> target
            * [Via middle] -> middle
            === middle
            * [Continue] -> target
            === target
            -> finish
            === finish
            -> END
            """);
        var direct = new StoryHistory(story, 7);
        direct.Choose("c0");
        var indirect = new StoryHistory(story, 7);
        indirect.Choose("c1");
        indirect.Choose("c0");

        var directTarget = Assert.Single(direct.Transcript, frame => frame.Frame.Scene == new SceneId("target"));
        var indirectTarget = Assert.Single(indirect.Transcript, frame => frame.Frame.Scene == new SceneId("target"));
        Assert.True(directTarget.ChosenAutomatically);
        Assert.True(indirectTarget.ChosenAutomatically);
        Assert.Equal(directTarget.Frame.Choices.Single().ReplayIdentity!.Value.ToArray(),
            indirectTarget.Frame.Choices.Single().ReplayIdentity!.Value.ToArray());

        AssertEquivalent(direct, StoryHistory.Restore(story, direct.Save()));
        AssertEquivalent(indirect, StoryHistory.Restore(story, indirect.Save()));
        var legacy = Mutate(direct.Save(), save => save with
        {
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
        });
        AssertEquivalent(direct, StoryHistory.Restore(story, legacy));
    }

    [Fact]
    public void Manually_constructed_stories_support_hub_and_continue_replay()
    {
        var compiled = Compile(yaml: BuildManifest(declareVariables: true).Build(), tale: """
            === a
            @hub room
            === b
            @tags room
            -> c
            === c
            -> END
            """);
        var story = new StoryDefinition
        {
            Id = compiled.Id,
            Title = compiled.Title,
            Version = compiled.Version,
            ContentFingerprint = compiled.ContentFingerprint,
            ReplayCompatibilityId = compiled.ReplayCompatibilityId,
            UndoEnabled = compiled.UndoEnabled,
            Creation = compiled.Creation,
            Start = compiled.Start,
            Randomness = compiled.Randomness,
            Seed = compiled.Seed,
            World = compiled.World,
            Triggers = compiled.Triggers,
            Predicates = compiled.Predicates,
            Scenes = compiled.Scenes.Select(scene => new SceneDefinition
            {
                Id = scene.Id,
                Title = scene.Title,
                Requires = scene.Requires,
                Tags = scene.Tags,
                Priority = scene.Priority,
                Once = scene.Once,
                Hub = scene.Hub,
                HubLimit = scene.HubLimit,
                Steps = scene.Steps,
            }).ToArray(),
        };

        var history = new StoryHistory(story, 7);
        history.Choose("storylet:b");

        Assert.True(history.Transcript[^2].ChosenAutomatically);
        AssertEquivalent(history, StoryHistory.Restore(story, history.Save()));
    }

    [Fact]
    public void Stories_sharing_compiled_scenes_keep_independent_replay_identities()
    {
        var yaml = BuildManifest(declareVariables: true).Build();
        var first = Compile(yaml: yaml, tale: """
            === a
            @hub room
            === b
            @tags room
            -> c
            === c
            -> END
            """);
        var second = Compile(yaml: yaml, tale: """
            === a
            -> extra
            === extra
            @tags room
            -> END
            """);
        var firstIdentities = HubIdentities(first);
        var mixed = WithScenes(first, [.. first.Scenes, second.GetScene(new SceneId("extra"))]);

        var history = new StoryHistory(mixed, 7);
        history.Choose("storylet:extra");
        AssertEquivalent(history, StoryHistory.Restore(mixed, history.Save()));

        Assert.Equal(firstIdentities, HubIdentities(first));
        var original = new StoryHistory(first, 7);
        original.Choose("storylet:b");
        Assert.True(original.Transcript[^2].ChosenAutomatically);
        AssertEquivalent(original, StoryHistory.Restore(first, original.Save()));
        var other = new StoryHistory(second, 7);
        Assert.True(other.Transcript[0].ChosenAutomatically);
        AssertEquivalent(other, StoryHistory.Restore(second, other.Save()));

        static string[] HubIdentities(StoryDefinition story) =>
            new StoryHistory(story, 7).Current.Choices
                .Select(choice => Convert.ToHexString(choice.ReplayIdentity!.Value.Span)).ToArray();
    }

    [Fact]
    public void Invalid_last_choice_is_rejected_when_cursor_is_at_the_end()
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        history.Choose("c0");
        var save = Mutate(history.Save(), save => save with
        {
            ReplayChoiceIdentities = CorruptIdentity(save.ReplayChoiceIdentities!, 1),
        });

        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, save));

        Assert.Contains("cannot continue", error.Message);
    }

    [Fact]
    public void Replay_uses_the_saved_outcome_identity_instead_of_the_ui_choice_key()
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        var save = Mutate(history.Save(), data => data with { Choices = ["renamed-key"] });

        var restored = StoryHistory.Restore(history.Story, save);

        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.State.GetBaseStat(EntityId.Player, new StatId("hp")),
            restored.State.GetBaseStat(EntityId.Player, new StatId("hp")));
    }

    [Fact]
    public void Back_and_forward_rebuild_triggers_and_dice_without_reapplying_entry_effects()
    {
        var history = new StoryHistory(Compile(), 42);
        history.Choose("c0");
        var events = history.Events.ToArray();
        Assert.Equal(2, history.State.GetVariable(new VarId("visits")));
        history.Back();
        Assert.Equal(1, history.State.GetVariable(new VarId("visits")));
        history.Forward();
        Assert.Equal(events, history.Events);
        Assert.Equal(1, history.State.GetVariable(new VarId("trigger-count")));
        history.Back();
        history.Choose("c0");
        Assert.Equal(events, history.Events);
        Assert.False(history.CanForward);
    }

    [Fact]
    public void Different_choice_discards_redo_and_restores_transcript()
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        history.Back();
        Assert.Single(history.Transcript);
        Assert.Null(history.Transcript[0].Chosen);
        history.Choose("c1");
        Assert.False(history.CanForward);
        Assert.Equal(2, history.Transcript.Count);
        Assert.Equal("Finish", history.Transcript[0].Chosen!.OfType<Talewright.Narrative.Rendering.RenderedText>().Single().Text);
    }

    [Theory]
    [InlineData("format-old", "unsupported format")]
    [InlineData("format-new", "unsupported format")]
    [InlineData("json", "unsupported format")]
    [InlineData("story-id", "different story")]
    [InlineData("story-version", "Version other of History has changed")]
    [InlineData("fingerprint", "History has changed")]
    [InlineData("cursor", "history is damaged")]
    [InlineData("choice", "cannot continue")]
    public void Invalid_saves_preserve_active_session(string damage, string message)
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        var before = history.Save();
        var invalid = damage switch
        {
            "format-old" => [.. before[..4], 2, .. before[5..]],
            "format-new" => [.. before[..4], 99, .. before[5..]],
            "json" => """{"formatVersion":2,"storyId":"history"}"""u8.ToArray(),
            "story-id" => Mutate(before, save => save with { StoryId = "other" }),
            "story-version" => Mutate(before, save => save with { StoryVersion = "other", Continuation = null }),
            "fingerprint" => Mutate(before, save => save with { ContentFingerprint = "changed", Continuation = null }),
            "cursor" => Mutate(before, save => save with { Cursor = 3 }),
            "choice" => Mutate(before, save => save with
            {
                ReplayChoiceIdentities = CorruptIdentity(save.ReplayChoiceIdentities!, 0),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(damage)),
        };

        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, invalid));

        Assert.Contains(message, error.Message);
        Assert.Equal(before, history.Save());
    }

    [Fact]
    public void Invalid_redo_is_checked_even_when_cursor_is_at_the_start()
    {
        var history = new StoryHistory(Compile(), 7);
        var save = Mutate(history.Save(), save => save with
        {
            Choices = ["missing"],
            ReplayChoiceIdentities = [new byte[ReplayChoiceIdLength]],
        });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, save));
    }

    [Fact]
    public void Invalid_replay_reports_no_scene_or_choice_position()
    {
        var history = new StoryHistory(Compile(), 7);
        var save = Mutate(history.Save(), save => save with
        {
            Choices = ["missing"],
            ReplayChoiceIdentities = [new byte[ReplayChoiceIdLength]],
        });
        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, save));

        Assert.Contains("cannot continue", error.Message);
        Assert.Contains("no longer compatible", error.Message);
        Assert.DoesNotContain("choice", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scene", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("TWSV")]
    [InlineData("TWSV\u0003")]
    public void Malformed_saves_are_explicitly_rejected(string text)
    {
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(), Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Old_json_saves_are_reported_as_an_unsupported_format()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(Compile(), "\n {\"formatVersion\":2}"u8));
        Assert.Equal(SaveFormat.UnsupportedMessage, error.Message);
    }

    [Fact]
    public void Oversized_save_is_rejected_before_deserialization()
    {
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(), new byte[StoryHistory.MaxSaveBytes + 1]));
    }

    [Theory]
    [InlineData(SaveFormat.MaxChoiceKeyLength, true)]
    [InlineData(SaveFormat.MaxChoiceKeyLength + 1, false)]
    public void Choice_keys_up_to_the_length_limit_save_and_longer_keys_do_not(int keyLength, bool saves)
    {
        var story = Compile(Yaml, "=== a\n~ var:visits += 1\n* [Again] -> a");
        var key = new string('k', keyLength);
        var scene = story.GetScene(story.Start);
        var longKeyStory = new StoryDefinition
        {
            Id = story.Id,
            Title = story.Title,
            Version = story.Version,
            ContentFingerprint = story.ContentFingerprint,
            UndoEnabled = story.UndoEnabled,
            Creation = story.Creation,
            Start = story.Start,
            Randomness = story.Randomness,
            Seed = story.Seed,
            World = story.World,
            Triggers = story.Triggers,
            Predicates = story.Predicates,
            Scenes =
            [
                new SceneDefinition
                {
                    Id = scene.Id,
                    Title = scene.Title,
                    Requires = scene.Requires,
                    Tags = scene.Tags,
                    Priority = scene.Priority,
                    Once = scene.Once,
                    Hub = scene.Hub,
                    HubLimit = scene.HubLimit,
                    Steps =
                    [
                        new ChoiceStep("step:2:1", scene.Steps[0].Source, new ChoiceDefinition
                        {
                            Key = key,
                            Label = scene.Choices[0].Label,
                            Divert = new DivertTarget(story.Start),
                        }),
                    ],
                },
            ],
        };

        var history = new StoryHistory(longKeyStory, 7);
        history.Choose(key);
        history.Choose(key);
        if (saves)
        {
            var save = history.Save();
            Assert.Equal([key, key], SaveFormat.Read(save).Choices);
            Assert.Equal(2, StoryHistory.Restore(longKeyStory, save).Cursor);
            Assert.True(history.TryProgressKey(out _));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => history.Save());
            Assert.False(history.TryProgressKey(out var progress));
            Assert.Null(progress);
        }
    }

    // A producer that builds the model itself supplies the fingerprint and the choice key; the compiler no longer does.
    private static StoryDefinition BuildProgrammaticStory(string? fingerprint, string choiceKey)
    {
        var compiled = Compile(Yaml, "=== a
~ var:visits += 1
* [Again] -> a");
        var scene = compiled.GetScene(compiled.Start);
        return new StoryDefinition
        {
            Id = compiled.Id,
            Title = compiled.Title,
            Version = compiled.Version,
            ContentFingerprint = fingerprint,
            UndoEnabled = compiled.UndoEnabled,
            Creation = compiled.Creation,
            Start = compiled.Start,
            Randomness = compiled.Randomness,
            Seed = compiled.Seed,
            World = compiled.World,
            Triggers = compiled.Triggers,
            Predicates = compiled.Predicates,
            Scenes =
            [
                new SceneDefinition
                {
                    Id = scene.Id,
                    Title = scene.Title,
                    Requires = scene.Requires,
                    Tags = scene.Tags,
                    Priority = scene.Priority,
                    Once = scene.Once,
                    Hub = scene.Hub,
                    HubLimit = scene.HubLimit,
                    Steps =
                    [
                        new ChoiceStep("step:2:1", scene.Steps[0].Source, new ChoiceDefinition
                        {
                            Key = choiceKey,
                            Label = scene.Choices[0].Label,
                            Divert = new DivertTarget(compiled.Start),
                        }),
                    ],
                },
            ],
        };
    }

    [Fact]
    public void Programmatic_story_with_an_explicit_fingerprint_and_stable_choice_key_saves_and_restores()
    {
        var fingerprint = new string('A', 64);
        var story = BuildProgrammaticStory(fingerprint, "take-the-lantern");
        var history = new StoryHistory(story, 7);
        history.Choose("take-the-lantern");
        history.Choose("take-the-lantern");

        var save = history.Save();
        var saved = SaveFormat.Read(save);
        Assert.Equal(fingerprint, saved.ContentFingerprint);
        Assert.Equal(["take-the-lantern", "take-the-lantern"], saved.Choices);

        var restored = StoryHistory.Restore(BuildProgrammaticStory(fingerprint, "take-the-lantern"), save);
        Assert.Equal(history.Cursor, restored.Cursor);
        Assert.True(history.TryProgressKey(out var progress));
        Assert.True(restored.TryProgressKey(out var restoredProgress));
        Assert.Equal(progress, restoredProgress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Programmatic_story_without_a_fingerprint_cannot_be_saved(string? fingerprint)
    {
        var history = new StoryHistory(BuildProgrammaticStory(fingerprint, "take-the-lantern"), 7);
        history.Choose("take-the-lantern");

        Assert.Throws<InvalidOperationException>(() => history.Save());
        Assert.False(history.TryProgressKey(out _));
    }

    [Fact]
    public void One_hundred_thousand_choice_playthroughs_save_restore_and_keep_playing()
    {
        const int choices = 100_000;
        var history = new StoryHistory(Compile(BuildManifest(declareVariables: true).Build(),
            string.Join('\n', SceneFaker.Header("a"), ChoiceFaker.Create("a", "Again"))), 7);
        for (var i = 0; i < choices; i++)
        {
            history.Choose("c0");
        }

        Assert.Equal(choices, history.Cursor);
        Assert.Equal(choices + 1, history.Transcript.Count);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(choices, restored.Cursor);
        Assert.Equal(choices + 1, restored.Transcript.Count);
        restored.Choose("c0");
        Assert.Equal(choices + 1, restored.Cursor);
    }

    [Fact]
    public void Changed_source_requires_evidence_even_when_story_version_is_unchanged()
    {
        var history = new StoryHistory(Compile(), 7);
        var updated = Compile(tale: Tale + "\n// change");
        var save = history.Save();
        var legacy = Mutate(save, data => data with
        {
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
            Continuation = null,
        });
        var error = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(updated, legacy));
        Assert.Equal("Version 1 of History has changed, and this save is no longer compatible. Open it with the story and player version that created it.", error.Message);
        AssertEquivalent(history, StoryHistory.Restore(updated, save));
    }

    [Fact]
    public void Custom_extensions_require_explicit_versioned_compatibility_identity()
    {
        var history = new StoryHistory(Compile(options: new CompilerOptions()), 7);
        Assert.Throws<InvalidOperationException>(() => history.Save());
        var version1 = Compile(options: new CompilerOptions { CompatibilityId = "custom-v1" });
        var original = new StoryHistory(version1, 7);
        var save = original.Save();
        var copy = save.ToArray();
        var replayOnly = SaveFormat.Write(SaveFormat.Read(save) with { Continuation = null });
        var version2 = Compile(options: new CompilerOptions { CompatibilityId = "custom-v2" });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(version2, replayOnly));
        var continued = StoryHistory.Restore(version2, save);
        Assert.NotNull(continued.Continuation);
        Assert.Equal(original.Current.Scene, continued.Current.Scene);
        Assert.Equal(original.State.VisitCount(original.Current.Scene!.Value),
            continued.State.VisitCount(continued.Current.Scene!.Value));
        Assert.Equal(copy, save);
    }

    [Fact]
    public void Undo_can_be_disabled_without_disabling_saves()
    {
        var history = new StoryHistory(Compile(BuildManifest(undo: false).Build()), 7);
        history.Choose("c0");
        Assert.False(history.CanBack);
        Assert.Throws<InvalidOperationException>(() => history.Back());
        AssertEquivalent(history, StoryHistory.Restore(history.Story, history.Save()));
    }

    [Fact]
    public void Invalid_choice_and_history_navigation_preserve_history()
    {
        var history = new StoryHistory(Compile(), 7);
        var original = history.Save();
        Assert.Throws<InvalidOperationException>(() => history.Choose("missing"));
        Assert.Throws<InvalidOperationException>(() => history.Back());
        Assert.Throws<InvalidOperationException>(() => history.Forward());
        Assert.Equal(original, history.Save());
    }

    private static byte[] Mutate(byte[] save, Func<SavedGame, SavedGame> change) =>
        SaveFormat.Write(change(SaveFormat.Read(save)));

    private static byte[][] CorruptIdentity(IReadOnlyList<byte[]> identities, int index)
    {
        var copy = identities.Select(identity => identity.ToArray()).ToArray();
        copy[index][0] ^= 1;
        return copy;
    }

    private static StoryDefinition WithScenes(StoryDefinition story, IReadOnlyList<SceneDefinition> scenes) => new()
    {
        Id = story.Id,
        Title = story.Title,
        Version = story.Version,
        ContentFingerprint = story.ContentFingerprint,
        ReplayCompatibilityId = story.ReplayCompatibilityId,
        UndoEnabled = story.UndoEnabled,
        Creation = story.Creation,
        Start = story.Start,
        Randomness = story.Randomness,
        Seed = story.Seed,
        World = story.World,
        Triggers = story.Triggers,
        Predicates = story.Predicates,
        Scenes = scenes,
    };

    private static void AssertEquivalent(StoryHistory expected, StoryHistory actual)
    {
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Equal(expected.State.GetBaseStat(EntityId.Player, new StatId("hp")), actual.State.GetBaseStat(EntityId.Player, new StatId("hp")));
        Assert.Equal(expected.Current.Document.ToPlainText(), actual.Current.Document.ToPlainText());
        Assert.Equal(expected.Current.Choices.Select(c => c.Key), actual.Current.Choices.Select(c => c.Key));
        Assert.Equal(expected.Transcript.Select(f => f.Frame.Document.ToPlainText()), actual.Transcript.Select(f => f.Frame.Document.ToPlainText()));
    }

    private static string?[] ChosenLabels(StoryHistory history) =>
        history.Transcript.Select(frame => frame.Chosen is null ? null
            : string.Concat(frame.Chosen.OfType<Talewright.Narrative.Rendering.RenderedText>().Select(text => text.Text))).ToArray();
}
