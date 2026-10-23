using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public class StorySessionTests
{
    private const string Yaml = """
        id: test
        title: Test
        start: camp
        randomness: off
        characters:
          player:
            first-name: Mara
            last-name: Burns
            pronouns: she
            stats: { dex: 14, athletics: 2 }
        factions:
          guild: { name: "Explorers' Guild", standing: 0 }
        items:
          relic: { name: Relic of Ysh, modifiers: { wis: 2 } }
          rope: { name: coil of rope }
        triggers:
          - id: outcast
            when: faction:guild <= unfriendly
            do: set flag:outcast
            once: true
        """;

    private static StoryDefinition Compile(string tale, string yaml = Yaml)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml,
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
            * [Climb] (requires: player has rope) -> END
            * [Walk] -> END
            """));

        var frame = session.Start();
        Assert.Equal(["c1"], frame.Choices.Select(c => c.Key));
        Assert.Throws<InvalidOperationException>(() => session.Choose("c0"));
    }

    [Fact]
    public void Choice_effects_produce_notes_and_steer_fall_through_diverts()
    {
        var session = new StorySession(Compile("""
            === camp
            * [Take the relic] { give player relic; faction:guild -= 20 }
            * [Leave it]
            {if player has relic}
            -> cursed
            {/if}
            -> safe
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
    public void Entry_effects_apply_before_rendering()
    {
        var session = new StorySession(Compile("""
            === camp
            ~ var:fires += 1
            ~ player:hp -= 3
            Fires: {var:fires}. HP: {player:hp}.
            -> END
            """));

        var frame = session.Start();
        Assert.Equal("Fires: 1. HP: 7.\n\n[Hit Points −3 (7)]", Text(frame));
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
            """, Yaml.Replace("randomness: off", "randomness: checks-only"));

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
        Assert.Equal(["c0", "storylet:camp.map", "storylet:camp.talk"], frame.Choices.Select(c => c.Key));
        Assert.Equal("Talk to the guide", frame.Choices[2].Text);

        frame = session.Choose("storylet:camp.talk");
        Assert.Equal("You talk.", Text(frame));
        frame = session.Choose(StorySession.ContinueKey);

        // @once: talked out.
        Assert.Equal(["c0", "storylet:camp.map"], frame.Choices.Select(c => c.Key));
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
        Assert.Equal("Level 2.\n\n[Experience +150 (150)]\n\n[Level 2!]", Text(frame));
    }

    [Fact]
    public void Level_triggers_fire_after_the_level_up_in_the_same_choice()
    {
        const string yaml = """
            id: test
            title: Test
            start: camp
            randomness: off
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
}
