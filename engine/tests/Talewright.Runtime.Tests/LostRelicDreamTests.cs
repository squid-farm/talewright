using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

public partial class LostRelicTests
{
    private static StoryHistory Dream(string preset = "scholar", bool journal = false,
        string watchword = "unrecorded", string pronouns = "they", bool fixedRoll = true, ulong seed = 42,
        int guildStanding = 5)
    {
        var story = Story(fixedRoll, guildStanding);
        var selection = story.Creation!.Default(story.World.Characters[EntityId.Player], preset);
        var stats = selection.Stats.ToDictionary();
        stats["courage"] = 6;
        var history = new StoryHistory(story, seed, selection with { Stats = stats, Pronouns = pronouns });
        Choose(history, "Continue");
        Choose(history, "Ask your guide about the temple");
        Choose(history, "Continue");
        if (journal)
        {
            Choose(history, "Study your journal");
            Choose(history, "Puzzle out the glyphs");
            Choose(history, "Continue");
        }
        if (watchword != "unrecorded")
        {
            Choose(history, "Agree on a night watchword");
            Choose(history, "Keep quiet river");
            Choose(history, watchword switch
            {
                "current" => "Keep the recorded watchword",
                "changed" => "Change the watchword to lantern light",
                _ => "Retire the watchword without a replacement",
            });
            Choose(history, "Close the watch ledger");
        }
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Choose(history, "Close your eyes");
        Choose(history, "Continue");
        return history;
    }

    private static void AssertMarks(StoryHistory history, int expected)
    {
        Assert.Equal(expected, history.State.GetVariable(new("marks")));
        Assert.Equal(expected, history.State.GetItemCount(EntityId.Player, new("glyph-mark")));
    }

    private static void MeetSpiral(StoryHistory history)
    {
        Choose(history, "Answer the Spiral");
        Assert.Contains("Xe has a question", history.Current.Document.ToPlainText());
        Choose(history, "Accept the spiral mark");
        AssertMarks(history, 1);
        Assert.Equal(0, history.State.GetItemCount(new("spiral"), new("glyph-mark")));
    }

    private static void MeetWatcher(StoryHistory history, string route = "Remember that you are dreaming")
    {
        Choose(history, "Face the Watcher");
        Assert.Contains("It has no eyelid", history.Current.Document.ToPlainText());
        Choose(history, "Continue");
        Choose(history, "Continue");
        Choose(history, route);
        Assert.Equal("dream.eye-mark", history.Current.Scene!.Value.Value);
        AssertMarks(history, 2);
        Choose(history, "Continue");
    }

    private static void MeetHandsAndSleeper(StoryHistory history)
    {
        if (!history.Current.Choices.Any(choice => choice.Text == "Ask the Hands for passage"))
        {
            Choose(history, "Speak to the lost explorer");
            Choose(history, "Promise to carry her message");
        }
        Choose(history, "Ask the Hands for passage");
        var text = history.Current.Document.ToPlainText();
        Assert.Contains("The Hands have no owner", text);
        Assert.Contains("They offer the final mark", text);
        Choose(history, "Accept the hand mark and the price");
        AssertMarks(history, 3);
        Assert.True(history.State.GetFlag(new("all-marks")));
        Assert.True(history.State.HasFiredTrigger("dream-ready"));
        Assert.Equal(0, history.State.GetItemCount(new("hands"), new("glyph-mark")));
        Choose(history, "Approach the Sleeper");
    }

    [Theory]
    [InlineData("current", "living voice and the written word agree")]
    [InlineData("changed", "You changed the word, not the ink")]
    [InlineData("retired", "Silence is an honest answer")]
    [InlineData("unrecorded", "A suggestion, never recorded")]
    public void Spiral_responds_to_current_copied_changed_and_retired_watchwords(string watchword, string response)
    {
        var history = Dream(watchword: watchword);
        Choose(history, "Answer the Spiral");
        Assert.Contains(response, history.Current.Document.ToPlainText());
        Choose(history, "Accept the spiral mark");
        AssertMarks(history, 1);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        restored.Back();
        Assert.Contains(response, restored.Current.Document.ToPlainText());
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
    }

