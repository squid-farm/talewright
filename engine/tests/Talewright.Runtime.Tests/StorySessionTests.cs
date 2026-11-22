using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class StorySessionTests
{
    private static readonly string Yaml = BuildYaml();

    private static string BuildYaml(string randomness = "off")
    {
        var manifest = new ManifestFaker("test", "Test", "camp", 42) { Randomness = randomness, BaseRuleset = "rpg" };
        manifest.Character("player", "Mara", "Burns",
            new Dictionary<string, int> { ["dex"] = 14, ["athletics"] = 2 }, "she");
        manifest.Faction("guild", "Explorers' Guild");
        manifest.Item("relic", "Relic of Ysh", new Dictionary<string, int> { ["wis"] = 2 });
        manifest.Item("rope", "coil of rope");
        manifest.Trigger("outcast", "faction:guild:tier <= unfriendly", EffectFaker.Set(new TaleFlagRef("outcast")), once: true);
        return manifest.Build();
    }

    private static StoryDefinition Compile(string tale, string? yaml = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml ?? Yaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static string Text(StoryFrame frame) => frame.Document.ToPlainText();

    [Fact]
    public void Plays_a_linear_story_to_the_end()
    {
        var session = new StorySession(Compile("""
            === camp
            @title Camp
            {player:first-name} wakes.
            * [Get up] -> road
            * [Sleep in] -> END
            === road
            The road is long.
            -> END
            """));

        var frame = session.Start();
        Assert.Equal(new SceneId("camp"), frame.Scene);
        Assert.Equal("Camp", frame.Title);
        Assert.Equal("Mara wakes.", Text(frame));
        Assert.Equal(["Get up", "Sleep in"], frame.Choices.Select(c => c.Text));

        frame = session.Choose("c0");
        Assert.Equal("The road is long.", Text(frame));
        Assert.True(frame.IsEnded);
        Assert.Empty(frame.Choices);
        Assert.IsType<StoryEnded>(session.Events[^1]);
    }

    [Fact]
    public void Hides_choices_whose_requirements_fail_and_rejects_unknown_keys()
    {
        var session = new StorySession(Compile("""
            === camp
            * [Climb] (requires player has rope) -> END
            * [Walk] -> END
            """));

        var frame = session.Start();
        Assert.Equal(["c1"], frame.Choices.Select(c => c.Key));
        Assert.Throws<InvalidOperationException>(() => session.Choose("c0"));
    }

    [Fact]
    public void Choice_effects_produce_notes_before_the_explicit_target()
    {
        var session = new StorySession(Compile("""
            === camp
            * [Take the relic] { give player relic; faction:guild:standing -= 20 } -> cursed
            * [Leave it] -> safe
            === cursed
            The relic hums. Outcast: {if flag:outcast}yes{else}no{/if}.
            -> END
            === safe
            -> END
            """));

        session.Start();
        var frame = session.Choose("c0");

        Assert.Equal(new SceneId("cursed"), frame.Scene);
        Assert.Equal(
            "[Gained: Relic of Ysh]\n\n[Explorers' Guild −20 (now Unfriendly)]\n\nThe relic hums. Outcast: yes.",
            Text(frame));
        Assert.Equal(12, session.State.GetStat(EntityId.Player, new StatId("wis")));
        Assert.Contains(session.Events, e => e is TriggerFired { TriggerId: "outcast" });
    }

    [Fact]
    public void Entry_effects_and_their_notes_precede_later_prose()
    {
        var session = new StorySession(Compile("""
            === camp
            ~ var:fires += 1
            ~ player:hp -= 3
            Fires: {var:fires}. HP: {player:hp}.
            -> END
            """));

        var frame = session.Start();
        Assert.Equal("[Hit Points −3 (7)]\n\nFires: 1. HP: 7.", Text(frame));
    }

    [Theory]
    [InlineData(12, "success", "top")]
    [InlineData(13, "failure", "bottom")]
    public void Choice_skill_checks_resolve_by_the_ruleset(int dc, string outcome, string scene)
    {
        // randomness: off → take 10. Athletics is governed by str (10 → +0) plus 2 ranks → 12.
        var session = new StorySession(Compile($$"""
            === camp
            * [Climb] skill-check athletics dc {{dc}}
              success: -> top
              failure: { player:hp -= 2 } -> bottom
            === top
            -> END
            === bottom
            -> END
            """));

        session.Start();
        var frame = session.Choose("c0");

        Assert.Equal(new SceneId(scene), frame.Scene);
        var note = Assert.IsType<RenderedNote>(frame.Document.Blocks[0]);
        Assert.Equal(NoteKind.Check, note.Kind);
        Assert.Equal($"Athletics check: 10 +2 = 12 vs DC {dc} — {outcome}", note.Text);
    }

    [Fact]
    public void Entry_checks_resolve_on_entry_and_offer_continue()
    {
        var session = new StorySession(Compile("""
            === camp
            You look around.
            ::: skill-check perception dc 5
            success: { give player rope } -> found
            failure: -> END
            :::
            === found
            A rope!
            -> END
            """));

        var frame = session.Start();
        Assert.Equal("You look around.\n\n[Perception check: 10 +0 = 10 vs DC 5 — success]\n\n[Gained: coil of rope]", Text(frame));
        Assert.Equal([StorySession.ContinueKey], frame.Choices.Select(c => c.Key));
        Assert.Equal("A rope!", Text(session.Choose(StorySession.ContinueKey)));
    }

    [Fact]
    public void Seeded_dice_make_playthroughs_reproducible()
    {
        var story = Compile("""
            === camp
            * [Climb] skill-check athletics dc 12
              success: -> camp
              failure: -> camp
            * [Stop] -> END
            """, BuildYaml("checks-only"));

        static List<GameEvent> Play(StoryDefinition story)
        {
            var session = new StorySession(story, seed: 99);
            session.Start();
            for (var i = 0; i < 10; i++)
            {
                session.Choose("c0");
            }

            return session.Events.ToList();
        }

        var first = Play(story);
        Assert.Equal(first, Play(story));
        Assert.Equal(10, first.OfType<DiceRolled>().Count());
        Assert.True(first.OfType<DiceRolled>().Select(d => d.Result).Distinct().Count() > 1);
    }

    private static string OwnStatYaml(string randomness) => $$"""
        game:
          id: bridge
          title: Bridge
          start: bridge
          randomness: {{randomness}}
          ruleset:
            stats:
              - { id: nerve, name: Nerve }
        characters:
          player: { first-name: Ada, stats: { nerve: 3 } }
        """;

    private static string BridgeTale(int dc) => $"""
        === bridge
        * [Cross the rope bridge] skill-check nerve dc {dc}
          success: -> far-side
          failure: -> river
        === far-side
        -> END
        === river
        -> END
        """;

    [Theory]
    [InlineData(13, "success", "far-side")]
    [InlineData(14, "failure", "river")]
    public void Checks_on_a_story_stat_take_10_without_a_ruleset(int dc, string outcome, string scene)
    {
        var story = Compile(BridgeTale(dc), OwnStatYaml("off"));
        Assert.Equal("none", story.World.Ruleset.Id);

        var session = new StorySession(story);
        session.Start();
        var frame = session.Choose("c0");

        // Nerve is a custom stat, so its value (3) is added to the fixed 10.
        Assert.Equal(new SceneId(scene), frame.Scene);
        var note = Assert.IsType<RenderedNote>(frame.Document.Blocks[0]);
        Assert.Equal($"Nerve check: 10 +3 = 13 vs DC {dc} — {outcome}", note.Text);
        Assert.Empty(session.Events.OfType<DiceRolled>());
    }

    [Fact]
    public void Checks_on_a_story_stat_roll_a_d20_plus_its_value_without_a_ruleset()
    {
        var story = Compile(BridgeTale(12), OwnStatYaml("checks-only"));
        var outcomes = new HashSet<bool>();

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var session = new StorySession(story, seed: seed);
            session.Start();
            var frame = session.Choose("c0");

            var dice = Assert.Single(session.Events.OfType<DiceRolled>());
            var check = Assert.Single(session.Events.OfType<SkillCheckResolved>());
            Assert.Equal(20, dice.Sides);
            Assert.Equal(dice.Result, check.Roll);
            Assert.Equal(3, check.Modifier);
            Assert.Equal(dice.Result + 3, check.Total);
            Assert.Equal(12, check.Difficulty);
            var success = check.Success ?? throw new InvalidOperationException("The check did not record its outcome.");
            Assert.Equal(check.Total >= 12, success);
            Assert.Equal(new SceneId(success ? "far-side" : "river"), frame.Scene);
            outcomes.Add(success);
        }

        Assert.Equal([false, true], outcomes.Order());
    }

    [Fact]
    public void Stories_without_checks_never_roll_dice_without_a_ruleset()
    {
        var story = Compile("""
            === bridge
            * [Cross] -> far-side
            * [Wait] -> bridge
            === far-side
            -> END
            """, OwnStatYaml("checks-only"));

        var session = new StorySession(story, seed: 7);
        session.Start();
        session.Choose("c1");
        session.Choose("c0");

        Assert.Empty(session.Events.OfType<DiceRolled>());
        Assert.Empty(session.Events.OfType<SkillCheckResolved>());
    }

    [Fact]
    public void Hubs_offer_eligible_storylets_by_priority()
    {
        var session = new StorySession(Compile("""
            === camp
            @hub camp
            The fire crackles.
            * [Sleep] -> END
            === camp.talk
            @title Talk to the guide
            @tags camp
            @once
            You talk.
            -> camp
            === camp.secret
            @title Dig under the tent
            @tags camp
            @priority 5
            @requires player has rope
            -> camp
            === camp.map
            @title Study the map
            @tags camp
            @priority 10
            -> camp
            """));

        var frame = session.Start();
        Assert.Equal(["storylet:camp.map", "c0", "storylet:camp.talk"], frame.Choices.Select(c => c.Key));
        Assert.Equal("Talk to the guide", frame.Choices[2].Text);

        frame = session.Choose("storylet:camp.talk");
        Assert.Equal("You talk.", Text(frame));
        frame = session.Choose(StorySession.ContinueKey);

        // @once: talked out.
        Assert.Equal(["storylet:camp.map", "c0"], frame.Choices.Select(c => c.Key));
    }

    [Fact]
    public void Hub_labels_prefer_choice_label_then_title_then_scene_id_and_shown_text()
    {
        var session = new StorySession(Compile("""
            === camp
            @hub destination
            * [Wait] -> END

            === both
            @title The conversation
            @choice-label Speak to {player:first-name}
            @tags destination
            * [Wait] -> END

            === title-only
            @title The old label
            @tags destination
            * [Wait] -> END

            === no-label
            @tags destination
            * [Wait] -> END

            === unavailable
            @title The hidden title
            @choice-label The hidden label
            @tags destination
            @requires false, shown "Need a key"
            * [Wait] -> END
            """));

        var frame = session.Start();
        var entries = frame.Menu.ToDictionary(entry => entry.Key, entry => (entry.Text, entry.IsAvailable));
        Assert.Equal(("Speak to Mara", true), entries["storylet:both"]);
        Assert.Equal(("The old label", true), entries["storylet:title-only"]);
        Assert.Equal(("no-label", true), entries["storylet:no-label"]);
        Assert.Equal(("Need a key", false), entries["storylet:unavailable"]);

        frame = session.Choose("storylet:both");
        Assert.Equal("The conversation", frame.Title);
    }

    [Fact]
    public void Bare_scene_diverts_use_destination_choice_labels_but_other_continuations_stay_continue()
    {
        var session = new StorySession(Compile("""
            === camp
            * -> labeled
            * -> both
            * -> title-only
            * -> no-label
            * -> END

            === labeled
            @choice-label Ask {player:first-name}
            * [Wait] -> END

            === both
            @title The meeting
            @choice-label Speak to the guide
            * [Wait] -> END

            === title-only
            @title The old title
            * [Wait] -> END

            === no-label
            * [Wait] -> END
            """));

        var frame = session.Start();
        Assert.Equal(["Ask Mara", "Speak to the guide", "Continue", "Continue", "Continue"],
            frame.Choices.Select(choice => choice.Text));

        frame = session.Choose("c0");
        Assert.Null(frame.Title);
    }

    [Fact]
    public void Choice_labeled_scene_without_title_does_not_inherit_a_heading_when_it_ends()
    {
        var session = new StorySession(Compile("""
            === camp
            @title The opening
            * -> ending

            === ending
            @choice-label Goodbye
            -> END
            """));

        session.Start();
        var frame = session.Choose("c0");

        Assert.True(frame.IsEnded);
        Assert.Null(frame.Title);
    }

    [Fact]
    public void Experience_triggers_level_ups()
    {
        var session = new StorySession(Compile("""
            === camp
            ~ player:xp += 150
            Level {player:level}.
            -> END
            """));

        var frame = session.Start();
        Assert.Equal("[Experience +150 (150)]\n\n[Level 2!]\n\nLevel 2.", Text(frame));
    }

    [Fact]
    public void Level_triggers_fire_after_the_level_up_in_the_same_choice()
    {
        const string yaml = """
            game:
              id: test
              title: Test
              start: camp
              randomness: off
              ruleset: rpg
            characters:
              player: { first-name: Mara, pronouns: she }
            triggers:
              - id: level-two
                when: player:level >= 2
                do: set flag:leveled
                once: true
            """;
        var session = new StorySession(Compile("""
            === camp
            * [Earn experience] { player:xp += 150 } -> END
            """, yaml));

        session.Start();
        var frame = session.Choose("c0");

        Assert.True(frame.IsEnded);
        Assert.True(session.State.GetFlag(new FlagId("leveled")));
        Assert.Contains(session.Events, e => e is TriggerFired { TriggerId: "level-two" });
    }

    [Fact]
    public void Trigger_effects_are_evaluated_sequentially_before_later_triggers()
    {
        const string yaml = """
            game: { id: trigger-charm, title: Trigger charm, start: start, randomness: off, ruleset: rpg }
            characters:
              player: { first-name: Ada, inventory: { charm-of-life: 1 } }
            items:
              charm-of-life: { name: charm of life }
            triggers:
              - id: resurrection-charm
                when: player has charm-of-life and player:hp <= 0
                do:
                  effect: player:hp += 10; take player charm-of-life
              - id: death
                when: player:hp <= 0
                do:
                  divert: death-ending
            """;
        const string tale = """
            === start
            ~ player:hp = 0
            Still standing.
            * [Continue] -> safe
            === safe
            You survived.
            -> END
            === death-ending
            The death ending.
            -> END
            """;

        var living = new StoryHistory(Compile(tale, yaml), 42);
        Assert.Equal("start", living.Current.Scene!.Value.Value);
        Assert.Equal(10, living.State.GetStat(EntityId.Player, new StatId("hp")));
        Assert.Equal(0, living.State.GetItemCount(EntityId.Player, new ItemId("charm-of-life")));
        Assert.DoesNotContain(living.Events, gameEvent => gameEvent is TriggerDiverted);
        Assert.Equal(["resurrection-charm"], living.Events.OfType<TriggerFired>().Select(trigger => trigger.TriggerId));

        var withoutCharm = yaml.Replace("inventory: { charm-of-life: 1 }", "inventory: {}",
            StringComparison.Ordinal);
        var doomed = new StoryHistory(Compile(tale, withoutCharm), 42);
        Assert.Equal("death-ending", doomed.Current.Scene!.Value.Value);
        Assert.Contains("The death ending.", doomed.Current.Document.ToPlainText());
        Assert.Equal("death-ending", Assert.Single(doomed.Events.OfType<TriggerDiverted>()).Target!.Value.Value);
    }

    [Fact]
    public void Combined_trigger_effect_runs_before_its_divert_and_replaces_the_choice_target()
    {
        const string yaml = """
            game: { id: trigger-choice, title: Trigger choice, start: start, randomness: off }
            characters: { player: { first-name: Ada } }
            items: { relic: { name: relic } }
            triggers:
              - id: reroute
                when: flag:ready
                do:
                  divert: redirected
                  effect: give player relic
            """;
        const string tale = """
            === start
            * [Go] { set flag:ready } -> original
            === original
            The original destination.
            -> END
            === redirected
            The redirected passage.
            -> END
            """;
        var story = Compile(tale, yaml);
        var history = new StoryHistory(story, 42);

        history.Choose("c0");

        Assert.Equal("redirected", history.Current.Scene!.Value.Value);
        Assert.Contains("[Gained: relic]", history.Current.Document.ToPlainText());
        Assert.DoesNotContain(history.Events.OfType<SceneEntered>(), entered => entered.Scene.Value == "original");
        var redirect = Assert.Single(history.Events.OfType<TriggerDiverted>());
        Assert.Equal("reroute", redirect.TriggerId);
        Assert.Equal("start", redirect.Source!.Value.Value);
        Assert.Equal("redirected", redirect.Target!.Value.Value);

        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Current.Scene, restored.Current.Scene);
        Assert.Equal(history.Current.Document.ToPlainText(), restored.Current.Document.ToPlainText());
        Assert.Equal(history.Events.OfType<TriggerDiverted>(), restored.Events.OfType<TriggerDiverted>());
    }

    [Fact]
    public void Last_trigger_divert_wins_across_cascade_passes_without_skipping_effects()
    {
        const string yaml = """
            game: { id: trigger-cascade, title: Trigger cascade, start: start, randomness: off }
            characters: { player: { first-name: Ada } }
            items:
              relic: { name: relic }
              rope: { name: rope }
            triggers:
              - id: later
                when: flag:second
                do:
                  divert: final
                  effect: give player rope
              - id: first
                when: flag:first
                do:
                  divert: intermediate
                  effect: give player relic; set flag:second
            """;
        const string tale = """
            === start
            ~ set flag:first
            This line must be interrupted.
            -> END
            === intermediate
            This destination is overridden.
            -> END
            === final
            Final destination.
            -> END
            """;
        var history = new StoryHistory(Compile(tale, yaml), 42);

        Assert.Equal("final", history.Current.Scene!.Value.Value);
        Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new ItemId("relic")));
        Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new ItemId("rope")));
        Assert.Equal(["first", "later"], history.Events.OfType<TriggerFired>().Select(trigger => trigger.TriggerId));
        Assert.Equal("final", Assert.Single(history.Events.OfType<TriggerDiverted>()).Target!.Value.Value);
        Assert.DoesNotContain(history.Events.OfType<SceneEntered>(), entered => entered.Scene.Value == "intermediate");
    }

    [Fact]
    public void Trigger_divert_after_scene_entry_consequences_skips_the_entered_scene_body()
    {
        const string yaml = """
            game: { id: trigger-entry, title: Trigger entry, start: start }
            characters: { player: { first-name: Ada } }
            triggers:
              - id: redirect-on-entry
                when: scene:player:visits >= 1
                do:
                  divert: destination
            """;
        const string tale = """
            === start
            This body must not run.
            -> END
            === destination
            Destination reached.
            -> END
            """;
        var history = new StoryHistory(Compile(tale, yaml), 42);

        Assert.Equal("destination", history.Current.Scene!.Value.Value);
        Assert.Contains("Destination reached.", history.Current.Document.ToPlainText());
        Assert.DoesNotContain("This body must not run.",
            string.Join("\n", history.Transcript.Select(entry => entry.Frame.Document.ToPlainText())));
        Assert.Equal("start", Assert.Single(history.Events.OfType<TriggerDiverted>()).Source!.Value.Value);
    }

    [Fact]
    public void Trigger_divert_after_entry_check_outcome_overrides_its_authored_target()
    {
        const string yaml = """
            game: { id: trigger-check, title: Trigger check, start: start, randomness: off, ruleset: rpg }
            characters: { player: { first-name: Ada } }
            triggers:
              - id: redirect-on-success
                when: flag:success
                do:
                  divert: redirected
            """;
        const string tale = """
            === start
            ::: skill-check athletics dc 1
            success: { set flag:success } -> original
            failure: -> original
            :::
            This line must not run.
            -> END
            === original
            The check destination.
            -> END
            === redirected
            Redirected after the check.
            -> END
            """;
        var history = new StoryHistory(Compile(tale, yaml), 42);

        Assert.Equal("redirected", history.Current.Scene!.Value.Value);
        Assert.Contains("Redirected after the check.", history.Current.Document.ToPlainText());
        Assert.DoesNotContain(history.Events.OfType<SceneEntered>(), entered => entered.Scene.Value == "original");
        Assert.Equal("redirected", Assert.Single(history.Events.OfType<TriggerDiverted>()).Target!.Value.Value);
    }

    [Fact]
    public void Trigger_can_divert_to_END()
    {
        const string yaml = """
            game: { id: trigger-end, title: Trigger end, start: start }
            characters: { player: { first-name: Ada } }
            triggers:
              - id: finish
                when: flag:finished
                do:
                  divert: END
            """;
        const string tale = """
            === start
            ~ set flag:finished
            This line must not run.
            -> END
            """;
        var history = new StoryHistory(Compile(tale, yaml), 42);

        Assert.True(history.Current.IsEnded);
        Assert.Equal("finish", Assert.Single(history.Events.OfType<TriggerDiverted>()).TriggerId);
        Assert.Null(Assert.Single(history.Events.OfType<TriggerDiverted>()).Target);
        Assert.DoesNotContain("This line must not run.", history.Current.Document.ToPlainText());
    }
}
