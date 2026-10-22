using System.Text;
using System.Text.Json.Nodes;
using Talewright.Core.Identity;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public class StoryHistoryTests
{
    private const string Yaml = """
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
            ["story.yaml"] = yaml, ["main.tale"] = tale,
        }), options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
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
        var history = new StoryHistory(Compile(Yaml + "\nundo: false"), 7);
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
}
