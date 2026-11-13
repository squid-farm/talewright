using System.Security.Cryptography;
using System.Text;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Rulesets.Generic;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Tests;

/// <summary>
/// Player saves store hashes of these inputs. A failure here means saved evidence would change; keep the old encoding
/// or make an explicit, documented version decision instead of updating the expected value.
/// </summary>
public sealed class ReplayEvidenceEncodingTests
{
    private static readonly EntityId Player = new("player");

    public static TheoryData<string, GameEvent, string> Events => new()
    {
        { "identity", new CharacterIdentityChanged(Player, "Ada", "Burns", Pronouns.They),
            """["CharacterIdentityChanged",{"character":{"value":"player"},"firstName":"Ada","lastName":"Burns","pronouns":{"subject":"they","object":"them","possessive":"their","possessivePronoun":"theirs","reflexive":"themselves","agreement":1}}]""" },
        { "stat", new StatChanged(Player, new StatId("hp"), 10, 7),
            """["StatChanged",{"character":{"value":"player"},"stat":{"value":"hp"},"oldValue":10,"newValue":7}]""" },
        { "granted", new ItemsGranted(Player, new ItemId("rope"), 2),
            """["ItemsGranted",{"character":{"value":"player"},"item":{"value":"rope"},"quantity":2}]""" },
        { "removed", new ItemsRemoved(Player, new ItemId("rope"), 1),
            """["ItemsRemoved",{"character":{"value":"player"},"item":{"value":"rope"},"quantity":1}]""" },
        { "standing", new StandingChanged(new FactionId("guild"), 0, 5),
            """["StandingChanged",{"faction":{"value":"guild"},"oldValue":0,"newValue":5}]""" },
        { "flag", new FlagChanged(new FlagId("door-open"), true),
            """["FlagChanged",{"flag":{"value":"door-open"},"value":true}]""" },
        { "variable", new VariableChanged(new VarId("gold"), 10, 8),
            """["VariableChanged",{"variable":{"value":"gold"},"oldValue":10,"newValue":8}]""" },
        { "text variable", new TextVariableChanged(new VarId("nickname"), "", "Joel \"big\" <&>"),
            """["TextVariableChanged",{"variable":{"value":"nickname"},"oldValue":"","newValue":"Joel \u0022big\u0022 \u003C\u0026\u003E"}]""" },
        { "scene", new SceneEntered(new SceneId("cave")),
            """["SceneEntered",{"scene":{"value":"cave"}}]""" },
        { "choice", new ChoiceMade(new SceneId("cave"), "c0"),
            """["ChoiceMade",{"scene":{"value":"cave"},"choiceKey":"c0"}]""" },
        { "dice", new DiceRolled(20, 17),
            """["DiceRolled",{"sides":20,"result":17}]""" },
        { "check", new SkillCheckResolved(Player, new StatId("athletics"),
            new(new SceneId("cave"), Player, new StatId("athletics"), 0), new("cave"), 1, 12, 17, 3, 20, true),
            """["SkillCheckResolved",{"actor":{"value":"player"},"skill":{"value":"athletics"},"identity":{"scene":{"value":"cave"},"actor":{"value":"player"},"skill":{"value":"athletics"},"occurrence":0},"executionScene":{"value":"cave"},"visit":1,"difficulty":12,"roll":17,"modifier":3,"total":20,"success":true}]""" },
        { "check without difficulty", new SkillCheckResolved(Player, new StatId("athletics"),
            new(new SceneId("cave"), Player, new StatId("athletics"), 0), new("cave"), 1, null, 17, 3, 20, null),
            """["SkillCheckResolved",{"actor":{"value":"player"},"skill":{"value":"athletics"},"identity":{"scene":{"value":"cave"},"actor":{"value":"player"},"skill":{"value":"athletics"},"occurrence":0},"executionScene":{"value":"cave"},"visit":1,"roll":17,"modifier":3,"total":20}]""" },
        { "level", new LevelChanged(Player, 1, 2),
            """["LevelChanged",{"character":{"value":"player"},"oldLevel":1,"newLevel":2}]""" },
        { "trigger", new TriggerFired("outcast"),
            """["TriggerFired",{"triggerId":"outcast"}]""" },
        { "end", new StoryEnded(),
            """["StoryEnded",{}]""" },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Event_inputs_are_frozen(string name, GameEvent gameEvent, string expected)
    {
        Assert.NotEmpty(name);
        Assert.Equal($"[{expected}]", Encoding.UTF8.GetString(ReplayEvidence.EventInput(gameEvent)));
    }

    [Fact]
    public void Every_game_event_kind_has_a_frozen_input()
    {
        var kinds = typeof(GameEvent).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false } && typeof(GameEvent).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .ToHashSet();
        Assert.Equal(kinds, Events.Select(row => row[1].GetType().FullName!).ToHashSet());
    }

