using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Expressions;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public sealed class AuthorSessionTests
{
    private static readonly EntityId Guide = new("guide");
    private static readonly ItemId Rope = new("rope");
    private static readonly StatId Strength = new("str");
    private static readonly FlagId Ready = new("ready");
    private static readonly VarId Count = new("count");
    private static readonly FactionId Guild = new("guild");
    private const string Yaml = """
        id: author
        title: Author test
        start: start
        randomness: checks-only
        flags: [ready, fired]
        vars: {count: 0}
        characters:
          player: {first-name: Mara, inventory: {rope: 2}}
          guide: {first-name: Guide, stats: {str: 11}}
        items:
          rope: {name: Rope, modifiers: {str: 2}}
        factions:
          guild: {name: Guild, standing: 0}
        """;

    private static StoryDefinition Compile(string tale, string yaml = Yaml, CompilerOptions? options = null)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = yaml,
            ["main.tale"] = tale,
        }), options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static AuthorSession Start(StoryDefinition story, AuthorSetup? setup = null)
        => AuthorSession.Create(story, new SceneId("start"), setup, seed: 42);

    [Fact]
    public void Jumps_to_scene_1000_without_visiting_or_executing_skipped_scenes()
    {
        var evaluated = 0;
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Observe", _ => { evaluated++; return true; }),
        };
        var story = Compile(string.Join("\n", Enumerable.Range(1, 1000).Select(i => $$"""
            === scene{{i}}
            {if @Observe}Scene {{i}}.{/if}
            ~ var:count += 1
            -> {{(i == 1000 ? "END" : $"scene{i + 1}")}}
            """)), Yaml.Replace("start: start", "start: scene1"), options);
        var session = AuthorSession.Create(story, new SceneId("scene1000"), seed: 42);

        Assert.True(session.IsSynthetic);
        Assert.Equal(1, evaluated);
        Assert.Equal(new SceneId("scene1000"), session.Current.Scene);
        Assert.Equal([new SceneEntered(new SceneId("scene1000"))], session.Events.OfType<SceneEntered>());
        Assert.Empty(session.Events.OfType<ChoiceMade>());
        Assert.Equal(0, session.State.VisitCount(new SceneId("scene1")));
        Assert.Equal(1, session.State.GetVariable(Count));
        Assert.Empty(session.Checkpoint!.Events);
        Assert.Equal(0, session.Checkpoint.State.VisitCount(new SceneId("scene1000")));
    }

    [Fact]
    public void Setup_is_applied_before_entry_and_shows_requested_and_clamped_values()
    {
        var story = Compile("""
            === start
            ~ var:count += 1
            Ready: {if flag:ready}yes{else}no{/if}. Count: {var:count}.
            * [Prepared] (requires: flag:ready and guide:str >= 15 and faction:guild >= 5) -> END
            * [Wait] -> END
            """);
        var requested = int.MaxValue;
        var setup = new AuthorSetup
        {
            Flags = new Dictionary<FlagId, bool> { [Ready] = true },
            Variables = new Dictionary<VarId, int> { [Count] = 7 },
            Standings = new Dictionary<FactionId, int> { [Guild] = 5 },
            Stats = [new AuthorStatValue(Guide, Strength, requested)],
            Inventory = [new AuthorItemValue(Guide, Rope, 3), new AuthorItemValue(EntityId.Player, Rope, 0)],
        };
        var session = Start(story, setup);
        var checkpoint = session.Checkpoint!;

        Assert.Equal(requested, session.Setup.Stats[0].Value);
        Assert.Equal(story.World.Ruleset.StatsById[Strength].Max, checkpoint.State.GetBaseStat(Guide, Strength));
        Assert.Equal(3, checkpoint.State.GetItemCount(Guide, Rope));
        Assert.Equal(0, checkpoint.State.GetItemCount(EntityId.Player, Rope));
        Assert.Equal(5, checkpoint.State.GetStanding(Guild));
        Assert.Equal(7, checkpoint.State.GetVariable(Count));
        Assert.Equal(8, session.State.GetVariable(Count));
        Assert.Contains("Ready: yes. Count: 8.", session.Current.Document.ToPlainText());
        Assert.Equal(["c0", "c1"], session.Current.Choices.Select(c => c.Key));
        Assert.DoesNotContain(checkpoint.Events, e => e is SceneEntered or TriggerFired or DiceRolled or ChoiceMade);
        Assert.IsNotType<GameState>(session.State);
        Assert.IsNotType<GameState>(checkpoint.State);
        Assert.Equal(11, story.World.Characters[Guide].Stats[Strength]);
    }

    [Fact]
    public void Setup_primes_true_triggers_but_entry_rising_edges_and_leveling_run_normally()
    {
        var story = Compile("""
            === start
            * [Lower] { clear flag:ready } -> start
            * [Raise] { set flag:ready; player:xp += 150 } -> start
            """, Yaml + """

            triggers:
              - id: ready
                when: flag:ready
                do: var:count += 1
                once: false
              - id: leveled
                when: player:level >= 2
                do: set flag:fired
                once: true
            """);
        var session = Start(story, new AuthorSetup { Flags = new Dictionary<FlagId, bool> { [Ready] = true } });
        Assert.Empty(session.Events.OfType<TriggerFired>());
        session.Choose("c0");
        session.Choose("c1");
        Assert.Equal(1, session.State.GetVariable(Count));
        Assert.True(session.State.GetFlag(new FlagId("fired")));
        Assert.Equal(["ready", "leveled"], session.Events.OfType<TriggerFired>().Select(e => e.TriggerId));

        var rebuilt = session.Checkpoint!.Reenter();
        Assert.Equal(session.Events, rebuilt.Events);
        Assert.Equal(3, rebuilt.State.VisitCount(new SceneId("start")));
        rebuilt.Choose("c1");
        Assert.Single(rebuilt.Events.OfType<TriggerFired>(), e => e.TriggerId == "ready");
        Assert.Single(rebuilt.Events.OfType<TriggerFired>(), e => e.TriggerId == "leveled");
        rebuilt.Choose("c0");
        rebuilt.Choose("c1");
        Assert.Equal(2, rebuilt.State.GetVariable(Count));
        Assert.Equal(1, session.State.GetVariable(Count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void Checkpoint_rebuilds_choice_effects_checks_pending_notes_and_entry_roll_once(int dc)
    {
        var story = Compile($$"""
            === start
            * [Try] { var:count += 3 } skill-check athletics dc 1
              success: { give guide rope } -> target
              failure: -> target
            === target
            ~ var:count += 1
            Count: {var:count}.
            ::: skill-check perception dc {{dc}}
            success: -> finish
            failure: -> finish
            :::
            === finish
            -> END
            """);
        var session = Start(story);
        session.Choose("c0");
        var checkpoint = session.Checkpoint!;
        Assert.Equal(new SceneId("target"), checkpoint.Scene);
        Assert.Equal(new SceneId("start"), checkpoint.State.CurrentScene);
        Assert.Equal(0, checkpoint.State.VisitCount(new SceneId("target")));
        Assert.Equal(1, checkpoint.State.RollCount);
        Assert.Equal(3, checkpoint.State.GetVariable(Count));
        Assert.NotEmpty(checkpoint.PendingNotes);
        Assert.Equal(4, session.State.GetVariable(Count));
        Assert.Equal(2, session.State.RollCount);
        Assert.Equal(1, session.State.VisitCount(new SceneId("target")));

        var rebuilt = checkpoint.Reenter();
        Assert.Equal(session.Events, rebuilt.Events);
        Assert.Equal(session.Current.Document.ToPlainText(), rebuilt.Current.Document.ToPlainText());
        Assert.Equal(session.Current.Choices.Select(c => (c.Key, c.Text)),
            rebuilt.Current.Choices.Select(c => (c.Key, c.Text)));
        rebuilt.Choose(StorySession.ContinueKey);
        session.Choose(StorySession.ContinueKey);
        Assert.Equal(session.Events, rebuilt.Events);
        Assert.Equal(new SceneId("finish"), rebuilt.Current.Scene);
        Assert.Equal(1, checkpoint.State.RollCount);
        Assert.Equal(3, checkpoint.State.GetVariable(Count));
    }

    [Fact]
    public void Setup_order_is_canonical_and_inventory_quantities_are_absolute()
    {
        var story = Compile("=== start\n* [Wait] -> start");
        var first = new AuthorSetup
        {
            Flags = new Dictionary<FlagId, bool> { [Ready] = true, [new("fired")] = true },
            Stats = [new(Guide, Strength, 12), new(EntityId.Player, Strength, 14)],
            Inventory = [new(Guide, Rope, int.MaxValue), new(EntityId.Player, Rope, 1)],
        };
        var second = first with
        {
            Flags = first.Flags.Reverse().ToDictionary(),
            Stats = first.Stats.Reverse().ToArray(),
            Inventory = first.Inventory.Reverse().ToArray(),
        };
        var session = Start(story, first);
        Assert.Equal(session.Events, Start(story, second).Events);
        Assert.Equal(int.MaxValue, session.State.GetItemCount(Guide, Rope));
        Assert.Equal(1, session.State.GetItemCount(EntityId.Player, Rope));
    }

    [Fact]
    public void Setup_and_character_inputs_are_snapshotted_and_reentry_is_independent()
    {
        var story = Compile("""
            === start
            {player:first-name}: {player:str}. {var:count}.
            * [Next] { var:count += 1 } -> start
            """, Yaml + """

            creation:
              name: true
              stats:
                str: [8, 12, 16]
            """);
        var variables = new Dictionary<VarId, int> { [Count] = 9 };
        var stats = new List<AuthorStatValue> { new(EntityId.Player, Strength, 16) };
        var creationStats = new Dictionary<string, int> { ["str"] = 12 };
        var character = story.Creation!.Default(story.World.Characters[EntityId.Player]) with
        {
            FirstName = "Robin", Stats = creationStats,
        };
        var session = AuthorSession.Create(story, new SceneId("start"),
            new AuthorSetup { Variables = variables, Stats = stats }, 42, character);
        var checkpoint = session.Checkpoint!;
        variables[Count] = 99;
        stats.Clear();
        creationStats["str"] = 999;

        Assert.Equal(9, checkpoint.Reenter().State.GetVariable(Count));
        Assert.Equal(16, session.State.GetBaseStat(EntityId.Player, Strength));
        Assert.Equal(12, session.Character!.Stats["str"]);
        Assert.Contains("Robin: 18. 9.", session.Current.Document.ToPlainText());
        Assert.Equal("Mara", story.World.Characters[EntityId.Player].FirstName);
        var snapshot = session.State;
        session.Choose("c0");
        Assert.Equal(9, snapshot.GetVariable(Count));
        Assert.Equal(9, checkpoint.Reenter().State.GetVariable(Count));
        Assert.Equal(10, session.State.GetVariable(Count));
        Assert.Throws<ArgumentException>(() => AuthorSession.Create(story, new SceneId("start")));
    }

    public static IEnumerable<object[]> InvalidSetup()
    {
        yield return [new AuthorSetup { Flags = new Dictionary<FlagId, bool> { [new("missing")] = true } }];
        yield return [new AuthorSetup { Variables = new Dictionary<VarId, int> { [new("missing")] = 1 } }];
        yield return [new AuthorSetup { Standings = new Dictionary<FactionId, int> { [new("missing")] = 1 } }];
        yield return [new AuthorSetup { Stats = [new(Guide, new StatId("missing"), 1)] }];
        yield return [new AuthorSetup { Stats = [new(new EntityId("missing"), Strength, 1)] }];
        yield return [new AuthorSetup { Stats = [new(Guide, Strength, 1), new(Guide, Strength, 2)] }];
        yield return [new AuthorSetup { Inventory = [new(Guide, new ItemId("missing"), 1)] }];
        yield return [new AuthorSetup { Inventory = [new(new EntityId("missing"), Rope, 1)] }];
        yield return [new AuthorSetup { Inventory = [new(Guide, Rope, -1)] }];
        yield return [new AuthorSetup { Inventory = [new(Guide, Rope, 1), new(Guide, Rope, 2)] }];
    }

    [Theory]
    [MemberData(nameof(InvalidSetup))]
    public void Invalid_setup_preserves_the_active_session(AuthorSetup setup)
    {
        var story = Compile("=== start\n* [Wait] -> start");
        var session = Start(story);
        var frame = session.Current;
        var events = session.Events;
        var checkpoint = session.Checkpoint;
        Assert.Throws<ArgumentException>(() => session.JumpFresh(new SceneId("start"), setup));
        Assert.Same(frame, session.Current);
        Assert.Same(events, session.Events);
        Assert.Same(checkpoint, session.Checkpoint);
    }

    [Fact]
    public void Failed_target_choice_or_runtime_entry_preserves_the_active_run()
    {
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Explode",
                _ => throw new InvalidOperationException("entry failed")),
        };
        var story = Compile("""
            === start
            * [Bad] { var:count += 1 } -> bad
            === bad
            ~ var:count += 100
            {if @Explode}Never.{/if}
            -> END
            """, options: options);
        var session = Start(story);
        var checkpoint = session.Checkpoint;
        var events = session.Events;
        var frame = session.Current;
        Assert.Throws<KeyNotFoundException>(() => session.JumpFresh(new SceneId("missing")));
        Assert.Throws<ArgumentException>(() => session.JumpFresh(new SceneId("start"), character: new CharacterSelection()));
        Assert.Throws<InvalidOperationException>(() => session.Choose("invalid"));
        Assert.Equal("entry failed", Assert.Throws<InvalidOperationException>(() => session.Choose("c0")).Message);
        Assert.Equal("entry failed", Assert.Throws<InvalidOperationException>(() => session.JumpFresh(new SceneId("bad"))).Message);
        Assert.Same(events, session.Events);
        Assert.Same(frame, session.Current);
        Assert.Same(checkpoint, session.Checkpoint);
        Assert.Equal(0, session.State.GetVariable(Count));
        Assert.Equal(events, checkpoint!.Reenter().Events);
    }

    [Fact]
    public void Failed_trigger_cascade_preserves_the_active_candidate()
    {
        var triggers = string.Join("\n", Enumerable.Range(0, 40).Reverse().Select(i => $"""
              - id: chain-{i}
                when: flag:chain-{i}
                do: set flag:chain-{i + 1}
                once: true
            """));
        var story = Compile("""
            === start
            * [Enter] -> bad
            === bad
            ~ set flag:chain-0
            -> END
            """, Yaml + "\ntriggers:\n" + triggers);
        var session = Start(story);
        var events = session.Events;
        var checkpoint = session.Checkpoint;
        Assert.Contains("did not settle", Assert.Throws<InvalidOperationException>(
            () => session.JumpFresh(new SceneId("bad"))).Message);
        Assert.Contains("did not settle", Assert.Throws<InvalidOperationException>(() => session.Choose("c0")).Message);
        Assert.Same(events, session.Events);
        Assert.Same(checkpoint, session.Checkpoint);
        Assert.Empty(session.Events.OfType<TriggerFired>());
    }

    [Fact]
    public void Fresh_jump_discards_old_progress_and_does_not_inherit_setup_or_character()
    {
        var story = Compile("""
            === start
            * [Next] { var:count += 1 } -> target
            === target
            * [End] -> END
            """);
        var session = Start(story, new AuthorSetup { Variables = new Dictionary<VarId, int> { [Count] = 5 } });
        session.Choose("c0");
        Assert.Equal(6, session.State.GetVariable(Count));
        session.JumpFresh(new SceneId("target"), seed: 7);
        Assert.Equal(7UL, session.Seed);
        Assert.Equal(0, session.State.GetVariable(Count));
        Assert.Equal(0, session.State.VisitCount(new SceneId("start")));
        Assert.Empty(session.Events.OfType<ChoiceMade>());
        session.Choose("c0");
        Assert.True(session.State.IsEnded);
        Assert.Null(session.Checkpoint);
    }

    [Fact]
    public void Normal_start_and_author_start_without_setup_have_equal_events_and_dice()
    {
        var story = Compile("""
            === start
            ~ var:count += 1
            ::: skill-check athletics dc 12
            success: -> finish
            failure: -> finish
            :::
            === finish
            -> END
            """);
        var normal = new StoryHistory(story, 42);
        var author = Start(story);
        Assert.Equal(normal.Events, author.Events);
        Assert.Equal(normal.Current.Document.ToPlainText(), author.Current.Document.ToPlainText());
        author.Choose(StorySession.ContinueKey);
        normal.Choose(StorySession.ContinueKey);
        Assert.Equal(normal.Events, author.Events);
        var restored = StoryHistory.Restore(story, normal.Save());
        Assert.Equal(normal.Events, restored.Events);
    }
}
