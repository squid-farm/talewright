namespace Talewright.Testing;

public static class TestStories
{
    public const string DefaultYaml = """
        id: player-test
        title: Player Test
        start: start
        randomness: off
        seed: 42
        characters:
          player:
            first-name: Mara
            last-name: Burns
            pronouns: she
            stats: { athletics: 2 }
          guide:
            first-name: Tomas
            pronouns: he
            stats: { survival: 4 }
        factions:
          guild: { name: Test Guild, standing: 0 }
        items:
          rope: { name: Test rope }
          charm: { name: Test charm, modifiers: { wis: 2 } }
          relic: { name: Test relic, modifiers: { cha: 2 } }
        vars: { visits: 0 }
        triggers:
          - id: wary
            when: faction:guild <= unfriendly
            do: set flag:wary
            once: true
        """;

    public static readonly StoryFixture Linear = new("linear", DefaultYaml, """
        === start
        @title First passage
        Mara's **bold** *gentle* `code` and ***combined*** words.

        > guide: Hello, {player:name}.

        A line\
        continued.

        ![Reserved artwork](test-art)

        * [Walk onward] -> second
        * [Finish early] -> finish
        === second
        @title Second passage
        Second passage text.
        -> finish
        === finish
        @title Finished
        Finished safely.
        -> END
        """);

    public static readonly StoryFixture Gates = new("gates", DefaultYaml, """
        === start
        @title Gate room
        @hub room
        Inventory: {if player has rope}ready{else}empty{/if}.
        * [Use the rope] (requires: player has rope) -> finish
        * [Read the flag] (requires: flag:ready) -> flag
        * [Trusted passage] (requires: faction:guild >= friendly) -> finish
        * [Strong passage] (requires: player:str >= 12) -> finish
        * [Leave empty] -> finish
        === supply
        @title Collect supplies
        @tags room
        @priority 10
        @once
        ~ give player rope 2
        ~ give player charm
        ~ player:str += 2
        ~ faction:guild += 20
        ~ set flag:ready
        Supplies collected.
        -> start
        === talk
        @title Talk once
        @tags room
        @once
        The guide answers.
        -> start
        === flag
        The flag is ready.
        -> start
        === finish
        Gate route complete.
        -> END
        """);

    public static readonly StoryFixture Progression = new("progression", DefaultYaml, """
        === start
        Choose the relic's fate.
        * [Collect relic] { give player relic; player:xp += 150 } -> return
        * [Leave empty] -> empty
        === return
        Level {player:level}.
        * [Donate relic] { take player relic; faction:guild += 20 } -> donated
        * [Keep relic] { faction:guild -= 30 } -> kept
        === donated
        The donation is complete. Standing: {faction:guild:tier}.
        -> END
        === kept
        {if flag:wary}
        The guild is wary.
        {if player:level >= 2 and (player has relic or visited(return))}
        Ready{if player has relic}, relic watching{/if}.
        {else}
        Not ready.
        {/if}
        {else}
        The guild is calm.
        {/if}
        -> END
        === empty
        No relic, no trouble.
        -> END
        """);

    public static readonly StoryFixture Conditional = new("conditional", DefaultYaml, """
        === start
        Choose a condition.
        * [Outer false] -> result
        * [Inner false] { faction:guild -= 30 } -> result
        * [Inner true] { faction:guild -= 30; give player relic; player:xp += 150 } -> result
        * [Inner true without relic] { faction:guild -= 30; player:xp += 150 } -> result
        === result
        {if flag:wary}
        Outer true.
        {if player:level >= 2 and (player has relic or visited(start))}
        Inner true{if player has relic}, relic watching{/if}.
        {else}
        Inner false.
        {/if}
        {else}
        Outer false.
        {/if}
        -> END
        """);

    public static readonly StoryFixture Custom = new("custom", """
        id: custom
        title: Custom Player
        start: start
        randomness: off
        ruleset:
          base: none
          stats:
            - { id: affection, name: Affection, category: custom, default: 3 }
        characters:
          player: { first-name: Alex, pronouns: they }
        """, """
        === start
        Alex's quiet story.
        * [Make a friend] { player:affection += 2 } -> finish
        === finish
        Friendship grows.
        -> END
        """);

    public static readonly StoryFixture NoStats = new("no-stats", """
        id: no-stats
        title: Quiet Player
        start: start
        randomness: off
        ruleset: { base: none }
        characters:
          player: { first-name: Alex, pronouns: they }
        """, """
        === start
        A story without statistics.
        -> END
        """);

    public static readonly StoryFixture Unicode = new("unicode", DefaultYaml, """
        === start
        Café, 日本語, and <script>window.storyExecuted = true</script>.
        * [Continue peacefully] -> finish
        === finish
        Unicode preserved: naïve.
        -> END
        """);

