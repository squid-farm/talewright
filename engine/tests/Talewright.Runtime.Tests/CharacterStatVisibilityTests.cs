using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Expressions;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;

namespace Talewright.Runtime.Tests;

public sealed class CharacterStatVisibilityTests
{
    [Fact]
    public void Creation_visibility_uses_initial_state_before_presets_and_keeps_hidden_preset_values()
    {
        var story = Compile("""
            game:
              id: creation-visibility
              title: Creation visibility
              start: beginning
              ruleset: rpg
              character-sheet:
                stats:
                  wis: player:str >= 10
              creation:
                stats:
                  str: [8, 12]
                  wis: [6, 8]
                presets:
                  scholar:
                    stats: { str: 12, wis: 8 }
            characters:
              player:
                first-name: Ada
                stats: { str: 8 }
            """, "=== beginning\nHello.\n-> END\n");

        var creation = story.GetCreationOptions()!;
        Assert.DoesNotContain(new StatId("wis"), creation.Stats.Keys);
        Assert.Contains(new StatId("str"), creation.Stats.Keys);
        var selection = creation.Default(story.World.Characters[EntityId.Player], "scholar");
        Assert.DoesNotContain("wis", selection.Stats.Keys);

        var session = new StorySession(story, 42, character: selection);
        Assert.Equal(12, session.State.GetBaseStat(EntityId.Player, new StatId("str")));
        Assert.Equal(8, session.State.GetBaseStat(EntityId.Player, new StatId("wis")));
        Assert.True(story.IsStatVisible(new StatId("wis"), session.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Predicate_visibility_is_evaluated_when_creation_options_are_requested_not_when_compiling(bool ready)
    {
        var calls = 0;
        var options = new CompilerOptions
        {
            CompatibilityId = "creation-predicate-test-v1",
            Predicates = new NamedPredicateRegistry().Register("Ready", state =>
            {
                calls++;
                return state.GetFlag(new FlagId("ready"));
            }),
        };
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: creation-predicate
                  title: Creation predicate
                  start: beginning
                  ruleset: rpg
                  character-sheet:
                    stats:
                      str: "@Ready"
                  creation:
                    stats:
                      str: [8, 12]
                flags: { ready: false }
                characters:
                  player: { first-name: Ada }
                """.Replace("flags: { ready: false }", $"flags: {{ ready: {ready.ToString().ToLowerInvariant()} }}"),
            ["main.tale"] = "=== beginning\nHello.\n-> END\n",
        }), options);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(0, calls);
        var story = result.Story!;
        Assert.Contains(new StatId("str"), story.Creation!.Stats.Keys);
        var creation = story.GetCreationOptions()!;
        Assert.Equal(ready, creation.Stats.ContainsKey(new StatId("str")));
        Assert.True(calls > 0);
        var selection = creation.Default(story.World.Characters[EntityId.Player]);
        var history = new StoryHistory(story, 42, selection);
        Assert.Equal(ready, history.Character!.Stats.ContainsKey("str"));
        Assert.Equal(history.Events, StoryHistory.Restore(story, history.Save()).Events);
        Assert.Throws<ArgumentException>(() => new StoryHistory(story, 42, selection with
        {
            Stats = ready ? new Dictionary<string, int>() : new Dictionary<string, int> { ["str"] = 8 },
        }));
    }

    [Fact]
    public void Hidden_check_stat_change_and_level_notes_use_each_events_pre_event_visibility()
    {
        var story = Compile("""
            game:
              id: event-visibility
              title: Event visibility
              start: beginning
              randomness: off
              ruleset: rpg
              character-sheet:
                stats:
                  str: flag:known
                  stealth: flag:checked
                  xp: flag:known
                  level: flag:known
            flags: { checked: false, known: false }
            characters:
              player:
                first-name: Ada
                stats: { str: 10, stealth: 2 }
            """, """
            === beginning
            ::: skill-check stealth dc 1
            success: { set flag:checked }
            failure: { set flag:checked }
            :::
            * [Continue] -> reveal
            === reveal
            ~ player:str += 1
            ~ player:xp += 150
            ~ set flag:known
            * [Continue] -> next
            === next
            Done.
            -> END
            """);
        Assert.False(story.IsStatVisible(new StatId("stealth"), GameState.CreateInitial(story.World, 42)));
        var history = new StoryHistory(story, 42);
        var checkFrame = history.Current;

        Assert.True(story.IsStatVisible(new StatId("stealth"), history.State));
        Assert.DoesNotContain("Stealth", checkFrame.Document.ToPlainText());
        history.Choose(Assert.Single(history.Current.Choices).Key);
        var frame = history.Current;

        var eventSummary = string.Join(", ", history.Events.Select(gameEvent => gameEvent.ToString()));
        Assert.True(history.Events.Any(gameEvent => gameEvent is SkillCheckResolved { Skill.Value: "stealth" }), eventSummary);
        Assert.True(history.Events.Any(gameEvent => gameEvent is StatChanged { Stat.Value: "str" }), eventSummary);
        Assert.True(history.Events.Any(gameEvent => gameEvent is LevelChanged { NewLevel: 2 }), eventSummary);
        Assert.DoesNotContain("Strength", frame.Document.ToPlainText());
        Assert.DoesNotContain("Experience", frame.Document.ToPlainText());
        Assert.DoesNotContain("Level", frame.Document.ToPlainText());
        Assert.True(story.IsStatVisible(new StatId("str"), history.State));

        history.Choose(Assert.Single(history.Current.Choices).Key);
        Assert.True(history.Current.IsEnded);
        var restored = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        Assert.True(story.IsStatVisible(new StatId("str"), restored.State));
        restored.Back();
        Assert.True(story.IsStatVisible(new StatId("str"), restored.State));
        restored.Back();
        Assert.False(story.IsStatVisible(new StatId("str"), restored.State));
        Assert.True(story.IsStatVisible(new StatId("stealth"), restored.State));
        Assert.True(restored.CanForward);
        restored.Forward();
        Assert.True(story.IsStatVisible(new StatId("str"), restored.State));
    }

    [Fact]
    public void Author_inspection_and_playtester_results_omit_hidden_stat_disclosures()
    {
        var story = Compile("""
            game:
              id: inspect-visibility
              title: Inspect visibility
              start: beginning
              ruleset: rpg
              character-sheet:
                stats:
                  str: false
            characters:
              player:
                first-name: Ada
            """, """
            === beginning
            * [Change] { player:str += 1 } -> ending
            === ending
            Done.
            -> END
            """);
        var author = AuthorSession.Create(story, new SceneId("beginning"));
        author.Choose("c0");

        var inspection = author.Inspect();
        Assert.DoesNotContain(inspection.Characters.Single(character => character.Id == EntityId.Player).Stats,
            stat => stat.Id == new StatId("str"));
        Assert.DoesNotContain(inspection.Events, gameEvent => gameEvent is StatChanged { Stat.Value: "str" });
        Assert.DoesNotContain(author.Results.Single().Events,
            gameEvent => gameEvent is StatChanged { Stat.Value: "str" });
        var hiddenStatOffset = author.Events.ToList().FindIndex(gameEvent =>
            gameEvent is StatChanged { Stat.Value: "str" });
        Assert.True(hiddenStatOffset >= 0);
        Assert.DoesNotContain(inspection.EventEntries, entry => entry.Offset == hiddenStatOffset);
        Assert.Contains(inspection.EventEntries, entry =>
            entry.Offset > hiddenStatOffset && entry.Event is SceneEntered);
    }

    [Fact]
    public void Creation_with_no_visible_stats_has_an_empty_selectable_stats_set()
    {
        var story = Compile("""
            game:
              id: empty-creation
              title: Empty creation
              start: beginning
              ruleset: rpg
              character-sheet:
                stats:
                  str: false
              creation:
                stats:
                  str: [8, 12]
            characters:
              player:
                first-name: Ada
            """, "=== beginning\nHello.\n-> END\n");

        var creation = story.GetCreationOptions()!;
        Assert.Empty(creation.Stats);
        Assert.Empty(creation.Default(story.World.Characters[EntityId.Player]).Stats);
        var session = new StorySession(story, 42, character: creation.Default(story.World.Characters[EntityId.Player]));
        Assert.Equal(new SceneId("beginning"), session.Start().Scene);
    }

    private static Talewright.Talemark.Model.StoryDefinition Compile(string yaml, string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
