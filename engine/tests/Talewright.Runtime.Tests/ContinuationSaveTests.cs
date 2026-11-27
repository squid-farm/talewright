using System.Buffers;
using MessagePack;
using Talewright.Core;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class ContinuationSaveTests
{
    private static readonly StatId Hp = new("hp");
    private static readonly VarId Gold = new("gold");
    private static readonly VarId Bonus = new("bonus");

    [Fact]
    public void Added_hp_loss_in_a_visited_scene_is_not_applied_retroactively_but_applies_in_the_future()
    {
        var original = PlayToSquare(Compile(new()));
        var continuation = original.Save();
        var replayOnly = SaveFormat.Write(SaveFormat.Read(continuation) with { Continuation = null });
        var hp = original.State.GetBaseStat(EntityId.Player, Hp);
        var updated = Compile(new() { Version = "2", HpLoss = true });

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, replayOnly));
        var restored = Restore(updated, continuation);

        Assert.NotNull(restored.Continuation);
        Assert.Empty(restored.Continuation.Notes);
        Assert.Equal(hp, restored.State.GetBaseStat(EntityId.Player, Hp));
        Assert.Equal(original.State.GetVariable(Gold), restored.State.GetVariable(Gold));
        Assert.Equal(new SceneId("square"), restored.Current.Scene);
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        Assert.False(restored.CanBack);

        restored.Choose(KeyOf(restored.Current, "Back to market"));
        Assert.Equal(new SceneId("market"), restored.Current.Scene);
        Assert.Equal(hp - 5, restored.State.GetBaseStat(EntityId.Player, Hp));
    }

    [Fact]
    public void Unchanged_story_continuation_matches_uninterrupted_play_without_duplicating_effects()
    {
        var story = Compile(new());
        var original = PlayToSquare(story);
        Assert.Equal(1, original.State.GetVariable(Bonus));
        Assert.True(original.State.HasFiredTrigger("arrived"));
        var bytes = original.Save();

        var replayed = Restore(story, bytes);
        Assert.Null(replayed.Continuation);
        Assert.True(replayed.CanBack);
        var copy = bytes.ToArray();
        var restored = StoryHistory.RestoreFromCheckpoint(story, bytes);

        Assert.NotNull(restored.Continuation);
        Assert.Equal(copy, bytes);
        Assert.DoesNotContain(restored.Events, gameEvent => gameEvent is DiceRolled);
        Assert.Equal(0, restored.State.RollCount);
        Assert.Equal(Snapshot(original), Snapshot(restored));
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        var originalRolls = original.State.RollCount;
        foreach (var label in new[] { "Back to market", "Haggle", "Back to market", "Haggle" })
        {
            var originalStart = original.Events.Count;
            var restoredStart = restored.Events.Count;
            original.Choose(KeyOf(original.Current, label));
            restored.Choose(KeyOf(restored.Current, label));
            Assert.Equal(original.Events.Skip(originalStart), restored.Events.Skip(restoredStart));
            Assert.Equal(Snapshot(original), Snapshot(restored));
        }
        Assert.Contains(original.Events, gameEvent => gameEvent is DiceRolled);
        Assert.Equal(original.State.RollCount - originalRolls, restored.State.RollCount);
        Assert.Single(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "arrived" });
        Assert.Single(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "later" });
    }

    [Fact]
    public void Arithmetic_version_continuation_keeps_trigger_edges_and_keyed_checks()
    {
        var original = PlayToSquare(Compile(new()));
        var updated = Compile(new() { TalemarkVersion = 2 });
        var restored = Restore(updated, original.Save());
        Assert.NotNull(restored.Continuation);
        AssertEquivalentState();
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        foreach (var label in new[] { "Back to market", "Haggle", "Back to market", "Haggle" })
        {
            original.Choose(KeyOf(original.Current, label));
            restored.Choose(KeyOf(restored.Current, label));
            AssertEquivalentState();
        }
        Assert.Single(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "later" });
        Assert.Equal(original.Events.OfType<DiceRolled>().Last(), restored.Events.OfType<DiceRolled>().Last());

        void AssertEquivalentState()
        {
            Assert.Equal(original.Current.Scene, restored.Current.Scene);
            Assert.Equal(original.State.GetVariable(Gold), restored.State.GetVariable(Gold));
            Assert.Equal(original.State.GetVariable(Bonus), restored.State.GetVariable(Bonus));
            Assert.Equal(original.State.GetFlag(new FlagId("later")), restored.State.GetFlag(new FlagId("later")));
            Assert.Equal(original.State.GetBaseStat(EntityId.Player, Hp), restored.State.GetBaseStat(EntityId.Player, Hp));
            Assert.Equal(original.State.VisitCount(new SceneId("market")), restored.State.VisitCount(new SceneId("market")));
            Assert.Equal(original.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        }
    }

    [Fact]
    public void Item_group_order_and_values_survive_checkpoint_save_load_and_new_property_fill()
    {
        var originalStory = CompileGroupedItems();
        var sword = new ItemId("sword");
        var log = new EventLog(originalStory.World, 42);
        log.Append(new ItemGroupsRestored(EntityId.Player, sword,
        [
            new("1", [ItemPropertySnapshot.From("durability",
                new ItemPropertyValue.Number(ExactNumber.FromInteger(100)))]),
            new("2",
            [
                ItemPropertySnapshot.From("durability", new ItemPropertyValue.Number(ExactNumber.FromInteger(100))),
                ItemPropertySnapshot.From("color", new ItemPropertyValue.Text("blue")),
            ]),
        ]));
        var checkpoint = CaptureCheckpoint(originalStory, log);
        var bytes = SaveCheckpoint(originalStory, checkpoint);

        var updatedBlue = StoryHistory.RestoreFromCheckpoint(CompileGroupedItems("blue"), bytes);

        Assert.Collection(updatedBlue.State.GetItemGroups(EntityId.Player, sword),
            group =>
            {
                Assert.Equal(ExactNumber.FromInteger(3), group.Quantity);
                Assert.Equal(new ItemPropertyValue.Text("blue"), group.Properties["color"]);
            });

        var updatedGreen = StoryHistory.RestoreFromCheckpoint(CompileGroupedItems("green"), updatedBlue.Save());

        Assert.Collection(updatedGreen.State.GetItemGroups(EntityId.Player, sword),
            group =>
            {
                Assert.Equal(ExactNumber.FromInteger(3), group.Quantity);
                Assert.Equal(new ItemPropertyValue.Text("blue"), group.Properties["color"]);
            });
    }

    [Fact]
    public void Checkpoints_without_item_groups_load_as_one_group_per_held_item()
    {
        var story = CompileGroupedItems("blue");
        var log = new EventLog(story.World, 42);
        log.Append(new ItemsGranted(EntityId.Player, new ItemId("sword"), 3));
        var checkpoint = CaptureCheckpoint(story, log);
        checkpoint.Characters.Single(character => character.Id == "player").ItemGroups = null;
        var bytes = SaveCheckpoint(story, checkpoint);

        var restored = StoryHistory.RestoreFromCheckpoint(story, bytes);

        Assert.Collection(restored.State.GetItemGroups(EntityId.Player, new ItemId("sword")),
            group =>
            {
                Assert.Equal(ExactNumber.FromInteger(3), group.Quantity);
                Assert.Equal(new ItemPropertyValue.Number(ExactNumber.FromInteger(100)),
                    group.Properties["durability"]);
                Assert.Equal(new ItemPropertyValue.Text("blue"), group.Properties["color"]);
            });
    }

    [Fact]
    public void Updated_current_scene_is_reentered_once_with_its_new_text_choices_and_entry_effects()
    {
        var original = PlayToSquare(Compile(new()));
        var gold = original.State.GetVariable(Gold);
        var checkNote = original.Current.Document.Blocks.OfType<RenderedNote>().Single().Text;
        var updated = Compile(new() { Version = "2", SquareUpdate = true });

        var restored = Restore(updated, original.Save());

        Assert.NotNull(restored.Continuation);
        Assert.Equal(gold + 10, restored.State.GetVariable(Gold));
        Assert.Equal(1, restored.State.VisitCount(new SceneId("square")));
        Assert.Contains("The square has changed.", restored.Current.Document.ToPlainText());
        Assert.Equal(checkNote, restored.Current.Document.Blocks.OfType<RenderedNote>().Single().Text);
        Assert.Contains(restored.Current.Choices, choice => Label(choice.Label) == "Visit the tavern");
        Assert.DoesNotContain("The square has changed.", Plain(restored.Transcript.SkipLast(1)));
    }

    [Fact]
    public void Changed_recorded_choice_outcome_is_not_reapplied_when_continuing()
    {
        var original = PlayToSquare(Compile(new()));
        var bytes = original.Save();
        var unchanged = bytes.ToArray();
        var replayOnly = SaveFormat.Write(SaveFormat.Read(bytes) with { Continuation = null });
        var updated = Compile(new() { Version = "2", ChangedChoiceEffect = true });

        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, replayOnly));
        Assert.Contains("has changed", error.Message);

        var restored = StoryHistory.Restore(updated, bytes);

        Assert.NotNull(restored.Continuation);
        Assert.False(restored.Continuation.RedoDiscarded);
        Assert.Equal(original.State.GetVariable(Gold), restored.State.GetVariable(Gold));
        Assert.Equal(original.State.GetVariable(Bonus), restored.State.GetVariable(Bonus));
        Assert.Equal(original.Current.Scene, restored.Current.Scene);
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        Assert.False(restored.CanBack);
        Assert.False(restored.CanForward);
        Assert.Equal(unchanged, bytes);

        var gold = restored.State.GetVariable(Gold);
        restored.Choose(KeyOf(restored.Current, "Return to the road"));
        restored.Choose(KeyOf(restored.Current, "Go to market"));
        Assert.Equal(gold + 2, restored.State.GetVariable(Gold));
    }

    [Fact]
    public void Incompatible_replay_executes_its_valid_prefix_once_before_checkpoint_fallback()
    {
        var original = PlayToSquare(Compile(new()));
        var updated = Compile(new() { Version = "2", ChangedHaggleOutcome = true });
        var replayed = new List<ReplayTransitionKind>();

        var restored = StoryHistory.Restore(updated, original.Save(), replayed.Add);

        Assert.NotNull(restored.Continuation);
        Assert.Equal([ReplayTransitionKind.SavedChoice], replayed);
        Assert.Equal(Snapshot(original), Snapshot(restored));
    }

    [Fact]
    public void Continuation_base_replay_executes_redo_once_and_keeps_the_middle_cursor()
    {
        var original = PlayToSquare(Compile(new()));
        var updated = Compile(new() { Version = "2", HpLoss = true });
        var continued = Restore(updated, original.Save());
        continued.Choose(KeyOf(continued.Current, "Back to market"));
        continued.Back();
        var bytes = continued.Save();
        var save = SaveFormat.Read(bytes);
        Assert.NotNull(save.Continuation!.Payload.Base);
        Assert.Equal(0, save.Cursor);

        var replayed = new List<ReplayTransitionKind>();
        var restored = StoryHistory.Restore(updated, bytes, replayed.Add);

        Assert.Equal(save.Choices.Count, replayed.Count);
        Assert.All(replayed, kind => Assert.Equal(ReplayTransitionKind.SavedChoice, kind));
        Assert.Equal(continued.Events, restored.Events);
        Assert.Equal(continued.Current.Scene, restored.Current.Scene);
        Assert.Equal(0, restored.Cursor);
        Assert.True(restored.CanForward);
        restored.Forward();
        continued.Forward();
        Assert.Equal(Snapshot(continued), Snapshot(restored));
    }

    [Fact]
    public void Conditional_prose_alone_keeps_replay_and_complete_undo()
    {
        var original = PlayToSquare(Compile(new()));
        var updated = Compile(new() { Version = "2", ConditionalProse = true });

        var bytes = original.Save();
        var resilient = Restore(updated, bytes);

        Assert.Null(resilient.Continuation);
        Assert.Equal(original.Events, resilient.Events);
        Assert.True(resilient.CanBack);
        Assert.Contains("You remember the market.", Plain(resilient.Transcript));
        resilient.Back();
        Assert.True(resilient.CanForward);
        resilient.Forward();
        Assert.Equal(original.Cursor, resilient.Cursor);
    }

    [Fact]
    public void Save_after_back_keeps_redo_when_replayable_and_discards_it_at_a_new_undo_boundary()
    {
        var story = Compile(new());
        var original = PlayToSquare(story);
        original.Choose(KeyOf(original.Current, "Back to market"));
        original.Back();
        var bytes = original.Save();

        var unchanged = Restore(story, bytes);
        Assert.Null(unchanged.Continuation);
        Assert.Equal(original.Cursor, unchanged.Cursor);
        Assert.True(unchanged.CanForward);

        var updated = Compile(new() { Version = "2", HpLoss = true });
        var restored = Restore(updated, bytes);
        Assert.True(restored.Continuation is { RedoDiscarded: true });
        Assert.False(restored.CanBack);
        Assert.False(restored.CanForward);
        Assert.Equal(new SceneId("square"), restored.Current.Scene);
        var events = restored.Events.Count;
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        Assert.Equal(events, restored.Events.Count);

        // Play after the boundary is undoable again, and survives a save that starts replay from the boundary.
        var boundary = Snapshot(restored);
        restored.Choose(KeyOf(restored.Current, "Back to market"));
        restored.Choose(KeyOf(restored.Current, "Haggle"));
        restored.Back();
        var reloaded = Restore(updated, restored.Save());
        Assert.Null(reloaded.Continuation);
        Assert.Equal(Snapshot(restored), Snapshot(reloaded));
        Assert.Equal(Plain(restored.Transcript), Plain(reloaded.Transcript));
        Assert.True(reloaded.CanForward);
        reloaded.Back();
        Assert.False(reloaded.CanBack);
        Assert.Equal(boundary, Snapshot(reloaded));
        Assert.Equal("This playthrough continued across a story update, so it can't be opened in the Author Debugger. Use a save made from the start with the current story version.",
            Assert.Throws<InvalidOperationException>(() => reloaded.ForkAuthor()).Message);
        Assert.NotNull(original.ForkAuthor());
    }

    [Fact]
    public void Added_declarations_take_defaults_and_removed_ones_are_retained_for_later_saves()
    {
        var original = PlayToSquare(Compile(new()));
        Assert.Equal(2, original.State.GetVariable(new VarId("legacy")));
        var bytes = original.Save();
        var replayOnly = SaveFormat.Write(SaveFormat.Read(bytes) with { Continuation = null });
        var updated = Compile(new() { Version = "2", Legacy = false, AddedCounter = true });

        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(new() { AddedCounter = true }), replayOnly));
        var restored = Restore(updated, bytes);

        Assert.Equal(5, restored.State.GetVariable(new VarId("counter")));
        Assert.Equal(0, restored.State.GetVariable(new VarId("legacy")));
        Assert.Contains(restored.Continuation!.Notes, note => note.Contains("'legacy'", StringComparison.Ordinal));
        Assert.Equal(original.State.GetVariable(Gold), restored.State.GetVariable(Gold));

        var downgraded = Restore(Compile(new()), restored.Save());
        Assert.Equal(2, downgraded.State.GetVariable(new VarId("legacy")));
    }

    [Fact]
    public void Type_changed_or_structurally_missing_state_rejects_without_changing_the_save_or_active_history()
    {
        var original = PlayToSquare(Compile(new()));
        var bytes = original.Save();
        var unchanged = bytes.ToArray();
        var active = PlayToSquare(Compile(new()));
        var activeBefore = active.Save();

        var typeChange = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(Compile(new() { Version = "2", NicknameNumber = true }), bytes));
        var missingScene = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(Compile(new() { Version = "2", RemoveSquare = true }), bytes));
        var damaged = bytes.ToArray();
        damaged[^1] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(new()), damaged));

        Assert.Equal("This save cannot continue in the updated story because the variable 'nickname' changed from text to a number. Open it with the story version that created it.",
            typeChange.Message);
        Assert.StartsWith("This save cannot continue in the updated story because ", missingScene.Message);
        Assert.Contains("'square'", missingScene.Message, StringComparison.Ordinal);
        Assert.EndsWith("Open it with the story version that created it.", missingScene.Message);
        Assert.Equal(unchanged, bytes);
        Assert.Equal(activeBefore, active.Save());
    }

    [Fact]
    public void Removed_values_keep_their_kind_and_are_restored_only_when_redeclared_with_that_kind()
    {
        var original = PlayToSquare(Compile(new()));
        var bytes = original.Save();
        var removed = Restore(Compile(new() { Version = "2", Legacy = false }), bytes);
        var retainedBytes = removed.Save();
        var copy = retainedBytes.ToArray();

        var error = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(Compile(new() { Version = "3", LegacyText = true }), retainedBytes));
        Assert.Equal("This save cannot continue in the updated story because the variable 'legacy' changed from a number to text. Open it with the story version that created it.",
            error.Message);
        Assert.Equal(copy, retainedBytes);
        var returned = Restore(Compile(new() { Version = "4" }), retainedBytes);
        Assert.Equal(2, returned.State.GetVariable(new VarId("legacy")));

        var number = PlayToSquare(Compile(new() { NicknameNumber = true })).Save();
        var numberCopy = number.ToArray();
        var reverse = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(new() { Version = "2" }), number));
        Assert.Equal("This save cannot continue in the updated story because the variable 'nickname' changed from a number to text. Open it with the story version that created it.",
            reverse.Message);
        Assert.Equal(numberCopy, number);
    }

    [Fact]
    public void New_items_take_initial_counts_and_removed_known_zero_items_and_fired_triggers_are_retained()
    {
        var original = PlayToSquare(Compile(new()));
        var updated = Compile(new() { Version = "2", IncludeRope = false, AddPotion = true, RemoveArrived = true });
        var removed = Restore(updated, original.Save());
        Assert.Equal(3, removed.State.GetItemCount(EntityId.Player, new ItemId("potion")));
        Assert.Contains(removed.Continuation!.Notes, note => note.Contains("'arrived'", StringComparison.Ordinal));
        Assert.Contains(removed.Continuation.Notes, note => note.Contains("'rope'", StringComparison.Ordinal));
        var saved = SaveFormat.Read(removed.Save()).Continuation!.Payload.Current!;
        Assert.Contains("arrived", saved.FiredTriggers);
        Assert.Contains("arrived", saved.KnownTriggers);
        Assert.Contains("rope", saved.KnownItems);

        var returned = Restore(Compile(new() { Version = "3", RopeInitial = 5 }), removed.Save());
        Assert.Equal(0, returned.State.GetItemCount(EntityId.Player, new ItemId("rope")));
        Assert.True(returned.State.HasFiredTrigger("arrived"));
    }

    [Fact]
    public void A_narrower_stat_range_clamps_saved_values_and_reports_the_change()
    {
        var original = new StoryHistory(CompileStat(maximum: 20, initial: 15, version: "1"), 42);
        original.Choose("c0");
        var restored = Restore(CompileStat(maximum: 10, initial: 5, version: "2"), original.Save());

        Assert.Equal(10, restored.State.GetBaseStat(EntityId.Player, new StatId("nerve")));
        Assert.Contains(restored.Continuation!.Notes,
            note => note.Contains("nerve", StringComparison.Ordinal) && note.Contains("clamped to 10", StringComparison.Ordinal));
    }

    [Fact]
    public void Trigger_edges_prime_new_triggers_preserve_existing_edges_and_prevent_once_refiring()
    {
        var original = PlayToSquare(Compile(new()));
        var save = SaveFormat.Read(original.Save());
        save.Continuation!.Payload.Current!.TriggerEdges.Remove("arrived");
        var bytes = SaveFormat.Write(save);
        var updated = Compile(new() { Version = "2", ChangedTrigger = true, AddTrueTrigger = true });

        var restored = Restore(updated, bytes);

        Assert.Equal(1, restored.State.GetVariable(new VarId("changed-count")));
        Assert.Equal(0, restored.State.GetVariable(new VarId("new-count")));
        Assert.Single(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "changed-edge" });
        Assert.Single(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "arrived" });
        Assert.DoesNotContain(restored.Events, gameEvent => gameEvent is TriggerFired { TriggerId: "new-trigger" });
        restored.Choose(KeyOf(restored.Current, "Back to market"));
        Assert.Equal(1, restored.State.GetVariable(new VarId("changed-count")));
        Assert.Equal(0, restored.State.GetVariable(new VarId("new-count")));
    }

    [Fact]
    public void Progress_identity_includes_the_base_but_not_current_checkpoint_or_transcript()
    {
        var first = PlayToSquare(Compile(new()));
        var second = PlayToSquare(first.Story);
        second.Choose(KeyOf(second.Current, "Back to market"));
        second.Choose(KeyOf(second.Current, "Haggle"));
        var updated = Compile(new() { Version = "2", HpLoss = true });
        var source = first.Save();
        var sourceKey = StoryHistory.DescribeSave("source", source).ProgressKey;
        var a = Restore(updated, source);
        var b = Restore(updated, second.Save());
        Assert.Equal(sourceKey, a.RestoredProgressKey);
        Assert.Equal(0, a.Cursor);
        Assert.Equal(0, b.Cursor);
        Assert.NotEqual(a.ProgressKey, b.ProgressKey);

        var saved = SaveFormat.Read(a.Save());
        var checkpoint = saved.Continuation!.Payload;
        var previousKey = StoryHistory.DescribeSave("one", SaveFormat.Write(saved)).ProgressKey;
        checkpoint.Transcript![0].Blocks[0].Inlines![0].Text = "Different saved prose.";
        checkpoint.Current!.Variables.Single(value => value.Id == "gold").Value += 1;
        var changed = SaveFormat.Write(saved);
        Assert.Equal(previousKey, StoryHistory.DescribeSave("two", changed).ProgressKey);
    }

    [Fact]
    public void Multiple_update_boundaries_keep_both_transcript_segments_in_order()
    {
        var original = PlayToSquare(Compile(new()));
        var first = Restore(Compile(new() { Version = "2", HpLoss = true }), original.Save());
        var firstPrefix = Plain(first.Transcript.Take(first.Transcript.Count - 1));
        first.Choose(KeyOf(first.Current, "Back to market"));
        var beforeSecond = first.Save();
        var sourceFrames = Plain(first.Transcript.Take(first.Transcript.Count - 1));

        var second = Restore(Compile(new() { Version = "3", HpLoss = true, AddedCounter = true }), beforeSecond);

        Assert.NotNull(second.Continuation);
        Assert.StartsWith(firstPrefix, Plain(second.Transcript));
        Assert.Equal(sourceFrames, Plain(second.Transcript.Take(second.Transcript.Count - 1)));
        Assert.Equal(first.PlaythroughLength, second.PlaythroughLength);
        var savedAgain = SaveFormat.Read(second.Save()).Continuation!.Payload;
        Assert.Equal(first.Transcript.Count - 1, savedAgain.BaseTranscriptLength);
        Assert.Equal(second.PlaythroughLength, savedAgain.Transcript!.Count);
    }

    [Fact]
    public void Continuing_a_direct_ending_preserves_its_source_scene_and_transcript()
    {
        var original = PlayToSquare(Compile(new()));
        original.Choose(KeyOf(original.Current, "Rest"));
        var bytes = original.Save();
        var copy = bytes.ToArray();

        var restored = StoryHistory.RestoreFromCheckpoint(original.Story, bytes);

        Assert.True(restored.Current.IsEnded);
        Assert.Equal(original.Current.Scene, restored.Current.Scene);
        Assert.Equal(original.Current.Title, restored.Current.Title);
        Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
        Assert.Equal(copy, bytes);
    }

    [Fact]
    public void Updated_current_passage_can_continue_automatically_after_its_entry_effects()
    {
        var original = new StoryHistory(CompileAutomatic(changed: false), 42);
        original.Choose("c0");
        var bytes = original.Save();

        var restored = Restore(CompileAutomatic(changed: true), bytes);

        Assert.NotNull(restored.Continuation);
        Assert.Equal(new SceneId("finish"), restored.Current.Scene);
        Assert.Equal(5, restored.State.GetVariable(new VarId("score")));
        Assert.Equal(1, restored.State.VisitCount(new SceneId("next")));
        Assert.Equal(1, restored.State.VisitCount(new SceneId("finish")));
        Assert.Contains(restored.Transcript, frame => frame.ChosenAutomatically);
        Assert.False(restored.CanBack);
        var reloaded = Restore(restored.Story, restored.Save());
        Assert.Null(reloaded.Continuation);
        Assert.Equal(Snapshot(restored), Snapshot(reloaded));
        Assert.Equal(Plain(restored.Transcript), Plain(reloaded.Transcript));
    }

    [Fact]
    public void Continued_playthroughs_keep_going_past_the_former_10000_choice_cap()
    {
        var original = new StoryHistory(CompileLoop(changed: false), 42);
        for (var i = 0; i < 9_999; i++)
        {
            original.Choose("c0");
        }
        var restored = Restore(CompileLoop(changed: true), original.Save());

        Assert.Equal(9_999, restored.PlaythroughLength);
        restored.Choose("c0");
        restored.Choose("c0");
        Assert.Equal(10_001, restored.PlaythroughLength);
        var reloaded = Restore(restored.Story, restored.Save());
        Assert.Equal(restored.PlaythroughLength, reloaded.PlaythroughLength);
        reloaded.Choose("c0");
        Assert.Equal(10_002, reloaded.PlaythroughLength);
    }

    [Fact]
    public void Format_five_rejects_malformed_continuation_shapes_before_deserialization()
    {
        var source = PlayToSquare(Compile(new())).Save();
        var oversizedHeader = ReplaceContinuation(source, (ref MessagePackWriter writer) =>
        {
            writer.WriteArrayHeader(4);
            writer.WriteArrayHeader(int.MaxValue);
            writer.WriteNil();
            writer.WriteNil();
            writer.Write(0);
        });
        var map = ReplaceContinuation(source, (ref MessagePackWriter writer) => writer.WriteMapHeader(0));
        AssertMalformed(oversizedHeader);
        AssertMalformed(map);

        var invalidLength = SaveFormat.Read(source);
        var invalidPayload = invalidLength.Continuation!.Payload;
        invalidPayload.Base = invalidPayload.Current;
        invalidPayload.BaseTranscriptLength = invalidPayload.Transcript!.Count + 1;
        invalidLength = invalidLength with { ReplayEvidence = null, ReplayChoiceIdentities = null };
        AssertMalformed(SaveFormat.Write(invalidLength));

        var baseWithEvidence = SaveFormat.Read(source);
        baseWithEvidence.Continuation!.Payload.Base = baseWithEvidence.Continuation.Payload.Current;
        AssertMalformed(SaveFormat.Write(baseWithEvidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checkpoints_reject_visit_counts_exceeding_the_preceding_passages(bool useBase)
    {
        var original = PlayToSquare(Compile(new()));
        var continued = StoryHistory.RestoreFromCheckpoint(original.Story, original.Save());
        var saved = SaveFormat.Read(continued.Save());
        var payload = saved.Continuation!.Payload;
        var checkpoint = useBase ? payload.Base! : payload.Current!;
        checkpoint.Visits[0].Value = int.MaxValue;
        var bytes = SaveFormat.Write(saved);
        var copy = bytes.ToArray();

        AssertMalformed(bytes);
        Assert.Equal(copy, bytes);
    }

    [Fact]
    public void Format_four_ignores_any_trailing_continuation_position()
    {
        var original = PlayToSquare(Compile(new()));
        var bytes = ReplaceContinuation(original.Save(), (ref MessagePackWriter writer) =>
        {
            writer.WriteMapHeader(1);
            writer.Write("future");
            writer.Write(42);
        });
        bytes[4] = 4;
        var copy = bytes.ToArray();

        Assert.Null(SaveFormat.Read(bytes).Continuation);
        var restored = Restore(original.Story, bytes);
        Assert.Null(restored.Continuation);
        Assert.Equal(Snapshot(original), Snapshot(restored));
        Assert.Equal(copy, bytes);
    }

    [Fact]
    public void Format_five_ignores_unknown_trailing_fields_on_numeric_pairs()
    {
        var original = PlayToSquare(Compile(new()));
        var saved = SaveFormat.Read(original.Save());
        var variable = saved.Continuation!.Payload.Current!.Variables.Single(value => value.Id == "gold");
        var oldValue = variable.Value;
        variable.ExactValue = "1/3";
        var bytes = SaveFormat.Write(saved);
        bytes[4] = 5;

        var decoded = SaveFormat.Read(bytes);
        var decodedVariable = decoded.Continuation!.Payload.Current!.Variables.Single(value => value.Id == "gold");
        Assert.Equal(oldValue, decodedVariable.Value);
        Assert.Null(decodedVariable.ExactValue);
        Assert.Equal(Snapshot(original), Snapshot(StoryHistory.Restore(original.Story, bytes)));
    }

    [Fact]
    public void Continuation_truncations_and_single_bit_flips_are_rejected_or_preserve_play()
    {
        var original = PlayToSquare(Compile(new()));
        var bytes = original.Save();
        var copy = bytes.ToArray();
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes.AsSpan(0, length)));
        }
        for (var bit = 0; bit < bytes.Length * 8; bit++)
        {
            var mutated = bytes.ToArray();
            mutated[bit / 8] ^= (byte)(1 << (bit % 8));
            try
            {
                var restored = StoryHistory.Restore(original.Story, mutated);
                Assert.Equal(Snapshot(original), Snapshot(restored));
                Assert.Equal(Plain(original.Transcript), Plain(restored.Transcript));
            }
            catch (InvalidDataException)
            {
            }
        }
        Assert.Equal(copy, bytes);
    }

    [Fact]
    public void Continuation_has_the_frozen_positional_layout()
    {
        var history = PlayToSquare(Compile(new()));
        var bytes = history.Save();
        var body = SaveFormat.EncodeBody(SaveFormat.Read(bytes));
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(body));
        Assert.Equal(12, reader.ReadArrayHeader());
        for (var i = 0; i < 10; i++) reader.Skip();
        Assert.Equal(4, reader.ReadArrayHeader());
        Assert.Equal(13, reader.ReadArrayHeader());
        Assert.Equal("square", reader.ReadString());
        Assert.Equal("market", reader.ReadString());
        Assert.Equal(1, reader.ReadArrayHeader());
        Assert.Equal(7, reader.ReadArrayHeader());
        Assert.Equal("player", reader.ReadString());
        Assert.Equal("Robin", reader.ReadString());
        Assert.Equal("", reader.ReadString());
        Assert.Equal(6, reader.ReadArrayHeader());
        for (var i = 0; i < 5; i++) Assert.NotNull(reader.ReadString());
        Assert.InRange(reader.ReadInt32(), 0, 1);
        ReadSortedPairs(ref reader, textValues: false);
        ReadSortedPairs(ref reader, textValues: false);
        Assert.Equal(0, reader.ReadArrayHeader());
        ReadSortedPairs(ref reader, textValues: false);
        ReadSortedPairs(ref reader, textValues: false);
        ReadSortedPairs(ref reader, textValues: false);
        ReadSortedPairs(ref reader, textValues: true);
        ReadSortedPairs(ref reader, textValues: false);
        for (var i = 0; i < 4; i++) ReadSortedStrings(ref reader);
        ReadBlocks(ref reader);

        Assert.Equal(history.Cursor, reader.ReadArrayHeader());
        for (var i = 0; i < history.Cursor; i++)
        {
            Assert.Equal(8, reader.ReadArrayHeader());
            reader.Skip();
            reader.Skip();
            ReadBlocks(ref reader);
            if (!reader.TryReadNil()) ReadInlines(ref reader);
            for (var flag = 0; flag < 4; flag++) reader.ReadBoolean();
        }
        Assert.True(reader.TryReadNil());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.True(reader.End);
    }

    [Fact]
    public void Transcript_tokens_cover_all_rendered_kinds_and_enum_values()
    {
        foreach (var kind in Enum.GetValues<NoteKind>())
            Assert.Equal(kind, TranscriptCodec.DecodeNoteKind(TranscriptCodec.EncodeNoteKind(kind)));
        foreach (var style in Enum.GetValues<TextStyle>())
            Assert.Equal(style, TranscriptCodec.DecodeStyle(TranscriptCodec.EncodeStyle(style)));
        foreach (var accessibility in Enum.GetValues<MediaAccessibility>())
            Assert.Equal(accessibility, TranscriptCodec.DecodeAccessibility(TranscriptCodec.EncodeAccessibility(accessibility)));
        foreach (var agreement in Enum.GetValues<GrammaticalNumber>())
        {
            var pronouns = new Pronouns("they", "them", "their", "theirs", "themself", agreement);
            Assert.Equal(pronouns, PronounsPayload.From(pronouns).ToPronouns());
        }

        RenderedInline[] inlines =
        [
            new RenderedText("text", TextStyle.Emphasis | TextStyle.Strong | TextStyle.Code),
            new RenderedLineBreak(),
            new RenderedBlankLineInline(),
            new RenderedMedia("image.png", "alt") { Accessibility = MediaAccessibility.Decorative },
        ];
        RenderedBlock[] blocks =
        [
            new RenderedParagraph(inlines),
            new RenderedBlankLine(),
            new RenderedSpeech(EntityId.Player, "Robin", [inlines]),
            new RenderedNote(NoteKind.Change, "changed"),
        ];
        Assert.Equal(RenderedTypes<RenderedInline>(), inlines.Select(value => value.GetType()).OrderBy(type => type.Name));
        Assert.Equal(RenderedTypes<RenderedBlock>(), blocks.Select(value => value.GetType()).OrderBy(type => type.Name));
        var encoded = TranscriptCodec.Encode(blocks);
        Assert.Equal([0, 1, 2, 3], encoded.Select(block => block.Kind));
        Assert.Equal([0, 1, 2, 3], encoded[0].Inlines!.Select(inline => inline.Kind));
        Assert.Equal([0, 1, 2], new[] { NoteKind.Check, NoteKind.Change, NoteKind.Info }.Select(TranscriptCodec.EncodeNoteKind));
        Assert.Equal([0, 1, 2, 4], new[] { TextStyle.None, TextStyle.Emphasis, TextStyle.Strong, TextStyle.Code }.Select(TranscriptCodec.EncodeStyle));
        Assert.Equal([0, 1], new[] { MediaAccessibility.Descriptive, MediaAccessibility.Decorative }.Select(TranscriptCodec.EncodeAccessibility));
        var decoded = TranscriptCodec.Decode(encoded);
        Assert.Equal(blocks.Select(block => block.ToPlainText()), decoded.Select(block => block.ToPlainText()));
        var paragraph = Assert.IsType<RenderedParagraph>(decoded[0]);
        Assert.Equal(TextStyle.Emphasis | TextStyle.Strong | TextStyle.Code,
            Assert.IsType<RenderedText>(paragraph.Inlines[0]).Style);
        Assert.Equal(MediaAccessibility.Decorative, Assert.IsType<RenderedMedia>(paragraph.Inlines[3]).Accessibility);
    }

    [Fact]
    public void Continuation_data_is_opaque_to_public_callers()
    {
        Assert.Empty(typeof(SaveContinuation).GetConstructors());
        Assert.Empty(typeof(SaveContinuation).GetMembers(
            System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
    }

    private static IEnumerable<Type> RenderedTypes<T>() =>
        typeof(T).Assembly.GetTypes().Where(type => !type.IsAbstract && typeof(T).IsAssignableFrom(type))
            .OrderBy(type => type.Name);

    private static void AssertMalformed(byte[] bytes)
    {
        Assert.Equal(SaveFormat.MalformedMessage, Assert.Throws<InvalidDataException>(() => SaveFormat.Read(bytes)).Message);
        var legacy = bytes.ToArray();
        legacy[4] = 5;
        Assert.Equal(SaveFormat.MalformedMessage, Assert.Throws<InvalidDataException>(() => SaveFormat.Read(legacy)).Message);
    }

    private delegate void WriteContinuation(ref MessagePackWriter writer);

    private static byte[] ReplaceContinuation(byte[] bytes, WriteContinuation write)
    {
        var body = SaveFormat.EncodeBody(SaveFormat.Read(bytes));
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(body));
        Assert.Equal(12, reader.ReadArrayHeader());
        var output = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(output);
        writer.WriteArrayHeader(12);
        for (var i = 0; i < 10; i++)
        {
            var field = reader.ReadRaw();
            writer.WriteRaw(field);
        }
        write(ref writer);
        reader.Skip();
        writer.WriteRaw(reader.ReadRaw());
        writer.Flush();
        return SaveFormat.Pack(output.WrittenSpan);
    }

    private static void ReadSortedPairs(ref MessagePackReader reader, bool textValues)
    {
        var count = reader.ReadArrayHeader();
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(textValues ? 2 : 3, reader.ReadArrayHeader());
            ids.Add(reader.ReadString()!);
            if (textValues) Assert.NotNull(reader.ReadString());
            else
            {
                reader.ReadInt32();
                if (!reader.TryReadNil()) Assert.NotNull(reader.ReadString());
            }
        }
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    private static void ReadSortedStrings(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++) ids.Add(reader.ReadString()!);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    private static void ReadBlocks(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(6, reader.ReadArrayHeader());
            reader.ReadInt32();
            if (!reader.TryReadNil()) ReadInlines(ref reader);
            reader.Skip();
            reader.Skip();
            if (!reader.TryReadNil())
            {
                var paragraphs = reader.ReadArrayHeader();
                for (var j = 0; j < paragraphs; j++) ReadInlines(ref reader);
            }
            reader.ReadInt32();
        }
    }

    private static void ReadInlines(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(5, reader.ReadArrayHeader());
            reader.ReadInt32();
            reader.Skip();
            reader.ReadInt32();
            reader.Skip();
            reader.ReadInt32();
        }
    }

    private static StoryHistory PlayToSquare(StoryDefinition story)
    {
        var history = new StoryHistory(story, 42);
        history.Choose(KeyOf(history.Current, "Go to market"));
        history.Choose(KeyOf(history.Current, "Haggle"));
        return history;
    }

    private static string Snapshot(StoryHistory history)
    {
        var state = history.State;
        var world = history.Story.World;
        return string.Join('\n', [
            .. world.Variables.Keys.OrderBy(id => id.Value).Select(id => $"{id}={state.GetVariable(id)}"),
            .. world.TextVariables.Keys.OrderBy(id => id.Value).Select(id => $"{id}={state.GetTextVariable(id)}"),
            .. world.Flags.OrderBy(id => id.Value).Select(id => $"{id}={state.GetFlag(id)}"),
            .. world.Ruleset.Stats.OrderBy(stat => stat.Id.Value)
                .Select(stat => $"{stat.Id}={state.GetBaseStat(EntityId.Player, stat.Id)}"),
            .. world.Items.Keys.OrderBy(id => id.Value).Select(id => $"{id}={state.GetItemCount(EntityId.Player, id)}"),
            .. world.Factions.Keys.OrderBy(id => id.Value).Select(id => $"{id}={state.GetStanding(id)}"),
            $"character={state.GetCharacterDefinition(EntityId.Player)}",
            .. history.Story.Scenes.Select(scene => $"{scene.Id}#{state.VisitCount(scene.Id)}"),
            .. history.Story.Triggers.Select(trigger => $"{trigger.Id}!{state.HasFiredTrigger(trigger.Id)}"),
            $"at={state.CurrentScene}",
            history.Current.Document.ToPlainText(),
            .. history.Current.Choices.Select(choice => $"{choice.Key}:{Label(choice.Label)}"),
        ]);
    }

    private static string Plain(IEnumerable<HistoryFrame> frames) => string.Join("\n---\n",
        frames.Select(frame => $"{frame.Frame.Scene}|{frame.Frame.Document.ToPlainText()}|{(frame.Chosen is null ? "" : Label(frame.Chosen))}"));

    private static string Label(IEnumerable<RenderedInline> label) => RenderedInline.ToPlainText(label);

    private static string KeyOf(StoryFrame frame, string label) =>
        frame.Choices.Single(choice => Label(choice.Label) == label).Key;

    private static StoryHistory Restore(StoryDefinition story, byte[] bytes)
    {
        var copy = bytes.ToArray();
        var history = StoryHistory.Restore(story, bytes);
        Assert.Equal(copy, bytes);
        return history;
    }

    [Fact]
    public void Group_item_variable_round_trips_checkpoint_and_takes_the_matching_group_after_load()
    {
        var compilation = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game: {id: group-variable, title: Group variable, start: start, talemark: 2}
                characters:
                  player: {first-name: Ada}
                items:
                  sword: {name: old sword, sharpness: 5, material: "iron", magical: false}
                """,
            ["main.tale"] = """
                === start
                ~ var:chosen = item:sword
                -> next
                === next
                {var:chosen}: {var:chosen:sharpness}, {var:chosen:material}, {var:chosen:magical}; {player:inventory:var:chosen:count}.
                * [Spend chosen group] (requires player has var:chosen) { take player var:chosen 2 } -> after
                === after
                {var:chosen}: {player:inventory:var:chosen:count}.
                * [Finish] -> END
                """,
        }));
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));
        var story = compilation.Story!;
        var sword = new ItemId("sword");
        var chosen = new VarId("chosen");
        var log = new EventLog(story.World, 42);
        log.Append(new ItemsGranted(EntityId.Player, sword, 3));
        var reference = ItemReference.ForGroup(EntityId.Player, sword, log.State.GetItemGroups(EntityId.Player, sword)[0]);
        log.AppendRange(StateChanges.SetTextVariable(log.State, chosen, reference.Encode()));
        log.AppendRange(StateChanges.SetItemProperty(log.State, EntityId.Player, sword, "sharpness",
            new ItemPropertyValue.Number(7)));
        var bytes = SaveCheckpoint(story, CaptureCheckpoint(story, log));
        var restored = StoryHistory.RestoreFromCheckpoint(story, bytes);
        Assert.Equal(reference.Encode(), restored.State.GetTextVariable(chosen));
        Assert.Equal("old sword: 5, iron, false; 2.", restored.Current.Document.ToPlainText());
        restored.Choose(restored.Current.Choices.Single().Key);
        Assert.Equal("[Lost: old sword ×2]\n\n: 0.", restored.Current.Document.ToPlainText());
        var remaining = Assert.Single(restored.State.GetItemGroups(EntityId.Player, sword));
        Assert.Equal(ExactNumber.One, remaining.Quantity);
        Assert.Equal(new ItemPropertyValue.Number(7), remaining.Properties["sharpness"]);
        var replayed = StoryHistory.Restore(story, restored.Save());
        Assert.Equal("[Lost: old sword ×2]\n\n: 0.", replayed.Current.Document.ToPlainText());
        Assert.Equal(reference.Encode(), replayed.State.GetTextVariable(chosen));
        Assert.Equal(ExactNumber.One, Assert.Single(replayed.State.GetItemGroups(EntityId.Player, sword)).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Group_item_variable_survives_continuation_when_item_gains_a_property(bool originallyHadProperty)
    {
        StoryDefinition CompileVersion(bool upgraded)
        {
            var sharpness = originallyHadProperty ? "    sharpness: 5\n" : "";
            var material = upgraded ? "    material: iron\n" : "";
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = $$"""
                    game: {id: group-upgrade, title: Group upgrade, start: start, talemark: 2}
                    characters:
                      player: {first-name: Ada}
                    items:
                      sword:
                        name: sword
                    {{sharpness}}{{material}}
                    """,
                ["main.tale"] = """
                    === start
                    ~ var:chosen = item:sword
                    -> next
                    === next
                    {var:chosen}: {player:inventory:var:chosen:count}.
                    * [Use] (requires player has var:chosen) { take player var:chosen 2 } -> after
                    === after
                    {var:chosen}: {player:inventory:var:chosen:count}.
                    -> END
                    """,
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!;
        }

        var original = CompileVersion(false);
        var sword = new ItemId("sword");
        var chosen = new VarId("chosen");
        var log = new EventLog(original.World, 42);
        log.Append(new ItemsGranted(EntityId.Player, sword, 2));
        var reference = ItemReference.ForGroup(EntityId.Player, sword, log.State.GetItemGroups(EntityId.Player, sword)[0]);
        log.AppendRange(StateChanges.SetTextVariable(log.State, chosen, reference.Encode()));
        if (originallyHadProperty)
        {
            log.Append(new ItemsGranted(EntityId.Player, sword, 1));
            log.AppendRange(StateChanges.SetItemProperty(log.State, EntityId.Player, sword, "sharpness",
                new ItemPropertyValue.Number(7)));
        }

        var bytes = SaveCheckpoint(original, CaptureCheckpoint(original, log));
        var saved = StoryHistory.RestoreFromCheckpoint(original, bytes).Save();
        var updated = CompileVersion(true);
        var restored = Restore(updated, saved);

        Assert.NotNull(restored.Continuation);
        Assert.Equal(reference.Encode(), restored.State.GetTextVariable(chosen));
        Assert.Equal("sword: 2.", restored.Current.Document.ToPlainText());
        Assert.Equal(new ItemPropertyValue.Text("iron"),
            restored.State.GetItemGroups(EntityId.Player, sword)[0].Properties["material"]);
        var resumed = StoryHistory.Restore(updated, restored.Save());
        Assert.Equal("sword: 2.", resumed.Current.Document.ToPlainText());
        resumed.Choose(resumed.Current.Choices.Single().Key);
        Assert.Equal("[Lost: sword ×2]\n\n: 0.", resumed.Current.Document.ToPlainText());
        if (originallyHadProperty)
        {
            var remaining = Assert.Single(resumed.State.GetItemGroups(EntityId.Player, sword));
            Assert.Equal(new ItemPropertyValue.Number(7), remaining.Properties["sharpness"]);
        }
        else
        {
            Assert.Empty(resumed.State.GetItemGroups(EntityId.Player, sword));
        }
    }

    private static CheckpointPayload CaptureCheckpoint(StoryDefinition story, EventLog log) =>
        ContinuationCheckpoint.Capture(story, log.State.Seed, log.Events,
            new PlayerEntryBoundary(new DivertTarget(new SceneId("next")), log.Events.Count, [], []), null);

    private static byte[] SaveCheckpoint(StoryDefinition story, CheckpointPayload checkpoint) =>
        SaveFormat.Write(new SavedGame
        {
            StoryId = story.Id,
            StoryVersion = story.Version,
            ContentFingerprint = story.ContentFingerprint
                ?? throw new InvalidOperationException("The test story must have a content fingerprint."),
            Seed = 42,
            Choices = [],
            Cursor = 0,
            Continuation = new SaveContinuation(new ContinuationPayload
            {
                Current = checkpoint,
                Transcript = [],
            }),
        });

    private static StoryDefinition CompileGroupedItems(string? color = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                game:
                  id: held-items
                  title: Held items
                  talemark: 2
                  start: start
                characters:
                  player:
                    first-name: Tester
                items:
                  sword:
                    name: sword
                    durability: 100
                    color: {{color ?? "blue"}}
                """,
            ["main.tale"] = "=== start\n* [Next] -> next\n=== next\nReady.\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private sealed record Options
    {
        public string Version { get; init; } = "1";
        public int TalemarkVersion { get; init; } = 1;
        public bool HpLoss { get; init; }
        public bool ConditionalProse { get; init; }
        public bool Legacy { get; init; } = true;
        public bool AddedCounter { get; init; }
        public bool NicknameNumber { get; init; }
        public bool LegacyText { get; init; }
        public bool SquareUpdate { get; init; }
        public bool RemoveSquare { get; init; }
        public bool IncludeRope { get; init; } = true;
        public int RopeInitial { get; init; }
        public bool ChangedHaggleOutcome { get; init; }
        public bool ChangedChoiceEffect { get; init; }
        public bool AddPotion { get; init; }
        public bool RemoveArrived { get; init; }
        public bool ChangedTrigger { get; init; }
        public bool AddTrueTrigger { get; init; }
    }

    private static StoryDefinition Compile(Options options)
    {
        var manifest = new ManifestFaker("resilient", "Resilient", "start")
        {
            Version = options.Version,
            Talemark = options.TalemarkVersion,
            BaseRuleset = "rpg",
        };
        var inventory = new Dictionary<string, int>();
        if (options.IncludeRope)
        {
            manifest.Item("rope", "Rope");
            if (options.RopeInitial > 0) inventory["rope"] = options.RopeInitial;
        }
        if (options.AddPotion)
        {
            manifest.Item("potion", "Potion");
            inventory["potion"] = 3;
        }
        manifest.Character("player", "Robin", stats: new Dictionary<string, int> { ["athletics"] = 2 },
            inventory: inventory);
        var gold = manifest.Number("gold", 0);
        var bonus = manifest.Number("bonus", 0);
        var changedCount = manifest.Number("changed-count", 0);
        var newCount = manifest.Number("new-count", 0);
        var met = manifest.Flag("met");
        var later = manifest.Flag("later");
        var legacy = options.Legacy && !options.LegacyText ? manifest.Number("legacy", 0) : null;
        if (options.LegacyText) manifest.Text("legacy", "old");
        if (options.AddedCounter) manifest.Number("counter", 5);
        if (options.NicknameNumber) manifest.Number("nickname", 3);
        else manifest.Text("nickname", "Robbie");
        if (!options.RemoveArrived)
            manifest.Trigger("arrived", ExpressionFaker.AtLeast(gold, 1), EffectFaker.Set(met), once: true);
        manifest.Trigger("rich", ExpressionFaker.AtLeast(gold, 2), EffectFaker.Increase(bonus, 1));
        manifest.Trigger("later", ExpressionFaker.AtLeast(gold, 4), EffectFaker.Set(later), once: true);
        manifest.Trigger("changed-edge", ExpressionFaker.AtLeast(gold, options.ChangedTrigger ? 1 : 100),
            EffectFaker.Increase(changedCount, 1), once: true);
        if (options.AddTrueTrigger)
            manifest.Trigger("new-trigger", ExpressionFaker.AtLeast(gold, 1), EffectFaker.Increase(newCount, 1), once: true);

        var square = options.RemoveSquare ? "start" : "square";
        var lines = new List<string> { SceneFaker.Header("start"), "The road." };
        if (legacy is not null) lines.Add(EffectFaker.Line(EffectFaker.Increase(legacy, 2)));
        lines.Add(ChoiceFaker.Create("market", "Go to market",
            effect: EffectFaker.Increase(gold, options.ChangedChoiceEffect ? 2 : 1)));
        lines.Add(SceneFaker.Header("market"));
        if (options.HpLoss) lines.Add(EffectFaker.Line(EffectFaker.Decrease(TaleCharacterRef.Player.Field("hp"), 5)));
        lines.Add("The market.");
        if (options.ConditionalProse) lines.Add(IfFaker.Inline(met, "You remember the market."));
        lines.Add(SkillCheckFaker.Choice("Haggle", "athletics", options.ChangedHaggleOutcome ? 13 : 12, square, square,
            EffectFaker.Increase(gold, 2), EffectFaker.Increase(gold, 1)));
        lines.Add(ChoiceFaker.Create("END", "Leave"));
        if (!options.RemoveSquare)
        {
            lines.Add(SceneFaker.Header("square"));
            if (options.SquareUpdate)
            {
                lines.Add("The square has changed.");
                lines.Add(EffectFaker.Line(EffectFaker.Increase(gold, 10)));
            }
            else
            {
                lines.Add("The square.");
            }
            lines.Add(ChoiceFaker.Create("market", "Back to market", effect: EffectFaker.Increase(gold, 1)));
            lines.Add(ChoiceFaker.Create("start", "Return to the road"));
            if (options.SquareUpdate) lines.Add(ChoiceFaker.Create("END", "Visit the tavern"));
            lines.Add(ChoiceFaker.Create("END", "Rest"));
        }

        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n', lines),
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static StoryDefinition CompileStat(int maximum, int initial, string version)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                game:
                  id: clamp-test
                  title: Clamp test
                  start: start
                  version: "{{version}}"
                  ruleset:
                    stats:
                      - { id: nerve, min: 0, max: {{maximum}}, default: 1 }
                characters:
                  player:
                    first-name: Robin
                    stats: { nerve: {{initial}} }
                """,
            ["main.tale"] = "=== start\n* [Next] -> next\n=== next\nNext.\n* [Finish] -> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static StoryDefinition CompileLoop(bool changed)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: continuation-loop, title: Loop, start: start }
                characters: { player: { first-name: Robin } }
                vars: { count: 0 }
                """,
            ["main.tale"] = changed
                ? "=== start\n~ var:count += 1\n* [Again] -> start"
                : "=== start\n* [Again] -> start",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static StoryDefinition CompileAutomatic(bool changed)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: continuation-automatic, title: Automatic, start: start }
                characters: { player: { first-name: Robin } }
                vars: { score: 0 }
                """,
            ["main.tale"] = changed
                ? "=== start\n* [Go] { var:score += 2 } -> next\n=== next\n~ var:score += 3\n-> finish\n=== finish\n* [Stay] -> finish"
                : "=== start\n* [Go] { var:score += 2 } -> next\n=== next\n* [Stay] -> next\n=== finish\n* [Stay] -> finish",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
