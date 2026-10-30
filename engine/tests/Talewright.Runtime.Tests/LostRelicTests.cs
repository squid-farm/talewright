using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;

namespace Talewright.Runtime.Tests;

public partial class LostRelicTests
{
    private static StorySource Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Talewright.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return StorySource.FromDirectory(Path.Combine(directory.FullName, "samples", "LostRelic"));
    }

    private static StoryDefinition Story(bool fixedRoll = false, int guildStanding = 5)
    {
        var source = Source();
        if (fixedRoll || guildStanding != 5)
        {
            var files = source.Files.ToDictionary();
            if (fixedRoll)
                files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName].Replace("randomness: checks-only", "randomness: off");
            files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
                .Replace("standing: 5 }", $"standing: {guildStanding} }}");
            source = new StorySource(files, source.ImagePaths);
        }
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        return result.Story!;
    }

    private static StoryHistory History(string preset = "explorer", bool fixedRoll = false, ulong seed = 42)
    {
        var story = Story(fixedRoll);
        return new StoryHistory(story, seed, story.Creation!.Default(story.World.Characters[EntityId.Player], preset));
    }

    private static void Choose(StoryHistory history, string label) =>
        history.Choose(Assert.Single(history.Current.Choices, c => c.Text == label).Key);

    private static void BuySupplies(StoryHistory history)
    {
        Choose(history, "Haggle for supplies");
        Choose(history, "Sign the expedition register");
        Choose(history, "Continue");
    }

    [Fact]
    public void Shipped_sample_compiles_without_errors_or_warnings() => Story();

    [Fact]
    public void Four_chapters_use_matching_scene_prefixes_and_root_configuration()
    {
        var source = Source();
        Assert.Equal(new[]
        {
            "chapters/01-camp.tale", "chapters/02-trail.tale",
            "chapters/03-temple.tale", "chapters/04-endings.tale",
            "dream/dream.tale",
        }, source.Files.Keys.Where(path => path.EndsWith(".tale")).Order());
        Assert.Equal(new[] { "dream/dream.talewright.yaml", StorySource.ManifestFileName },
            source.Files.Keys.Where(path => path.EndsWith(".yaml")).Order());
        foreach (var (file, prefix) in new[]
        {
            ("01-camp.tale", "camp."), ("02-trail.tale", "trail."),
            ("03-temple.tale", "temple."), ("04-endings.tale", "ending."),
        })
        {
            foreach (var line in source.Files["chapters/" + file].Split('\n').Where(line => line.StartsWith("=== ")))
                Assert.StartsWith(prefix, line[4..].Trim());
        }
        Assert.Equal("0.4.0", Story().Version);
        Assert.Contains(Story().Scenes, scene => scene.Id.Value == "dream.hub");
    }

    [Fact]
    public void Opening_agreement_is_correct_for_every_offered_pronoun_and_survives_restore()
    {
        var story = Story();
        Assert.Equal(["she", "they", "he"], story.Creation!.Pronouns);
        foreach (var pronouns in story.Creation.Pronouns)
        {
            var selection = story.Creation.Default(story.World.Characters[EntityId.Player]) with { Pronouns = pronouns };
            var history = new StoryHistory(story, 42, selection);
            var expected = $"{pronouns} {(pronouns == "they" ? "have" : "has")} to admit";
            Assert.Contains(expected, history.Current.Document.ToPlainText());
            Assert.DoesNotContain("they has", history.Current.Document.ToPlainText());
            var restored = StoryHistory.Restore(story, history.Save());
            Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
            Choose(history, "Continue");
            history.Back();
            Assert.Contains(expected, history.Current.Document.ToPlainText());
            history.Forward();
            Choose(history, "Sign the expedition register");
            var forms = pronouns switch
            {
                "she" => new[] { "She", "her", "hers", "herself" },
                "he" => new[] { "He", "his", "his", "himself" },
                _ => new[] { "They", "their", "theirs", "themselves" },
            };
            var text = history.Current.Document.ToPlainText();
            Assert.Contains($"{forms[0]} signed {forms[1]} own name; this copy is {forms[2]}.", text);
            Assert.Contains($"check it {forms[3]}", text);
            Assert.Contains("MARA BURNS", text);
            Assert.Contains("An obsidian idol", text);
            Assert.Contains("this obsidian idol belongs to the guild", text);
            Assert.Contains("{expedition copy}", text);
            Assert.Equal(text, StoryHistory.Restore(story, history.Save()).Current.Document.ToPlainText());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Watchword_demonstrates_text_declarations_copying_empty_values_and_replay(bool alternate, bool retire)
    {
        var history = History();
        Assert.Equal("quiet river", history.State.GetTextVariable(new("watchword")));
        Assert.Equal("", history.State.GetTextVariable(new("watch-note")));
        Choose(history, "Continue");
        Choose(history, "Agree on a night watchword");
        Assert.Contains("no phrase recorded", history.Current.Document.ToPlainText());
        Choose(history, alternate ? "Use silver heron instead" : "Keep quiet river");
        var recorded = alternate ? "silver heron" : "quiet river";
        Assert.Equal(recorded, history.State.GetTextVariable(new("watch-note")));
        Choose(history, retire ? "Retire the watchword without a replacement" : "Change the watchword to lantern light");
        var current = retire ? "" : "lantern light";
        Assert.Equal(current, history.State.GetTextVariable(new("watchword")));
        Assert.Equal(recorded, history.State.GetTextVariable(new("watch-note")));
        Assert.Contains(retire ? "No watchword tonight" : "current watchword", history.Current.Document.ToPlainText());
        Assert.Contains($"ledger still reads {recorded}", history.Current.Document.ToPlainText());
        history.Back();
        Assert.Equal(recorded, history.State.GetTextVariable(new("watchword")));
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.True(restored.CanForward);
        history.Forward();
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(current, restored.State.GetTextVariable(new("watchword")));
        Assert.Equal(recorded, restored.State.GetTextVariable(new("watch-note")));
        Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        Choose(history, "Close the watch ledger");
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Agree on a night watchword");
    }

    [Fact]
    public void Trader_and_register_give_camp_features_narrative_work()
    {
        var history = History();
        Assert.Equal(3, history.State.GetBaseStat(EntityId.Player, new StatId("courage")));
        Choose(history, "Continue");
        Assert.Equal(["Set out for the temple", "Haggle for supplies", "Sign the expedition register",
            "Study your journal", "Ask your guide about the temple", "Agree on a night watchword"],
            history.Current.Choices.Select(c => c.Text));
        Choose(history, "Haggle for supplies");
        Assert.Equal(2, history.State.GetItemCount(EntityId.Player, new ItemId("ration")));
        Assert.True(history.State.GetFlag(new FlagId("packing-open")));
        Assert.Equal(2, history.State.GetVariable(new("supplies")));
        Assert.Equal(["Return to camp", "Sign the expedition register"], history.Current.Choices.Select(choice => choice.Text));
        Choose(history, "Sign the expedition register");
        Choose(history, "Continue");
        Assert.Equal("camp.hub", history.Current.Scene!.Value.Value);
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text is "Haggle for supplies" or "Sign the expedition register");

        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        restored.Back();
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
    }

    [Fact]
    public void Trader_empty_intersection_returns_to_camp_when_register_is_already_signed()
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Sign the expedition register");
        Choose(history, "Continue");
        Choose(history, "Haggle for supplies");
        Assert.Equal("Return to camp", Assert.Single(history.Current.Choices).Text);
        Assert.Equal(2, history.State.GetItemCount(EntityId.Player, new("ration")));
        Choose(history, "Return to camp");
        Assert.Equal("camp.hub", history.Current.Scene!.Value.Value);
    }

    [Fact]
    public void Night_camp_caps_offers_counts_rations_clamps_courage_and_replays_bell_edges()
    {
        var history = History();
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Make camp before the climb");
        Assert.Equal(["Press on at dawn", "Eat a ration"], history.Current.Choices.Select(c => c.Text));
        Choose(history, "Eat a ration");
        Choose(history, "Share supper by the fire");
        Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new("ration")));
        Assert.False(history.State.GetFlag(new("packing-open")));
        Assert.Contains("one ration left", history.Current.Document.ToPlainText());
        Choose(history, "Continue");
        Assert.Equal(["Press on at dawn", "Stand your ground"], history.Current.Choices.Select(c => c.Text));
        var xp = history.State.GetBaseStat(EntityId.Player, new("xp"));
        Choose(history, "Stand your ground");
        Assert.Equal(10, history.State.GetStat(EntityId.Player, new StatId("courage")));
        Assert.Equal(xp + 5, history.State.GetBaseStat(EntityId.Player, new StatId("xp")));
        Assert.True(history.State.HasFiredTrigger("courage-earned"));
        Assert.True(history.State.GetFlag(new FlagId("steady")));
        var saved = StoryHistory.Restore(history.Story, history.Save());
        saved.Back();
        saved.Forward();
        Assert.Equal(history.Events, saved.Events);
        Choose(history, "Catch your breath");
        Assert.Equal(["Press on at dawn", "Follow the signal"], history.Current.Choices.Select(c => c.Text));
        Choose(history, "Follow the signal");
        Choose(history, "Continue");
        Assert.Equal("trail.wait", history.Current.Scene!.Value.Value);
        Choose(history, "Continue");
        Choose(history, "Check the tripwire bell");
        Choose(history, "Ring the bell");
        Assert.Equal(1, history.State.GetVariable(new VarId("bell-count")));
        Choose(history, "Silence the bell");
        Choose(history, "Ring the bell");
        Assert.Equal(2, history.State.GetVariable(new VarId("bell-count")));
        Choose(history, "Return to the fire");
        Assert.Equal("trail.night", history.Current.Scene!.Value.Value);
        Assert.Equal("Press on at dawn", Assert.Single(history.Current.Choices).Text);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        restored.Back();
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
        Choose(history, "Press on at dawn");
        Assert.Equal("temple.door", history.Current.Scene!.Value.Value);
    }

    [Theory]
    [InlineData(true, "trail.found")]
    [InlineData(false, "trail.wait")]
    public void Signal_uses_the_current_and_recorded_watchwords_with_a_fallback(bool keep, string destination)
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Agree on a night watchword");
        Choose(history, "Keep quiet river");
        Choose(history, keep ? "Keep the recorded watchword" : "Change the watchword to lantern light");
        Choose(history, "Close the watch ledger");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Make camp before the climb");
        Choose(history, "Eat a ration");
        Choose(history, "Share supper by the fire");
        Choose(history, "Continue");
        Choose(history, "Stand your ground");
        Choose(history, "Catch your breath");
        Choose(history, "Follow the signal");
        Choose(history, "Continue");
        Assert.Equal(destination, history.Current.Scene!.Value.Value);
        Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
    }

    [Fact]
    public void Fixed_dawn_choice_can_skip_every_night_encounter()
    {
        var history = History();
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Make camp before the climb");
        Choose(history, "Press on at dawn");
        Assert.Equal("temple.door", history.Current.Scene!.Value.Value);
        Assert.Equal(2, history.State.GetItemCount(EntityId.Player, new("ration")));
        Assert.Equal(3, history.State.GetStat(EntityId.Player, new("courage")));
        Assert.False(history.State.HasFiredTrigger("courage-earned"));
    }

    [Theory]
    [InlineData("explorer", 0)]
    [InlineData("scholar", 3)]
    public void Solving_journal_adds_two_lucidity_once_and_opens_glyph_route(string preset, int initial)
    {
        var history = History(preset, fixedRoll: true);
        Assert.Equal(initial, history.State.GetBaseStat(EntityId.Player, new("lucidity")));
        Assert.Equal(new StatId("will"), history.Story.World.Ruleset.StatsById[new("lucidity")].GoverningAttribute);
        Choose(history, "Continue");
        Choose(history, "Study your journal");
        Choose(history, "Puzzle out the glyphs");
        Assert.Equal("camp.journal-solved", history.Current.Scene!.Value.Value);
        Assert.Equal(initial + 2, history.State.GetBaseStat(EntityId.Player, new("lucidity")));
        Choose(history, "Continue");
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Study your journal");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Choose(history, "Trace the glyphs and press the hand");
        Assert.Equal("temple.hall", history.Current.Scene!.Value.Value);
        Assert.False(history.State.GetFlag(new("door-damaged")));
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(initial + 2, restored.State.GetBaseStat(EntityId.Player, new("lucidity")));
        Assert.Equal(history.Events, restored.Events);
    }

    [Theory]
    [InlineData("Swim for it", "trail.far-bank", 0)]
    [InlineData("Swim for it", "trail.swept", 3)]
    [InlineData("Let Tomas find a ford", "trail.far-bank", 0)]
    [InlineData("Let Tomas find a ford", "trail.swept", 1)]
    public void Player_and_guide_river_checks_preserve_success_and_failure_routes(string choice, string scene, int damage)
    {
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var history = History(seed: seed);
            var hp = history.State.GetStat(EntityId.Player, new("hp"));
            Choose(history, "Continue");
            Choose(history, "Set out for the temple");
            Assert.DoesNotContain(history.Current.Choices, option => option.Text.StartsWith("Tie off"));
            Choose(history, choice);
            if (history.Current.Scene!.Value.Value != scene) continue;
            Assert.Equal(hp - damage, history.State.GetStat(EntityId.Player, new("hp")));
            Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
            return;
        }
        Assert.Fail($"No seed exercised {choice} -> {scene}.");
    }

    [Theory]
    [InlineData("explorer", true)]
    [InlineData("scholar", false)]
    public void Forcing_door_preserves_both_routes(string preset, bool succeeds)
    {
        var story = Story(fixedRoll: true);
        var selection = story.Creation!.Default(story.World.Characters[EntityId.Player], preset);
        if (succeeds)
        {
            var stats = selection.Stats.ToDictionary();
            stats["str"] = 16;
            selection = selection with { Stats = stats };
        }
        var history = new StoryHistory(story, 42, selection);
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text == "Trace the glyphs and press the hand");
        Choose(history, "Force the door");
        Assert.Equal(succeeds ? "temple.hall" : "temple.side", history.Current.Scene!.Value.Value);
        Assert.Equal(succeeds, history.State.GetFlag(new("door-damaged")));
    }

    [Fact]
    public void Earlier_sample_version_save_is_rejected_without_changing_current_history()
    {
        var source = Source();
        var files = source.Files.ToDictionary();
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName].Replace("version: 0.4.0", "version: 0.3.0");
        var older = StoryCompiler.Compile(new StorySource(files, source.ImagePaths)).Story!;
        var bytes = new StoryHistory(older, 42, older.Creation!.Default(older.World.Characters[EntityId.Player])).Save();
        var history = History();
        Choose(history, "Continue");
        var events = history.Events.ToArray();
        Assert.Contains("incompatible", Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, bytes)).Message);
        Assert.Equal(events, history.Events);
    }

    [Theory]
    [InlineData("explorer", "guild", "ending.guild", "she")]
    [InlineData("explorer", "guild", "ending.guild", "they")]
    [InlineData("explorer", "guild", "ending.guild", "he")]
    [InlineData("scholar", "keep", "ending.keep", "she")]
    [InlineData("scholar", "keep", "ending.keep", "they")]
    [InlineData("scholar", "keep", "ending.keep", "he")]
    [InlineData("explorer", "leave", "ending.empty-handed", "she")]
    [InlineData("explorer", "leave", "ending.empty-handed", "they")]
    [InlineData("explorer", "leave", "ending.empty-handed", "he")]
    public void Created_characters_can_finish_each_ending_and_restore(string preset, string outcome, string ending, string pronouns)
    {
        var story = Story();
        var selection = story.Creation!.Default(story.World.Characters[EntityId.Player], preset) with { Pronouns = pronouns };
        var history = new StoryHistory(story, 42, selection);
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Choose(history, "Look for another way in");
        Choose(history, "Squeeze through");
        Choose(history, "Continue");
        if (history.Current.Scene == new SceneId("temple.niche"))
        {
            Choose(history, "Take the idol");
        }
        Choose(history, outcome == "leave" ? "Leave it where it lies" : "Take the relic");
        Choose(history, outcome switch
        {
            "guild" => "Hand the relic to the guild",
            "keep" => "Keep it for yourself",
            _ => "Continue",
        });
        Assert.True(history.Current.IsEnded);
        Assert.Equal(ending, history.Current.Scene!.Value.Value);
        Assert.Equal(outcome == "keep", history.State.GetItemCount(EntityId.Player, new ItemId("relic")) == 1);
        if (outcome == "keep")
        {
            Assert.True(history.State.GetFlag(new FlagId("guild-wary")));
        }
        Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
    }
}