    [Fact]
    public void Dream_intersection_caps_offers_and_all_six_roles_preserve_counts_and_replay()
    {
        var history = Dream(journal: true);
        Assert.Equal(["Wake before going further", "Speak to the lost explorer", "Answer the Spiral"],
            history.Current.Choices.Select(choice => choice.Text));
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Approach the Sleeper");
        Choose(history, "Speak to the lost explorer");
        Assert.Equal(10, history.State.GetVariable(new("sum")));
        Assert.Contains("Tomas's story", history.Current.Document.ToPlainText());
        Assert.Contains("Tell Tomas I remember the river", history.Current.Document.ToPlainText());
        Choose(history, "Promise to carry her message");
        MeetSpiral(history);
        Assert.Equal(["Wake before going further", "Face the Watcher", "Hear the guild ghost"],
            history.Current.Choices.Select(choice => choice.Text));
        MeetWatcher(history);
        var check = Assert.Single(history.Events.OfType<SkillCheckResolved>(),
            check => check.Skill == new StatId("lucidity"));
        Assert.Equal(7, check.Modifier); // Scholar Lucidity 3 + journal 2 + Will 14 modifier 2.
        Assert.Equal(17, check.Total);
        var standing = history.State.GetStanding(new("explorers-guild"));
        Choose(history, "Hear the guild ghost");
        Assert.Contains("They still put your name in the ledger", history.Current.Document.ToPlainText());
        Choose(history, "Promise the guild an honest account");
        Assert.True(history.State.GetFlag(new("dream-promise")));
        Assert.Equal(standing + 5, history.State.GetStanding(new("explorers-guild")));
        MeetHandsAndSleeper(history);
        Assert.Contains("not a monster, but an opening", history.Current.Document.ToPlainText());
        var saved = StoryHistory.Restore(history.Story, history.Save());
        Choose(history, "Pay the Hands and return to the temple");
        AssertMarks(history, 2);
        Assert.Equal(1, history.State.GetItemCount(new("hands"), new("glyph-mark")));
        Assert.True(history.State.GetFlag(new("dream-opened")));
        Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
        saved.Back();
        saved.Forward();
        AssertMarks(saved, 3);
        Choose(saved, "Pay the Hands and return to the temple");
        Assert.Equal(history.Events, saved.Events);
        Choose(history, "Continue");
        Assert.Equal("temple.hall", history.Current.Scene!.Value.Value);
    }

    [Theory]
    [InlineData("explorer", false, "Only its hunger remains", "sleeper-low.jpeg")]
    [InlineData("explorer", true, "a latch, and a door that must stay shut", "sleeper-middle.jpeg")]
    [InlineData("scholar", false, "a latch, and a door that must stay shut", "sleeper-middle.jpeg")]
    [InlineData("scholar", true, "Never turn it", "sleeper-high.jpeg")]
    public void Sleeper_prose_uses_low_middle_and_high_lucidity(string preset, bool journal, string expected, string image)
    {
        var history = Dream(preset, journal);
        MeetSpiral(history);
        MeetWatcher(history, "Meet its gaze without flinching");
        MeetHandsAndSleeper(history);
        Assert.Contains(expected, history.Current.Document.ToPlainText());
        var media = Assert.Single(history.Current.Document.Blocks.OfType<RenderedParagraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<RenderedMedia>());
        Assert.EndsWith(image, media.MediaId);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(media, Assert.Single(restored.Current.Document.Blocks.OfType<RenderedParagraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<RenderedMedia>()));
        AssertMarks(history, 3);
    }

    [Fact]
    public void Failed_lucidity_and_stealth_checks_keep_courage_and_waking_alternatives()
    {
        var history = Dream("explorer");
        MeetSpiral(history);
        Choose(history, "Face the Watcher");
        Choose(history, "Continue");
        Choose(history, "Continue");
        foreach (var choice in new[] { "Remember that you are dreaming", "Slip beneath its gaze" })
        {
            Choose(history, choice);
            Assert.Equal("dream.watcher-failed", history.Current.Scene!.Value.Value);
            Choose(history, "Continue");
        }
        Assert.Contains(history.Current.Choices, choice => choice.Text == "Wake rather than face it");
        Choose(history, "Meet its gaze without flinching");
        AssertMarks(history, 2);
    }

    [Theory]
    [InlineData(5, "They still put your name in the ledger", true)]
    [InlineData(-5, "The guild sent us here", true)]
    [InlineData(-20, "fall out of their favour", false)]
    public void Archivist_standing_tiers_gate_promises_and_refusal_changes_standing(
        int initialStanding, string prose, bool mayPromise)
    {
        var history = Dream(guildStanding: initialStanding);
        Choose(history, "Speak to the lost explorer");
        Choose(history, "Promise to carry her message");
        MeetSpiral(history);
        Choose(history, "Hear the guild ghost");
        Assert.Contains(prose, history.Current.Document.ToPlainText());
        var promise = history.Current.Choices.Where(choice => choice.Text == "Promise the guild an honest account");
        Assert.Equal(mayPromise, promise.Any());
        var before = history.State.GetStanding(new("explorers-guild"));
        Choose(history, "Refuse to speak for the guild");
        Assert.Equal(before - 10, history.State.GetStanding(new("explorers-guild")));
        Assert.False(history.State.GetFlag(new("dream-promise")));
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Hear the guild ghost");
    }