    public static readonly StoryFixture Long = new("long", DefaultYaml,
        "=== start\n@title Long beginning\n" +
        string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i}: readable text for scrolling.")) +
        "\n* [Next passage] -> next\n=== next\n@title New passage\nThe new passage is visible.\n* [End the story] -> END\n");

    public static readonly StoryFixture Seeded = new("seeded",
        DefaultYaml.Replace("randomness: off", "randomness: checks-only", StringComparison.Ordinal), """
        === start
        * [Roll once] skill-check athletics dc 12
          success: -> won
          failure: -> lost
        === won
        Seeded success.
        -> END
        === lost
        Seeded failure.
        -> END
        """);

    public static readonly StoryFixture NoRoute = new("no-route", DefaultYaml, """
        === start
        No route is available.
        * [Use a missing rope] (requires: player has rope) -> END
        """);

    public static StoryFixture Check(string id, string actor, bool entry, bool success)
    {
        var skill = actor == "guide" ? "guide survival" : "athletics";
        var dc = success ? 1 : 100;
        var tale = entry
            ? $$"""
                === start
                Entry check.
                ::: skill-check {{skill}} dc {{dc}}
                success: { give player rope } -> won
                failure: { player:hp -= 2 } -> lost
                :::
                """
            : $$"""
                === start
                Choice check.
                * [Try the check] skill-check {{skill}} dc {{dc}}
                  success: { give player rope } -> won
                  failure: { player:hp -= 2 } -> lost
                """;
        return new StoryFixture(id, DefaultYaml, tale + """

            === won
            Check succeeded.
            -> END
            === lost
            Check failed.
            -> END
            """);
    }

    public static IReadOnlyList<StoryFixture> All { get; } =
        new[] { Linear, Gates, Progression, Conditional, Custom, NoStats, Unicode, Long, Seeded, NoRoute }
        .Concat(from entry in new[] { false, true }
                from actor in new[] { "player", "guide" }
                from success in new[] { false, true }
                select Check($"check-{actor}-{entry}-{success}".ToLowerInvariant(), actor, entry, success))
        .ToArray();

    public static StoryFixture Get(string id) => All.Single(f => f.Id == id);

    public static IReadOnlyList<PlayerScenario> Scenarios { get; } = CreateScenarios();

    private static PlayerScenario[] CreateScenarios()
    {
        var scenarios = new List<PlayerScenario>
        {
            new("linear", "linear", [
                new(null, "Hello, Mara Burns.", ["Walk onward", "Finish early"]),
                new("Walk onward", "Second passage text.", ["Continue"]),
                new("Continue", "Finished safely.", [], Ended: true)]),
            new("early-ending", "linear", [
                new(null, "First passage", ["Walk onward", "Finish early"]),
                new("Finish early", "Finished safely.", [], Ended: true)]),
            new("gates", "gates", [
                new(null, "Inventory: empty.", ["Leave empty", "Collect supplies", "Talk once"]),
                new("Collect supplies", "Supplies collected.", ["Continue"], ["Test rope ×2", "Test charm", "(+2)", "Friendly"]),
                new("Continue", "Inventory: ready.", ["Use the rope", "Read the flag", "Trusted passage", "Strong passage", "Leave empty", "Talk once"]),
                new("Read the flag", "The flag is ready.", ["Continue"]),
                new("Continue", "Inventory: ready."),
                new("Talk once", "The guide answers.", ["Continue"]),
                new("Continue", "Inventory: ready.", ["Use the rope", "Read the flag", "Trusted passage", "Strong passage", "Leave empty"]),
                new("Use the rope", "Gate route complete.", [], Ended: true)]),
            new("empty-gate", "gates", [
                new(null, "Inventory: empty."),
                new("Leave empty", "Gate route complete.", [], Ended: true)]),
            new("trusted-gate", "gates", [
                new(null, "Inventory: empty."),
                new("Collect supplies", "Supplies collected."),
                new("Continue", "Inventory: ready."),
                new("Trusted passage", "Gate route complete.", [], Ended: true)]),
            new("strong-gate", "gates", [
                new(null, "Inventory: empty."),
                new("Collect supplies", "Supplies collected."),
                new("Continue", "Inventory: ready."),
                new("Strong passage", "Gate route complete.", [], Ended: true)]),
            new("donate", "progression", [
                new(null, "", ["Collect relic", "Leave empty"]),
                new("Collect relic", "Level 2.", ["Donate relic", "Keep relic"], ["Test relic", "(+2)"]),
                new("Donate relic", "The donation is complete.", [], ["Friendly"], true)]),
            new("keep", "progression", [
                new(null, ""),
                new("Collect relic", "Level 2."),
                new("Keep relic", "Ready, relic watching.", [], ["Unfriendly"], true)]),
            new("leave", "progression", [
                new(null, ""),
                new("Leave empty", "No relic, no trouble.", [], Ended: true)]),
            new("custom", "custom", [
                new(null, "Alex's quiet story.", ["Make a friend"], ["Affection", "3"]),
                new("Make a friend", "Friendship grows.", [], ["Affection", "5"], true)]),
            new("no-stats", "no-stats", [
                new(null, "A story without statistics.", [], ["Nothing."], true)]),
            new("no-route", "no-route", [
                new(null, "The story cannot continue from 'start'", [], Ended: true)]),
            new("unicode", "unicode", [
                new(null, "Café, 日本語"),
                new("Continue peacefully", "Unicode preserved: naïve.", [], Ended: true)]),
        };
        foreach (var arm in new[] { "Outer false", "Inner false", "Inner true" })
        {
            scenarios.Add(new("conditional-" + arm.Replace(' ', '-').ToLowerInvariant(), "conditional", [
                new(null, ""), new(arm, arm + (arm == "Inner true" ? ", relic watching." : "."), [], Ended: true)]));
        }
        scenarios.Add(new("conditional-no-relic", "conditional", [
            new(null, ""), new("Inner true without relic", "Inner true.", [], Ended: true)]));

        foreach (var entry in new[] { false, true })
        foreach (var actor in new[] { "player", "guide" })
        foreach (var success in new[] { false, true })
        {
            var id = $"check-{actor}-{entry}-{success}".ToLowerInvariant();
            var steps = new List<PlayerStep> { new(null, entry ? "Entry check." : "Choice check.") };
            steps.Add(new(entry ? "Continue" : "Try the check",
                success ? "Check succeeded." : "Check failed.", [],
                success ? ["Test rope"] : ["8"], true));
            scenarios.Add(new(id, id, steps.ToArray()));
        }

        return scenarios.ToArray();
    }
}
