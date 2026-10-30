using System.Text;
using System.Text.Json.Nodes;
using Talewright.Core.Identity;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public class StoryHistoryTests
{
    private const string Yaml = """
        game:
          id: history
          title: History
          version: 1
          start: a
        characters:
          player: { first-name: Tester, stats: { athletics: 2 } }
        triggers:
          - id: first
            when: var:visits >= 1
            do: var:trigger-count += 1
            once: true
        """;

    private const string Tale = """
        === a
        ~ var:visits += 1
        Visits: {var:visits}; triggers: {var:trigger-count}.
        * [Try] skill-check athletics dc 12
          success: { player:hp -= 1 } -> a
          failure: { player:hp -= 2 } -> a
        * [Finish] -> END
        """;

    private static StoryDefinition Compile(string yaml = Yaml, string tale = Tale, CompilerOptions? options = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml, ["main.tale"] = tale,
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
        Assert.DoesNotContain("summary", Encoding.UTF8.GetString(history.Save()));
    }

    [Theory]
    [InlineData("\"damaged\"")]
    [InlineData("{\"sceneTitle\":\"a\",\"savedAt\":\"not-a-date\"}")]
    [InlineData("{\"savedAt\":\"2026-10-23T00:00:00Z\"}")]
    public void Unreadable_summary_does_not_prevent_loading_replay(string metadata)
    {
        var history = new StoryHistory(Compile(), 7);
        var json = JsonNode.Parse(history.Save())!;
        json["summary"] = JsonNode.Parse(metadata);
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
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
        var json = JsonNode.Parse(history.Save(history.CaptureSummary(DateTimeOffset.UtcNow)))!;
        json["choices"] = new JsonArray("no-such-choice");
        json["cursor"] = 1;
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, bytes));
        Assert.NotNull(StoryHistory.DescribeSave("Invalid replay", bytes).Summary);
        var damaged = StoryHistory.DescribeSave("Broken", "{broken"u8);
        Assert.Equal("Broken", damaged.Slot);
        Assert.Null(damaged.ProgressKey);
        Assert.NotNull(damaged.Warning);
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
        var save = JsonNode.Parse(history.Save())!.AsObject();
        save["choices"]![1] = "missing";

        var error = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(history.Story, Encoding.UTF8.GetBytes(save.ToJsonString())));

        Assert.Contains("cannot continue at choice 2", error.Message);
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
    [InlineData("formatVersion", "1", "unsupported")]
    [InlineData("formatVersion", "99", "unsupported")]
    [InlineData("storyId", "\"other\"", "different story")]
    [InlineData("storyVersion", "\"other\"", "incompatible")]
    [InlineData("contentFingerprint", "\"changed\"", "incompatible")]
    [InlineData("cursor", "-1", "history is damaged")]
    [InlineData("cursor", "3", "history is damaged")]
    [InlineData("choices", "null", "history is damaged")]
    [InlineData("choices", "[null]", "history is damaged")]
    [InlineData("choices", "[\"missing\"]", "cannot continue")]
    public void Invalid_saves_preserve_active_session(string field, string value, string message)
    {
        var history = new StoryHistory(Compile(), 7);
        history.Choose("c0");
        var before = history.Save();
        var save = JsonNode.Parse(before)!.AsObject();
        save[field] = JsonNode.Parse(value);
        var invalid = Encoding.UTF8.GetBytes(save.ToJsonString());

        var error = Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, invalid));

        Assert.Contains(message, error.Message);
        Assert.Equal(before, history.Save());
    }

    [Fact]
    public void Invalid_redo_is_checked_even_when_cursor_is_at_the_start()
    {
        var history = new StoryHistory(Compile(), 7);
        var save = JsonNode.Parse(history.Save())!.AsObject();
        save["choices"] = new JsonArray("missing");
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, Encoding.UTF8.GetBytes(save.ToJsonString())));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    public void Malformed_saves_are_explicitly_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(), Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Oversized_save_is_rejected_before_deserialization()
    {
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(), new byte[StoryHistory.MaxSaveBytes + 1]));
    }

    [Fact]
    public void Legal_long_choice_keys_reach_the_exact_save_byte_limit()
    {
        var story = Compile(Yaml, "=== a\n~ var:visits += 1\n* [Again] -> a");
        var emptySaveLength = new StoryHistory(story, 7).Save().Length;
        var choices = Enumerable.Range(1, StoryHistory.MaxChoices)
            .Select(count =>
            {
                var keyBytes = StoryHistory.MaxSaveBytes - emptySaveLength -
                    (3 * count - 1) - (count.ToString().Length - 1);
                return keyBytes > 0 && keyBytes % count == 0
                    ? (Count: count, KeyLength: keyBytes / count)
                    : (Count: 0, KeyLength: 0);
            })
            .First(candidate => candidate.Count != 0 && candidate.KeyLength is >= 1 and <= 512);

        var key = new string('k', choices.KeyLength);
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
        for (var index = 0; index < choices.Count; index++)
        {
            history.Choose(key);
        }

        var exactLimitSave = history.Save();
        Assert.Equal(StoryHistory.MaxSaveBytes, exactLimitSave.Length);
        Assert.Equal(choices.Count, StoryHistory.Restore(longKeyStory, exactLimitSave).Cursor);

        history.Choose(key);
        Assert.Throws<InvalidOperationException>(() => history.Save());
        Assert.Equal(choices.Count + 1, history.Cursor);
        Assert.Equal(StoryHistory.MaxSaveBytes, exactLimitSave.Length);
        Assert.False(history.TryProgressKey(out var progress));
        Assert.Null(progress);
    }

    [Fact]
    public void Choice_limit_accepts_exact_boundary_and_rejects_the_next_choice()
    {
        var history = new StoryHistory(Compile(Yaml + "\nvars: { visits: 0 }", "=== a\n* [Again] -> a"), 7);
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
    public void Oversized_choice_history_and_unknown_fields_are_rejected()
    {
        var history = new StoryHistory(Compile(), 7);
        var save = JsonNode.Parse(history.Save())!.AsObject();
        save["extra"] = true;
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, Encoding.UTF8.GetBytes(save.ToJsonString())));
        save.Remove("extra");
        save["choices"] = new JsonArray(Enumerable.Range(0, StoryHistory.MaxChoices + 1).Select(_ => (JsonNode?)JsonValue.Create("c0")).ToArray());
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, Encoding.UTF8.GetBytes(save.ToJsonString())));
    }

    [Fact]
    public void Changed_source_with_unchanged_story_version_is_incompatible()
    {
        var history = new StoryHistory(Compile(), 7);
        Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(Compile(tale: Tale + "\n// change"), history.Save()));
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
        var history = new StoryHistory(Compile(Yaml.Replace("  start: a", "  start: a\n  undo: false")), 7);
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