    [Fact]
    public void Successful_stealth_is_an_alternative_to_lucidity_and_courage()
    {
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var history = Dream("explorer", fixedRoll: false, seed: seed);
            MeetSpiral(history);
            Choose(history, "Face the Watcher");
            Choose(history, "Continue");
            Choose(history, "Continue");
            Choose(history, "Slip beneath its gaze");
            var check = Assert.Single(history.Events.OfType<SkillCheckResolved>(),
                check => check.Skill == new StatId("stealth"));
            if (!check.Success) continue;
            Assert.Equal("dream.eye-mark", history.Current.Scene!.Value.Value);
            AssertMarks(history, 2);
            Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
            return;
        }
        Assert.Fail("No seed exercised a successful Stealth route.");
    }

    [Fact]
    public void Refusing_the_Hands_preserves_two_marks_and_other_temple_routes()
    {
        var history = Dream();
        MeetSpiral(history);
        MeetWatcher(history);
        Choose(history, "Speak to the lost explorer");
        Choose(history, "Promise to carry her message");
        Choose(history, "Ask the Hands for passage");
        Choose(history, "Refuse and wake");
        AssertMarks(history, 2);
        Assert.False(history.State.GetFlag(new("all-marks")));
        Assert.False(history.State.GetFlag(new("dream-opened")));
        Assert.Equal(1, history.State.GetItemCount(new("hands"), new("glyph-mark")));
        Choose(history, "Continue");
        Assert.Contains(history.Current.Choices, choice => choice.Text == "Look for another way in");
    }

    [Theory]
    [InlineData("explorer", false, "guild", "You keep the dream out of the report")]
    [InlineData("explorer", false, "keep", "The warning returns in fragments")]
    [InlineData("scholar", false, "keep", "The warning returns in fragments")]
    [InlineData("scholar", true, "keep", "you know not to open it")]
    [InlineData("scholar", true, "leave", "The glyph marks are proof enough")]
    public void Dream_endings_without_a_guild_promise_react_to_lucidity_and_visits(
        string preset, bool journal, string ending, string prose)
    {
        var history = Dream(preset, journal);
        MeetSpiral(history);
        MeetWatcher(history, "Meet its gaze without flinching");
        MeetHandsAndSleeper(history);
        Assert.False(history.State.GetFlag(new("dream-promise")));
        Choose(history, "Pay the Hands and return to the temple");
        Choose(history, "Continue");
        Choose(history, "Continue");
        if (history.Current.Scene == new SceneId("temple.niche")) Choose(history, "Leave it be");
        Choose(history, ending == "leave" ? "Leave it where it lies" : "Take the relic");
        Choose(history, ending switch
        {
            "guild" => "Hand the relic to the guild",
            "keep" => "Keep it for yourself",
            _ => "Continue",
        });
        Assert.True(history.Current.IsEnded);
        Assert.Contains(prose, history.Current.Document.ToPlainText());
        AssertMarks(history, 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Watcher_npc_roll_records_both_outcomes_and_replays(bool success)
    {
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var history = Dream(fixedRoll: false, seed: seed);
            MeetSpiral(history);
            Choose(history, "Face the Watcher");
            var check = Assert.Single(history.Events.OfType<SkillCheckResolved>(),
                check => check.Actor == new EntityId("watcher"));
            if (check.Success != success) continue;
            Assert.Equal(6, check.Modifier);
            Choose(history, "Continue");
            Assert.Equal(success ? "dream.watcher-sees" : "dream.watcher-missed",
                history.Current.Scene!.Value.Value);
            Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
            return;
        }
        Assert.Fail($"No seed exercised Watcher success={success}.");
    }

    [Fact]
    public void Abandoned_dream_does_not_open_door_or_remove_existing_routes()
    {
        var history = Dream();
        Choose(history, "Wake before going further");
        Choose(history, "Continue");
        Assert.Equal("temple.door", history.Current.Scene!.Value.Value);
        Assert.False(history.State.GetFlag(new("dream-opened")));
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Close your eyes");
        Assert.Contains(history.Current.Choices, choice => choice.Text == "Force the door");
        Choose(history, "Look for another way in");
        Choose(history, "Squeeze through");
        Assert.Equal("temple.hall", history.Current.Scene!.Value.Value);
    }

    [Theory]
    [InlineData("she", "guild")]
    [InlineData("they", "guild")]
    [InlineData("he", "guild")]
    [InlineData("she", "keep")]
    [InlineData("they", "keep")]
    [InlineData("he", "keep")]
    [InlineData("she", "leave")]
    [InlineData("they", "leave")]
    [InlineData("he", "leave")]
    public void Dream_route_preserves_all_endings_and_offered_pronouns(string pronouns, string ending)
    {
        var history = Dream(journal: true, pronouns: pronouns);
        MeetSpiral(history);
        MeetWatcher(history);
        Choose(history, "Hear the guild ghost");
        Choose(history, "Promise the guild an honest account");
        MeetHandsAndSleeper(history);
        Choose(history, "Pay the Hands and return to the temple");
        Choose(history, "Continue");
        Choose(history, "Continue");
        if (history.Current.Scene == new SceneId("temple.niche")) Choose(history, "Leave it be");
        Assert.Contains("voice beyond the door falls silent", history.Current.Document.ToPlainText());
        Choose(history, ending == "leave" ? "Leave it where it lies" : "Take the relic");
        Choose(history, ending switch
        {
            "guild" => "Hand the relic to the guild",
            "keep" => "Keep it for yourself",
            _ => "Continue",
        });
        Assert.True(history.Current.IsEnded);
        Assert.Contains(ending switch
        {
            "guild" => "testimony",
            "keep" => "you know not to open it",
            _ => "The guild ghost's testimony earns a hearing",
        }, history.Current.Document.ToPlainText());
        AssertMarks(history, 2);
        Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
    }
}