    [Fact]
    public void Initial_state_and_rule_configuration_input_is_frozen()
    {
        var story = RichStory();
        var state = new StorySession(story, 42).State;
        var input = ReplayEvidence.InitialStateInput(story, state);

        Assert.Equal("""
            ["initial-state-v1","ChecksOnly","rpg",[["acrobatics",0,10,0,"dex"],["animal-handling",0,10,0,"wis"],
            ["arcana",0,10,0,"int"],["athletics",0,10,0,"str"],["cha",1,30,10,null],["con",1,30,10,null],["deception",
            0,10,0,"cha"],["dex",1,30,10,null],["history",0,10,0,"int"],["hp",0,999,10,null],["insight",0,10,0,"wis"],
            ["int",1,30,10,null],["intimidation",0,10,0,"cha"],["investigation",0,10,0,"int"],["level",1,20,1,null],
            ["medicine",0,10,0,"wis"],["nature",0,10,0,"int"],["perception",0,10,0,"wis"],["performance",0,10,0,
            "cha"],["persuasion",0,10,0,"cha"],["religion",0,10,0,"int"],["sleight-of-hand",0,10,0,"dex"],["stealth",
            0,10,0,"dex"],["str",1,30,10,null],["survival",0,10,0,"wis"],["wis",1,30,10,null],["xp",0,2147483647,
            0,null]],[["hostile",-2147483648],["unfriendly",-30],["neutral",-9],["friendly",10],["allied",50]],"Talewright.Rulesets.Generic.D20SkillCheckRule",
            20,"Talewright.Rulesets.Generic.ExperienceLevelingRule","level",[],[["player",["acrobatics",0,0],["animal-handling",
            0,0],["arcana",0,0],["athletics",2,2],["cha",10,10],["con",10,10],["deception",0,0],["dex",14,14],["history",
            0,0],["hp",10,10],["insight",0,0],["int",10,10],["intimidation",0,0],["investigation",0,0],["level",
            1,1],["medicine",0,0],["nature",0,0],["perception",0,0],["performance",0,0],["persuasion",0,0],["religion",
            0,0],["sleight-of-hand",0,0],["stealth",0,0],["str",10,10],["survival",0,0],["wis",10,10],["xp",0,0],
            [["relic",0],["rope",1]]]],[["relic",["wis",2]],["rope"]],[["guild",3]],[["ready",false]],[["gold",10]],
            [["nickname",""]]]
            """.ReplaceLineEndings(""), Encoding.UTF8.GetString(input));
        Assert.Equal(SHA256.HashData(input), ReplayEvidence.InitialState(story, state));
    }

    [Fact]
    public void Randomness_and_rule_tokens_are_frozen()
    {
        Assert.Equal(["Off", "ChecksOnly"], Enum.GetValues<RandomnessMode>().Select(ReplayEvidence.Token));
        Assert.Equal("Talewright.Rulesets.Generic.D20SkillCheckRule", ReplayEvidence.RuleToken(new D20SkillCheckRule()));
        Assert.Equal("Talewright.Rulesets.Generic.ExperienceLevelingRule",
            ReplayEvidence.RuleToken(new ExperienceLevelingRule()));
        Assert.Null(ReplayEvidence.RuleToken(null));
        Assert.Equal(typeof(ExtensionModifier).FullName, ReplayEvidence.RuleToken(new ExtensionModifier()));
    }

    [Fact]
    public void Complete_timeline_with_checks_triggers_and_redo_has_frozen_evidence()
    {
        var history = RichTimeline(RichStory());

        Assert.Equal(1, history.Cursor);
        Assert.Contains(history.Events, gameEvent => gameEvent is SkillCheckResolved);
        Assert.Contains(history.Events, gameEvent => gameEvent is TriggerFired);
        var evidence = Convert.ToHexString(SaveFormat.Read(history.Save()).ReplayEvidence!);
        Assert.Equal("04F104ACB83649F68362EBE7BC87964D8B2F04EA638174F2FEB728FBA2DCB571AA", evidence);
    }

    private static StoryDefinition RichStory()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: evidence, title: Evidence, start: start, seed: 42, ruleset: rpg }
                characters:
                  player:
                    first-name: Mara
                    last-name: Burns
                    pronouns: she
                    stats: { dex: 14, athletics: 2 }
                    inventory: [rope]
                factions:
                  guild: { name: Guild, standing: 3 }
                items:
                  rope: { name: rope }
                  relic: { name: relic, modifiers: { wis: 2 } }
                vars: { gold: 10 }
                flags: [ready]
                triggers:
                  - id: rich
                    when: var:gold >= 12
                    do: set flag:ready
                    once: true
                """,
            ["main.tale"] = """
                === start
                ~ var:nickname = "Ace"
                * [Climb] skill-check athletics dc 12
                  success: { var:gold += 5; give player relic } -> top
                  failure: { player:hp -= 2; faction:guild:standing -= 1 } -> top
                * [Wait] -> top
                === top
                * [Rest] -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static StoryHistory RichTimeline(StoryDefinition story)
    {
        var history = new StoryHistory(story, 42);
        history.Choose(history.Current.Choices[0].Key);
        history.Choose(history.Current.Choices[0].Key);
        history.Back();
        history.Back();
        history.Forward();
        return history;
    }

    private sealed class ExtensionModifier : IStatModifier
    {
        public int GetModifier(IGameStateView state, EntityId character, StatId stat) => 0;
    }
}
