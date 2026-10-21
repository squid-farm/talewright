using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;

namespace Talewright.Tale.Tests;

public class CompilerTests
{
    private const string Yaml = """
        id: test
        title: Test Story
        version: 1.0.0
        start: a
        seed: 42
        characters:
          player:
            first-name: Mara
            last-name: Burns
            pronouns: she
            stats: { dex: 14, athletics: 2 }
            inventory: [rope]
          guards:
            first-name: the guards
            pronouns: they
            plural: true
        factions:
          guild: { name: "Explorers' Guild", standing: 0 }
        items:
          rope: { name: coil of rope }
          relic: { name: Relic of Ysh, modifiers: { wis: 2 } }
        vars: { torches: 1 }
        triggers:
          - id: outcast
            when: faction:guild <= hostile
            do: set flag:outcast
            once: true
        """;

    private static CompilationResult Compile(string tale, string yaml = Yaml, CompilerOptions? options = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = yaml,
            ["main.tale"] = tale,
        }), options);

    private static StoryDefinition Ok(string tale, string yaml = Yaml)
    {
        var result = Compile(tale, yaml);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static Diagnostic Error(string tale, string code, string yaml = Yaml)
    {
        var result = Compile(tale, yaml);
        Assert.False(result.Success);
        var error = result.Errors.FirstOrDefault(d => d.Code == code);
        Assert.True(error is not null, $"Expected {code} but got:\n{string.Join("\n", result.Diagnostics)}");
        return error;
    }

    private static string Render(StoryDefinition story, SceneDefinition scene, GameState? state = null) =>
        new TemplateRenderer().Render(scene.Body, state ?? GameState.CreateInitial(story.World, 1)).ToPlainText();

    [Fact]
    public void Compiles_yaml_metadata_world_and_triggers()
    {
        var story = Ok("=== a\nHello.\n-> END");

        Assert.Equal("test", story.Id);
        Assert.Equal("Test Story", story.Title);
        Assert.Equal(new SceneId("a"), story.Start);
        Assert.Equal(42UL, story.Seed);
        Assert.Equal(RandomnessMode.ChecksOnly, story.Randomness);
        var player = story.World.Characters[EntityId.Player];
        Assert.Equal("Mara Burns", player.FullName);
        Assert.Equal(14, player.Stats[new StatId("dex")]);
        Assert.Equal(1, player.Inventory[new ItemId("rope")]);
        Assert.True(story.World.Characters[new EntityId("guards")].IsPluralName);
        Assert.Equal(2, story.World.Items[new ItemId("relic")].Modifiers[new StatId("wis")]);

        // The trigger's "set flag:outcast" auto-declares the flag.
        var trigger = Assert.Single(story.Triggers);
        Assert.True(trigger.Once);
        Assert.Contains(new FlagId("outcast"), story.World.Flags);
        Assert.IsType<BinaryExpr>(trigger.When);
    }

    [Fact]
    public void Parses_scene_metadata()
    {
        var story = Ok("""
            === a
            @title The Camp
            @tags camp, start
            @priority 5
            @once
            @hub camp
            @requires player:dex >= 12
            Text.
            -> END
            """);

        var scene = story.GetScene(new SceneId("a"));
        Assert.Equal("The Camp", scene.Title);
        Assert.Equal(["camp", "start"], scene.Tags.Order());
        Assert.Equal(5, scene.Priority);
        Assert.True(scene.Once);
        Assert.Equal("camp", scene.HubTag);
        Assert.Equal(new BinaryExpr(BinaryOp.GreaterOrEqual, new StatRef(EntityId.Player, new StatId("dex")), new NumberLiteral(12)), scene.Requires);
    }

    [Fact]
    public void Renders_markdown_substitutions_and_grammar()
    {
        var story = Ok("""
            === a
            {player:first-name#possessive} torch gutters. **{player:last-name#possessive}** *map* is `old`.
            {guards:first-name#possessive#capitalize} post. {player:pronoun.subject#capitalize} carries {item:rope#a}.

            > guards: Halt, {player:name}!
            -> END
            """);

        var text = Render(story, story.GetScene(new SceneId("a")));
        Assert.Equal(
            "Mara's torch gutters. Burns's map is old. The guards' post. She carries a coil of rope.\n\nthe guards: Halt, Mara Burns!",
            text);

        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("a")).Body[0]);
        Assert.Contains(paragraph.Inlines, i => i is StyledTemplate { Style: TextStyle.Strong });
        Assert.Contains(paragraph.Inlines, i => i is StyledTemplate { Style: TextStyle.Emphasis });
        Assert.Contains(paragraph.Inlines, i => i is StyledTemplate { Style: TextStyle.Code });
    }

    [Fact]
    public void Media_placeholders_are_kept_for_future_presentation()
    {
        var story = Ok("=== a\n![The sealed door](door-art)\n-> END");

        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("a")).Body[0]);
        Assert.Equal(new MediaTemplate("door-art", "The sealed door"), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void Inline_and_block_conditionals_render_by_state()
    {
        var story = Ok("""
            === a
            You {if player has rope}carry a rope{else}carry nothing{/if}.

            {if faction:guild >= friendly}
            The guild waves.
            {else}
            The guild ignores you.
            {/if}
            -> END
            """);
        var scene = story.GetScene(new SceneId("a"));
        var state = GameState.CreateInitial(story.World, 1);

        Assert.Equal("You carry a rope.\n\nThe guild ignores you.", Render(story, scene, state));

        state.Apply(new ItemsRemoved(EntityId.Player, new ItemId("rope"), 1));
        state.Apply(new StandingChanged(new FactionId("guild"), 0, 20));
        Assert.Equal("You carry nothing.\n\nThe guild waves.", Render(story, scene, state));
    }

    [Fact]
    public void Parses_choices_with_requires_effects_and_diverts()
    {
        var story = Ok("""
            === a
            Pick.
            * [Read the glyphs] (requires: player:int >= 12) -> b
            * [Shove {player:first-name}] { faction:guild -= 5; give player relic; set flag:shoved } -> b
            * [Leave] -> END
            === b
            {if flag:shoved}Shoved.{/if}
            -> END
            """);

        var choices = story.GetScene(new SceneId("a")).Choices;
        Assert.Equal(["c0", "c1", "c2"], choices.Select(c => c.Key));
        Assert.NotNull(choices[0].Requires);
        Assert.Equal(new SceneId("b"), choices[0].Divert!.Scene);
        Assert.Equal(3, choices[1].Effects.Count);
        Assert.Equal(new ChangeStanding(new FactionId("guild"), AssignOp.Subtract, new NumberLiteral(5)), choices[1].Effects[0]);
        Assert.IsType<SubstitutionTemplate>(choices[1].Label[1]);
        Assert.True(choices[2].Divert!.IsEnd);
    }

    [Fact]
    public void Choices_inside_block_if_inherit_the_condition()
    {
        var story = Ok("""
            === a
            {if player has rope}
            * [Climb down] -> END
            {else}
            * [Jump] -> END
            {/if}
            """);

        var choices = story.GetScene(new SceneId("a")).Choices;
        Assert.Equal(new HasItem(EntityId.Player, new ItemId("rope")), choices[0].Requires);
        Assert.Equal(new UnaryExpr(UnaryOp.Not, new HasItem(EntityId.Player, new ItemId("rope"))), choices[1].Requires);
    }

    [Fact]
    public void Parses_entry_effects_and_conditional_diverts()
    {
        var story = Ok("""
            === a
            ~ var:torches += 1
            ~ player:hp -= 2; set flag:burned
            {if flag:burned}
            -> b
            {/if}
            -> END
            === b
            -> END
            """);

        var scene = story.GetScene(new SceneId("a"));
        Assert.Equal(3, scene.EntryEffects.Count);
        Assert.All(scene.EntryEffects, e => Assert.Null(e.Condition));
        Assert.Equal(new FlagRef(new FlagId("burned")), scene.Diverts[0].Condition);
        Assert.True(scene.Diverts[1].Target.IsEnd);
    }

    [Fact]
    public void Parses_choice_and_block_skill_checks()
    {
        var story = Ok("""
            === a
            * [Climb] skill-check athletics dc 14
              success: -> b
              failure: { player:hp -= 2 } -> b
            * [Ask the guards] skill-check guards perception dc 10 + 2
              success: -> b
              failure: -> END
            === b
            ::: skill-check perception dc 12
            success: -> END
            // comments are fine here
            failure: { set flag:missed }
            :::
            -> END
            """);

        var climb = story.GetScene(new SceneId("a")).Choices[0].Check!;
        Assert.Equal(new SkillCheck(EntityId.Player, new StatId("athletics"), new NumberLiteral(14)), climb.Check);
        Assert.Single(climb.Failure.Effects);
        var ask = story.GetScene(new SceneId("a")).Choices[1].Check!;
        Assert.Equal(new EntityId("guards"), ask.Check.Actor);
        Assert.IsType<BinaryExpr>(ask.Check.Difficulty);

        var block = story.GetScene(new SceneId("b")).EntryCheck!;
        Assert.True(block.Success.Divert!.IsEnd);
        Assert.Null(block.Failure.Divert);
    }

    [Fact]
    public void Reports_type_errors_with_locations()
    {
        var error = Error("=== a\n* [x] (requires: player:dex + 1) -> END", "TW2003");
        Assert.Equal(new SourceLocation("main.tale", 2, 18), error.Location);

        Error("=== a\n{if player:first-name > 3}x{/if}\n-> END", "TW2003");
        Error("=== a\n~ var:torches = \"three\"\n-> END", "TW2003");
    }

    [Fact]
    public void Reports_unknown_references()
    {
        Assert.Contains("Unknown property 'strenght'", Error("=== a\n{player:strenght}\n-> END", "TW2001").Message);
        Error("=== a\n{faction:nobody}\n-> END", "TW2001");
        Error("=== a\n{if player has sword}x{/if}\n-> END", "TW2001");
        Error("=== a\n> ghost: boo\n-> END", "TW2001");
        Error("=== a\n-> nowhere", "TW2002");
        Error("=== a\n{if flag:never-set}x{/if}\n-> END", "TW2004");
        Error("=== a\n{if @Lucky}x{/if}\n-> END", "TW2005");
        Error("=== a\n{player:name#shout}\n-> END", "TW2006");
    }

    [Fact]
    public void Reports_structural_errors_and_warnings()
    {
        Error("=== a\nJust text.", "TW3002");
        Error("=== a\n* [Wait]", "TW3003");
        Error("=== a\n-> END\n=== a\n-> END", "TW3010");
        Error("=== a\n{if true}\nx\n-> END", "TW1005");
        Error("=== b\n-> END", "TW3007");

        var result = Compile("=== a\n@hub nothing\n-> END\n=== orphan\n-> END");
        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Code == "TW3008");
        Assert.Contains(result.Warnings, w => w.Code == "TW3009" && w.Message.Contains("orphan"));
    }

    [Fact]
    public void Broken_diverts_do_not_cascade_into_unreachable_warnings()
    {
        var result = Compile("=== a\n-> typo\n=== b\n-> c\n=== c\n-> END");

        Assert.Contains(result.Errors, e => e.Code == "TW2002");
        Assert.DoesNotContain(result.Warnings, w => w.Code == "TW3009");
    }

    [Fact]
    public void Inline_conditionals_cannot_cross_formatting()
    {
        Error("=== a\n*{if true}a*{/if}\n-> END", "TW1005");
    }

    [Fact]
    public void Ruleset_can_be_customised_or_removed()
    {
        const string DatingSim = """
            id: dating
            title: Dating Sim
            start: a
            ruleset:
              base: none
              stats:
                - { id: charm, name: Charm, category: attribute, min: 0, max: 10, default: 3 }
            characters:
              player: { first-name: Sam, pronouns: they, stats: { charm: 5 } }
            """;
        var story = Ok("=== a\n{if player:charm >= 5}Smooth.{/if}\n-> END", DatingSim);
        Assert.Equal("none", story.World.Ruleset.Id);
        Assert.False(story.World.Ruleset.HasStat(new StatId("str")));

        // No check rule in "none", and unknown generic stats are rejected.
        Error("=== a\n* [x] skill-check charm dc 5\n  success: -> END\n  failure: -> END", "TW3005", DatingSim);
        Error("=== a\n{player:str}\n-> END", "TW2001", DatingSim);

        const string NoCharisma = """
            id: t
            title: T
            start: a
            ruleset: { base: generic, disable: [cha] }
            characters:
              player: { first-name: Ann, pronouns: she }
            """;
        Error("=== a\n{player:cha}\n-> END", "TW2001", NoCharisma);
    }

    [Fact]
    public void Reports_yaml_errors()
    {
        Assert.False(Compile("=== a\n-> END", "id: x\ntitle: [").Success);
        Error("=== a\n-> END", "TW4002", "id: x\ntitle: X\nstart: a\n");
        Error("=== a\n-> END", "TW4005", Yaml.Replace("inventory: [rope]", "inventory: [sword]"));
        Error("=== a\n-> END", "TW4004", Yaml.Replace("seed: 42", "ruleset: { base: gurps }"));
        Error("=== a\n-> END", "TW4003", Yaml.Replace("pronouns: she", "pronouns: ze"));
    }

    [Fact]
    public void Rejects_custom_stats_with_minimum_greater_than_maximum()
    {
        const string yaml = """
            id: test
            title: Test
            start: a
            ruleset:
              base: none
              stats:
                - { id: love, min: 10, max: 0 }
            characters:
              player: { first-name: Mara, pronouns: she }
            """;

        var error = Error("=== a\n-> END", "TW4003", yaml);

        Assert.Contains("minimum (10) must not be greater than its maximum (0)", error.Message);
        Assert.Equal("story.yaml", error.Location.File);
    }

    [Fact]
    public void Custom_pronouns_and_named_predicates()
    {
        var yaml = Yaml.Replace("pronouns: she", "pronouns: { subject: xe, object: xem, possessive: xyr, possessive-pronoun: xyrs, reflexive: xemself }");
        var options = new CompilerOptions { Predicates = new NamedPredicateRegistry().Register("Lucky", _ => true) };
        var result = Compile("=== a\n{if @Lucky}{player:pronoun.subject#capitalize} wins.{/if}\n-> END", yaml, options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal("Xe wins.", new TemplateRenderer(new Narrative.Grammar.EnglishGrammar(), options.Predicates)
            .Render(story.GetScene(new SceneId("a")).Body, GameState.CreateInitial(story.World, 1)).ToPlainText());
    }

    [Fact]
    public void Loads_stories_from_zip_with_a_top_level_folder()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }

            Add("my-story/story.yaml", Yaml);
            Add("my-story/chapters/one.tale", "=== a\n-> END");
        }

        stream.Position = 0;
        var source = StorySource.FromZip(stream);
        Assert.Equal(["chapters/one.tale", "story.yaml"], source.Files.Keys.Order());
        Assert.True(StoryCompiler.Compile(source).Success);
    }
}
