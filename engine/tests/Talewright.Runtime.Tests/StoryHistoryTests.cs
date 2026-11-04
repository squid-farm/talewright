using System.Text;
using Talewright.Core.Identity;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class StoryHistoryTests
{
    private static readonly string Yaml = BuildManifest().Build();
    private static readonly string Tale = BuildTale();

    private static ManifestFaker BuildManifest(bool declareVariables = false, bool? undo = null)
    {
        var manifest = new ManifestFaker("history", "History", "a")
        {
            Version = "1",
            Undo = undo,
        };
        manifest.Character("player", "Tester", stats: new Dictionary<string, int> { ["athletics"] = 2 });
        if (declareVariables)
        {
            manifest.Number("visits", 0);
            manifest.Number("trigger-count", 0);
        }

        manifest.Trigger("first", "var:visits >= 1", EffectFaker.Increase(new TaleVariableRef("trigger-count"), 1), once: true);
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
            save => save with { Choices = ["no-such-choice"], Cursor = 1 });
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
    public void Invalid_last_choice_is_rejected_when_cursor_is_at_the_end()
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        history.Choose("c0");
        var save = Mutate(history.Save(), save => save with { Choices = [save.Choices[0], "missing"] });

        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, save));

        Assert.Contains("cannot continue", error.Message);
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
            "story-version" => Mutate(before, save => save with { StoryVersion = "other" }),
            "fingerprint" => Mutate(before, save => save with { ContentFingerprint = "changed" }),
            "cursor" => Mutate(before, save => save with { Cursor = 3 }),
            "choice" => Mutate(before, save => save with { Choices = ["missing"] }),
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
        var save = Mutate(history.Save(), save => save with { Choices = ["missing"] });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, save));
    }

    [Fact]
    public void Invalid_replay_reports_no_scene_or_choice_position()
    {
        var history = new StoryHistory(Compile(), 7);
        var save = Mutate(history.Save(), save => save with { Choices = ["missing"] });
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

    [Fact]
    public void Choice_limit_accepts_exact_boundary_and_rejects_the_next_choice()
    {
        var history = new StoryHistory(Compile(BuildManifest(declareVariables: true).Build(),
            string.Join('\n', SceneFaker.Header("a"), ChoiceFaker.Create("a", "Again"))), 7);
        for (var i = 0; i < StoryHistory.MaxChoices; i++)
        {
            history.Choose("c0");
        }

        Assert.Equal(StoryHistory.MaxChoices, history.Cursor);
        Assert.Equal(StoryHistory.MaxChoices + 1, history.Transcript.Count);
        var saved = history.Save();
        Assert.Throws<InvalidOperationException>(() => history.Choose("c0"));
        Assert.Equal(saved, history.Save());
        var restored = StoryHistory.Restore(history.Story, saved);
        Assert.Equal(StoryHistory.MaxChoices, restored.Cursor);
        Assert.Equal(StoryHistory.MaxChoices + 1, restored.Transcript.Count);
    }

    [Fact]
    public void Oversized_choice_history_is_rejected()
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        var oversized = Mutate(history.Save(), save => save with
        {
            Choices = Enumerable.Repeat("c0", StoryHistory.MaxChoices + 1).ToArray(),
        });
        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, oversized));
        Assert.Equal(SaveFormat.HistoryMessage, error.Message);
    }

    [Fact]
    public void Changed_source_requires_evidence_even_when_story_version_is_unchanged()
    {
        var history = new StoryHistory(Compile(), 7);
        var updated = Compile(tale: Tale + "\n// change");
        var save = history.Save();
        var legacy = Mutate(save, data => data with { ReplayEvidence = null });
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
        var save = new StoryHistory(version1, 7).Save();
        var version2 = Compile(options: new CompilerOptions { CompatibilityId = "custom-v2" });
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(version2, save));
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
