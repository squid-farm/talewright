using Talewright.Core.Events;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Runtime.Saves;

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
        Assert.Equal("0.6.0", Story().Version);
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
            Assert.Contains($"{forms[0]} signed\n{forms[1]} own name; this copy is {forms[2]}.", text);
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
        Assert.Equal(["Haggle for supplies", "Sign the expedition register", "Study your journal",
            "Set out for the temple", "Ask Tomas about the temple", "Agree on a night watchword"],
            history.Current.Choices.Select(c => c.Text));
        Choose(history, "Haggle for supplies");
        Assert.Equal(2, history.State.GetItemCount(EntityId.Player, new ItemId("ration")));
        Assert.True(history.State.GetFlag(new FlagId("packing-open")));
        Assert.Equal(2, history.State.GetVariable(new("supplies")));
        Assert.Equal(["Sign the expedition register", "Return to camp"], history.Current.Choices.Select(choice => choice.Text));
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
        Assert.Equal(["Eat a ration", "Press on at dawn"], history.Current.Choices.Select(c => c.Text));
        Choose(history, "Eat a ration");
        Choose(history, "Share supper by the fire");
        Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new("ration")));
        Assert.False(history.State.GetFlag(new("packing-open")));
        Assert.Contains("one ration left", history.Current.Document.ToPlainText());
        Choose(history, "Continue");
        Assert.Equal(["Stand your ground", "Press on at dawn"], history.Current.Choices.Select(c => c.Text));
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
        Assert.Equal(["Follow the signal", "Press on at dawn"], history.Current.Choices.Select(c => c.Text));
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
    public void Earlier_sample_version_requires_evidence_and_never_changes_current_history()
    {
        var source = Source();
        var files = source.Files.ToDictionary();
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName].Replace("version: 0.6.0", "version: 0.3.0");
        var older = StoryCompiler.Compile(new StorySource(files, source.ImagePaths)).Story!;
        var oldHistory = new StoryHistory(older, 42, older.Creation!.Default(older.World.Characters[EntityId.Player]));
        var bytes = oldHistory.Save();
        var legacy = SaveFormat.Write(SaveFormat.Read(bytes) with
        {
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
        });
        var history = History();
        Choose(history, "Continue");
        var events = history.Events.ToArray();
        Assert.StartsWith("Version 0.3.0 of ", Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(history.Story, legacy)).Message);
        Assert.Equal(oldHistory.Events, StoryHistory.Restore(history.Story, bytes).Events);
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

    private static ChoiceMenuEntry MenuEntry(StoryHistory history, string label) =>
        Assert.Single(history.Current.Menu, entry => entry.Text == label);

    private static RenderedSpeech Speech(StoryHistory history) =>
        history.Current.Document.Blocks.OfType<RenderedSpeech>().First();

    [Fact]
    public void Manifest_starts_the_guide_doubtful_until_the_labelled_offer_changes_his_mind()
    {
        var history = History();
        Assert.True(history.State.GetFlag(new("guide-doubtful")));
        Assert.False(history.State.GetFlag(new("packing-open")));
        Assert.DoesNotContain(history.Events, @event => @event is FlagChanged);
        Choose(history, "Continue");
        Assert.Contains("hasn't said what he thinks of your chances", history.Current.Document.ToPlainText());
        var offer = Assert.Single(history.Current.Choices, choice => choice.Text == "Ask Tomas about the temple");
        Assert.Contains(new RenderedText("Tomas", TextStyle.Strong), offer.Label);

        Choose(history, "Ask Tomas about the temple");
        Assert.Equal("What the Guild Never Wrote Down", history.Current.Title);
        var speech = Speech(history);
        Assert.Equal(4, speech.Paragraphs.Count);
        Assert.Equal("The old ones built it to keep something in, not people out.\nThe guild knows that. They never write it down.",
            RenderedInline.ToPlainText(speech.Paragraphs[0]));
        Assert.Contains("Ines Varga", RenderedInline.ToPlainText(speech.Paragraphs[1]));
        Assert.Equal(new RenderedBlankLineInline(), Assert.Single(speech.Paragraphs[2]));
        Assert.StartsWith("I took this job", RenderedInline.ToPlainText(speech.Paragraphs[3]));
        Assert.False(history.State.GetFlag(new("guide-doubtful")));

        Choose(history, "Continue");
        Assert.Contains("nods, as if the two of you have already agreed", history.Current.Document.ToPlainText());
        Choose(history, "Set out for the temple");
        Choose(history, "Let Tomas find a ford");
        if (history.Current.Scene == new SceneId("trail.far-bank"))
            Assert.DoesNotContain("didn't think you'd get this far", history.Current.Document.ToPlainText());
        history.Back();
        history.Back();
        history.Back();
        Assert.False(history.State.GetFlag(new("guide-doubtful")));
        Assert.Equal(speech.ToPlainText(), Speech(history).ToPlainText());
        Assert.Equal(history.Current.Document.ToPlainText(), StoryHistory.Restore(history.Story, history.Save()).Current.Document.ToPlainText());
        history.Back();
        Assert.True(history.State.GetFlag(new("guide-doubtful")));
        Assert.True(StoryHistory.Restore(history.Story, history.Save()).State.GetFlag(new("guide-doubtful")));
    }

    [Fact]
    public void Journal_keeps_authored_indentation_and_extra_vertical_space()
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Study your journal");
        var blocks = history.Current.Document.Blocks;
        var layout = Assert.Single(blocks.OfType<RenderedParagraph>(), block => block.ToPlainText().Contains("the spiral"));
        Assert.Equal("    the spiral\n            the closed eye\n                    the hand", layout.ToPlainText());
        Assert.IsType<RenderedBlankLine>(blocks[blocks.ToList().IndexOf(layout) + 1]);
        Assert.Contains("in that order, and not before", blocks[blocks.ToList().IndexOf(layout) + 2].ToPlainText());
    }

    [Theory]
    [InlineData(false, "Keep the recorded watchword")]
    [InlineData(true, "Change the watchword to lantern light")]
    public void Watchword_replies_continue_one_passage_as_separate_history_steps(bool alternate, string reply)
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Agree on a night watchword");
        Assert.False(history.Current.ContinuesPrevious);
        Assert.Equal("Agree on a night watchword", history.Current.Title);
        Choose(history, alternate ? "Use silver heron instead" : "Keep quiet river");
        Assert.True(history.Current.ContinuesPrevious);
        Assert.Null(history.Current.Title);
        var recordCursor = history.Cursor;
        Choose(history, reply);
        Assert.Equal(recordCursor + 1, history.Cursor);
        Assert.Equal("camp.watchword-readback", history.Current.Scene!.Value.Value);
        Assert.True(history.Current.ContinuesPrevious);
        Assert.Null(history.Current.Title);

        history.Back();
        Assert.Equal("camp.watchword-record", history.Current.Scene!.Value.Value);
        Assert.True(history.Current.ContinuesPrevious);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.True(restored.Current.ContinuesPrevious);
        restored.Forward();
        history.Forward();
        Assert.True(restored.Current.ContinuesPrevious);
        Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        Choose(restored, "Close the watch ledger");
        Assert.False(restored.Current.ContinuesPrevious);
        Assert.Equal("The Camp", restored.Current.Title);
    }

    [Fact]
    public void Tripwire_bell_choices_stay_visible_while_unavailable_and_unlock_in_turn()
    {
        var history = History();
        Choose(history, "Continue");
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
        Choose(history, "Continue");
        Choose(history, "Check the tripwire bell");
        Assert.Contains("The watch bell has sounded 0 times.", history.Current.Document.ToPlainText());
        Assert.Equal(["Ring the bell", "Silence the bell — it is already still", "Return to the fire"],
            history.Current.Menu.Select(entry => entry.Text));
        Assert.Equal([true, false, true], history.Current.Menu.Select(entry => entry.IsAvailable));
        Assert.Equal(new RenderedText("it is already still", TextStyle.Emphasis),
            MenuEntry(history, "Silence the bell — it is already still").Label[^1]);
        Assert.DoesNotContain(history.Current.Choices, choice => choice.Text.StartsWith("Silence"));
        var locked = MenuEntry(history, "Silence the bell — it is already still").Key;
        var cursor = history.Cursor;
        Assert.ThrowsAny<Exception>(() => history.Choose(locked));
        Assert.Equal(cursor, history.Cursor);
        Assert.Equal(0, history.State.GetVariable(new VarId("bell-count")));

        Choose(history, "Ring the bell");
        Assert.Contains("The watch bell has sounded 1 time.", history.Current.Document.ToPlainText());
        Assert.Equal(["Ring the bell", "Silence the bell", "Return to the fire"],
            history.Current.Menu.Select(entry => entry.Text));
        Assert.Equal([false, true, true], history.Current.Menu.Select(entry => entry.IsAvailable));
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Current.Menu.Select(entry => (entry.Text, entry.IsAvailable)),
            restored.Current.Menu.Select(entry => (entry.Text, entry.IsAvailable)));
        Choose(history, "Silence the bell");
        Assert.Contains("The watch bell has sounded 1 time.", history.Current.Document.ToPlainText());
        Assert.False(MenuEntry(history, "Silence the bell — it is already still").IsAvailable);
        history.Back();
        Assert.True(MenuEntry(history, "Silence the bell").IsAvailable);
    }

    [Fact]
    public void River_shows_why_the_rope_crossing_is_unavailable_without_supplies()
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Set out for the temple");
        var rope = MenuEntry(history, "Tie off a rope and cross carefully — you never bought a coil of rope");
        Assert.False(rope.IsAvailable);
        Assert.Equal(1, rope.Number);
        Assert.Equal(["Swim for it", "Let Tomas find a ford"], history.Current.Choices.Select(choice => choice.Text));
    }

    [Theory]
    [InlineData(true, "marked the bank with a strip of red cloth")]
    [InlineData(false, "There is no sign of Tomas")]
    public void Swept_prose_uses_the_guides_check_then_a_labelled_bare_choice(bool success, string prose)
    {
        for (ulong seed = 1; seed <= 128; seed++)
        {
            var history = History(seed: seed);
            Choose(history, "Continue");
            Choose(history, "Set out for the temple");
            Choose(history, "Swim for it");
            if (history.Current.Scene != new SceneId("trail.swept")) continue;
            var check = Assert.Single(history.Events.OfType<SkillCheckResolved>(), check => check.Actor == new EntityId("guide"));
            if (check.Success != success) continue;
            Assert.Equal(new StatId("survival"), check.Skill);
            Assert.False(check.IsHidden);
            Assert.Contains(prose, history.Current.Document.ToPlainText());
            Assert.Contains(history.Current.Document.Blocks, block => block is RenderedNote { Kind: NoteKind.Check });
            var follow = Assert.Single(history.Current.Choices);
            Assert.Equal("Follow Tomas's voice back upstream", follow.Text);
            var events = history.Events.ToArray();
            var restored = StoryHistory.Restore(history.Story, history.Save());
            Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
            Choose(history, "Follow Tomas's voice back upstream");
            Assert.Equal("trail.far-bank", history.Current.Scene!.Value.Value);
            Assert.Null(history.Current.Title);
            Assert.Contains("I'll admit I didn't think you'd get this far", history.Current.Document.ToPlainText());
            history.Back();
            Assert.Equal(events, history.Events);
            return;
        }
        Assert.Fail($"No seed exercised the guide's swept check with success={success}.");
    }

    [Theory]
    [InlineData("explorer", true)]
    [InlineData("scholar", false)]
    public void Hidden_root_climb_check_reveals_a_route_without_a_note_and_squeezing_remains(string preset, bool climbs)
    {
        var history = History(preset, fixedRoll: true);
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Choose(history, "Look for another way in");
        var check = Assert.Single(history.Events.OfType<SkillCheckResolved>());
        Assert.True(check.IsHidden);
        Assert.Equal(new StatId("athletics"), check.Skill);
        Assert.Equal(climbs, check.Success);
        Assert.DoesNotContain(history.Current.Document.Blocks, block => block is RenderedNote { Kind: NoteKind.Check });
        Assert.Equal(climbs, history.Current.Choices.Any(choice => choice.Text == "Climb the fig roots to the high window"));
        Assert.Contains(history.Current.Choices, choice => choice.Text == "Squeeze through");
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.Current.Choices.Select(choice => choice.Text), restored.Current.Choices.Select(choice => choice.Text));
        Choose(history, climbs ? "Climb the fig roots to the high window" : "Squeeze through");
        Assert.Equal("temple.hall", history.Current.Scene!.Value.Value);
    }

    [Theory]
    [InlineData("explorer", "You make out part of the rim", "You can read the whole rim")]
    [InlineData("scholar", "You can read the whole rim", "You make out part of the rim")]
    public void Altar_stores_one_history_total_for_every_threshold(string preset, string expected, string absent)
    {
        var history = History(preset, fixedRoll: true);
        Choose(history, "Continue");
        BuySupplies(history);
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Climb straight to the temple");
        Choose(history, "Look for another way in");
        Choose(history, "Squeeze through");
        Choose(history, "Continue");
        if (history.Current.Scene == new SceneId("temple.niche")) Choose(history, "Leave it be");
        Assert.Equal("temple.altar", history.Current.Scene!.Value.Value);
        var check = Assert.Single(history.Events.OfType<SkillCheckResolved>(), check => check.Skill == new StatId("history"));
        Assert.Null(check.Difficulty);
        Assert.Null(check.Success);
        Assert.Equal(check.Total, history.State.GetVariable(new VarId("altar-reading")));
        var text = history.Current.Document.ToPlainText();
        Assert.Contains(expected, text);
        Assert.DoesNotContain(absent, text);
        Assert.DoesNotContain("too worn for you to read", text);
        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        Choose(history, "Take the relic");
        Assert.True(history.Current.ClearsPrevious);
        Assert.Equal("The Return", history.Current.Title);
        history.Back();
        Assert.False(history.Current.ClearsPrevious);
        history.Forward();
        Assert.True(history.Current.ClearsPrevious);
        Assert.True(StoryHistory.Restore(history.Story, history.Save()).Current.ClearsPrevious);
    }

    [Theory]
    [InlineData("explorer", false)]
    [InlineData("scholar", true)]
    public void Lucidity_joins_the_sheet_when_discovered_without_hiding_creation_options(string preset, bool visibleAtStart)
    {
        var history = History(preset, fixedRoll: true);
        var lucidity = new StatId("lucidity");
        Assert.Equal(visibleAtStart, history.Story.IsStatVisible(lucidity, history.State));
        Assert.True(history.Story.IsStatVisible(new("courage"), history.State));
        Assert.Equal(["courage", "str"], history.Story.GetCreationOptions(42)!.Stats.Keys.Select(stat => stat.Value).Order());
        Choose(history, "Continue");
        Choose(history, "Study your journal");
        Assert.Equal(visibleAtStart, history.Story.IsStatVisible(lucidity, history.State));
        Choose(history, "Puzzle out the glyphs");
        Assert.True(history.Story.IsStatVisible(lucidity, history.State));
        history.Back();
        Assert.Equal(visibleAtStart, history.Story.IsStatVisible(lucidity, history.State));
    }
}
