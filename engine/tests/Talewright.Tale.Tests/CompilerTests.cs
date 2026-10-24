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
using System.IO.Compression;

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
            ["story.talewright.yaml"] = yaml,
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

    [Theory]
    [InlineData("// ~ set flag:phantom")]
    [InlineData("The inscription says set flag:phantom.")]
    [InlineData("> guards: set flag:phantom")]
    [InlineData("* [set flag:phantom] -> END")]
    [InlineData("`set flag:phantom`")]
    public void Non_effect_text_does_not_declare_flags(string text)
    {
        var error = Error($"=== a\n{text}\n{{if flag:phantom}}Never.{{/if}}\n-> END", "TW2004");
        Assert.Equal(3, error.Location.Line);
    }

    [Theory]
    [InlineData("// ~ var:phantom = 1")]
    [InlineData("The inscription says var:phantom += 1.")]
    [InlineData("* [var:phantom = 1] -> END")]
    [InlineData("> guards: var:phantom -= 1")]
    public void Non_effect_text_does_not_declare_variables(string text)
    {
        Error($"=== a\n{text}\n{{var:phantom}}\n-> END", "TW2004");
    }

    [Fact]
    public void Discovery_handles_all_effect_positions_and_cross_file_forward_references()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Yaml,
            ["01.tale"] = """
                === a
                {if flag:entry}Entry.{/if}
                {if flag:choice}Choice.{/if}
                {if flag:success}Success.{/if}
                {if flag:failure}Failure.{/if}
                {if flag:block-success}Block.{/if}
                {if flag:block-failure}Block.{/if}
                {var:later}
                -> b
                """,
            ["02.tale"] = """
                === b
                ~ clear flag:entry; var:later = var:other + 1; var:other = 2
                * [Continue] { set flag:choice } skill-check athletics dc 1
                  success: { set flag:success } -> c
                  failure: { clear flag:failure } -> c
                === c
                ::: skill-check athletics dc 1
                  success: { set flag:block-success } -> END
                  failure: { set flag:block-failure } -> END
                :::
                """,
        }));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(7, result.Story!.World.Flags.Count);
        Assert.Contains(new VarId("later"), result.Story.World.Variables.Keys);
        Assert.Contains(new VarId("other"), result.Story.World.Variables.Keys);
    }

    [Fact]
    public void String_contents_inside_effects_do_not_declare_assignments()
    {
        var result = Compile("=== a\n~ var:real = \"set flag:phantom; var:fake = 1\"\n{if flag:phantom}No.{/if}\n{var:fake}\n-> END");
        Assert.False(result.Success);
        Assert.Equal(2, result.Errors.Count(d => d.Code == "TW2004"));
    }

    [Fact]
    public void Renders_markdown_substitutions_and_grammar()
    {
        var story = Ok("""
            === a
            {player:first-name#possessive} torch gutters. **{player:last-name#possessive}** *map* is `old`.
            {guards:first-name#possessive#capitalize} post. {player:pronoun#capitalize} carries {item:rope#a}.

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
    public void Faction_properties_use_colons_and_dots_remain_part_of_names()
    {
        const string yaml = """
            id: dotted-name-test
            title: Dotted names
            start: camp.arrival
            characters: { player: { first-name: Ada } }
            factions:
              guild.name: { name: Guild name }
              guild: { name: Guild }
            """;
        var story = Ok("""
            === camp.arrival
            {faction:guild:name} / {faction:guild:tier} / {faction:guild.name}
            {player:pronoun}
            -> END
            """, yaml);
        Assert.Equal("camp.arrival", story.Scenes.Single().Id.Value);
        Assert.Equal("Guild name", story.World.Factions[new Talewright.Core.Identity.FactionId("guild.name")].Name);
    }

    [Theory]
    [InlineData("{faction:guild.name}", "faction:guild:name")]
    [InlineData("{faction:missing.tier}", "faction:missing:tier")]
    [InlineData("{player:pronoun.subject}", "'player:pronoun'")]
    public void Dotted_property_access_reports_its_colon_replacement(string reference, string replacement)
    {
        var error = Error($"=== a\n{reference}\n-> END", "TW2001");
        Assert.Contains(replacement, error.Message);
    }

    [Theory]
    [InlineData("café")]
    [InlineData("camp..arrival")]
    [InlineData("camp--arrival")]
    [InlineData("-camp")]
    [InlineData("camp-")]
    public void Scene_ids_reject_names_outside_shared_identifier_grammar(string id)
    {
        var result = Compile($"=== {id}\n-> END");
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "TW1008");
        Assert.Contains("ASCII", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 1, 1), error.Location);
    }

    [Fact]
    public void Scene_targets_use_the_same_identifier_grammar_as_declarations()
    {
        var result = Compile("=== a\n-> café");
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "TW1008");
        Assert.Contains("ASCII", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 4), error.Location);
    }

    [Theory]
    [InlineData("guards:", "pläyer:")]
    [InlineData("relic:", "bad..item:")]
    [InlineData("guild:", "guild-:")]
    [InlineData("guards:", "\"guards\\n\":")]
    [InlineData("vars: { torches: 1 }", "vars: {\"bad/id\": 0}")]
    [InlineData("vars: { torches: 1 }", "flags: [bad name]\nvars: { torches: 1 }")]
    public void Manifest_entity_ids_enforce_shared_identifier_grammar(string original, string replacement)
    {
        var yaml = Yaml.Replace(original, replacement);
        var result = Compile("=== a\n-> END", yaml);
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "TW4003");
        Assert.Contains("Invalid", error.Message);
        Assert.Contains("ASCII", error.Message);
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
        Assert.Equal("story.talewright.yaml", error.Location.File);
    }

    [Fact]
    public void Rejects_duplicate_trigger_ids()
    {
        const string yaml = """
            id: test
            title: Test
            start: a
            characters:
              player: { first-name: Mara, pronouns: she }
            triggers:
              - id: duplicate
                when: true
                do: set flag:first
              - id: duplicate
                when: true
                do: set flag:second
            """;

        var error = Error("=== a\n-> END", "TW4007", yaml);

        Assert.Contains("Trigger id 'duplicate' is declared more than once", error.Message);
        Assert.Equal(10, error.Location.Line);
    }

    [Fact]
    public void Custom_pronouns_and_named_predicates()
    {
        var yaml = Yaml.Replace("pronouns: she", "pronouns: { subject: xe, object: xem, possessive-pronoun: { determiner: xyr, independent: xyrs }, reflexive: xemself }");
        var options = new CompilerOptions { Predicates = new NamedPredicateRegistry().Register("Lucky", _ => true) };
        var result = Compile("=== a\n{if @Lucky}{player:pronoun#capitalize} wins.{/if}\n-> END", yaml, options);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal("Xe wins.", new TemplateRenderer(new Narrative.Grammar.EnglishGrammar(), options.Predicates)
            .Render(story.GetScene(new SceneId("a")).Body, GameState.CreateInitial(story.World, 1)).ToPlainText());
    }

    [Theory]
    [InlineData("player", "she/her/her/her/hers/herself/Herself/Her/SHE")]
    [InlineData("guards", "they/them/their/their/theirs/themselves/Themselves/Their/THEY")]
    public void Pronoun_forms_are_chosen_with_hash_modifiers(string who, string expected)
    {
        var story = Ok("""
            === a
            {W:pronoun}/{W:pronoun#object}/{W:pronoun#possessive}/{W:pronoun#possessive("determiner")}/{W:pronoun#possessive("independent")}/{W:pronoun#reflexive}/{W:pronoun#reflexive#capitalize}/{W:pronoun#possessive#capitalize}/{W:pronoun#upper}
            -> END
            """.Replace("W", who));
        Assert.Equal(expected, Render(story, story.GetScene(new SceneId("a"))));
    }

    [Fact]
    public void Custom_pronoun_sets_use_their_declared_forms()
    {
        var yaml = Yaml.Replace("pronouns: she", "pronouns: { subject: xe, object: xem, possessive-pronoun: { determiner: xyr, independent: xyrs }, reflexive: xemself }");
        var result = Compile("""
            === a
            {player:pronoun}/{player:pronoun#object}/{player:pronoun#possessive}/{player:pronoun#possessive("determiner")}/{player:pronoun#possessive("independent")}/{player:pronoun#reflexive#upper}
            -> END
            """, yaml);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("xe/xem/xyr/xyr/xyrs/XEMSELF", Render(result.Story!, result.Story!.GetScene(new SceneId("a"))));
    }

    [Theory]
    [InlineData("possessive: xyr, possessive-pronoun: xyrs", "TW4003", "Use 'possessive-pronoun: { determiner: xer, independent: xers }'")]
    [InlineData("possessive-pronoun: xyrs", "TW4003", "must be a mapping")]
    [InlineData("possessive-pronoun: null", "TW4003", "must be a mapping")]
    [InlineData("possessive-pronoun: []", "TW4003", "must be a mapping")]
    [InlineData("", "TW4002", "Missing required 'possessive-pronoun'")]
    [InlineData("possessive-pronoun: {}", "TW4002", "Missing required 'determiner'")]
    [InlineData("possessive-pronoun: { determiner: xyr }", "TW4002", "Missing required 'independent'")]
    [InlineData("possessive-pronoun: { independent: xyrs }", "TW4002", "Missing required 'determiner'")]
    [InlineData("possessive-pronoun: { determiner: '', independent: xyrs }", "TW4003", "'determiner' must be a single value")]
    [InlineData("possessive-pronoun: { determiner: xyr, independent: [] }", "TW4003", "'independent' must be a single value")]
    public void Invalid_custom_possessive_forms_are_reported(string forms, string code, string message)
    {
        var yaml = Yaml.Replace("pronouns: she", $"pronouns: {{ subject: xe, object: xem, reflexive: xemself{(forms.Length > 0 ? ", " + forms : "")} }}");
        var error = Error("=== a\n-> END", code, yaml);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Unknown_custom_possessive_keys_are_reported()
    {
        var yaml = Yaml.Replace("pronouns: she",
            "pronouns: { subject: xe, object: xem, possessive-pronoun: { determiner: xyr, independent: xyrs, other: xer }, reflexive: xemself }");
        var result = Compile("=== a\n-> END", yaml);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "TW4006");
        Assert.Contains("Unknown key 'other'", warning.Message);
        Assert.Contains("determiner, independent", warning.Message);
    }

    [Theory]
    [InlineData("subject", "player:pronoun")]
    [InlineData("object", "player:pronoun#object")]
    [InlineData("possessive", "player:pronoun#possessive")]
    [InlineData("possessive-pronoun", "player:pronoun#possessive(\"independent\")")]
    [InlineData("reflexive", "player:pronoun#reflexive")]
    public void Legacy_pronoun_forms_are_rejected_even_when_a_stat_has_the_same_name(string form, string replacement)
    {
        var yaml = Yaml.Replace("seed: 42", $"seed: 42\nruleset:\n  base: generic\n  stats:\n    - {{ id: pronoun.{form}, default: 3 }}");
        var error = Error($"=== a\n{{player:pronoun.{form}}}\n-> END", "TW2001", yaml);
        Assert.Contains($"Use '{replacement}'.", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 17), error.Location);
    }

    [Fact]
    public void Non_legacy_dotted_pronoun_stat_names_remain_valid()
    {
        var yaml = Yaml.Replace("seed: 42", "seed: 42\nruleset:\n  base: generic\n  stats:\n    - { id: pronoun.fluency, default: 3 }");
        var story = Ok("=== a\n{player:pronoun.fluency}\n-> END", yaml);
        Assert.Equal("3", Render(story, story.GetScene(new SceneId("a"))));
    }

    [Theory]
    [InlineData("{player:pronoun:subject}", "Use 'player:pronoun'.")]
    [InlineData("{player:pronoun:object}", "Use 'player:pronoun#object'.")]
    [InlineData("{player:pronoun:possessive}", "Use 'player:pronoun#possessive'.")]
    [InlineData("{player:pronoun:possessive-pronoun}", "Use 'player:pronoun#possessive(\"independent\")'.")]
    [InlineData("{guards:pronoun:reflexive#capitalize}", "Use 'guards:pronoun#reflexive'.")]
    [InlineData("{player:pronoun.object}", "Use 'player:pronoun#object'.")]
    [InlineData("{player:pronoun.possessive-pronoun}", "Use 'player:pronoun#possessive(\"independent\")'.")]
    [InlineData("{player:pronoun:own}", "Unknown pronoun form 'own'")]
    public void Old_pronoun_forms_report_their_hash_replacement(string reference, string message)
    {
        var error = Error($"=== a\n{reference}\n-> END", "TW2001");
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("{player:pronoun#possessive(\"hers\")}", "Unknown possessive form \"hers\"")]
    [InlineData("{player:pronoun#object(\"x\")}", "'#object' does not take an argument")]
    [InlineData("{player:first-name#possessive(\"independent\")}", "only allowed after '<character>:pronoun'")]
    [InlineData("{player:first-name#capitalize(\"x\")}", "'#capitalize' does not take an argument")]
    [InlineData("{player:first-name#object}", "only follow '<character>:pronoun'")]
    [InlineData("{player:pronoun#capitalize#object}", "come straight after 'pronoun'")]
    [InlineData("{player:pronoun#capitalize#possessive}", "come straight after 'pronoun'")]
    [InlineData("{player:pronoun#object#reflexive}", "come straight after 'pronoun'")]
    [InlineData("{player:pronoun#subject}", "already gives the subject form")]
    [InlineData("{player:pronoun#possessive(independent)}", "double quotes")]
    [InlineData("{player:pronoun#possessive(\"independent\"}", "Expected ')'")]
    [InlineData("{player:pronoun#possessive(\"independent\") x}", "Expected '#' or the end")]
    public void Invalid_pronoun_modifiers_are_reported(string reference, string message)
    {
        var error = Error($"=== a\n{reference}\n-> END", "TW2006");
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Unclosed_pronoun_argument_quote_leaves_the_substitution_open() =>
        Error("=== a\n{player:pronoun#possessive(\"independent)}\n-> END", "TW1004");

    [Fact]
    public void Invalid_possessive_form_points_at_the_argument()
    {
        var error = Error("=== a\n{player:pronoun#possessive(\"hers\")}\n-> END", "TW2006");
        Assert.Equal(new SourceLocation("main.tale", 2, 28), error.Location);
    }

    [Fact]
    public void Loads_stories_from_zip_with_a_top_level_folder()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }

            Add("my-story/story.talewright.yaml", Yaml);
            Add("my-story/chapters/one.tale", "=== a\n-> END");
        }

        stream.Position = 0;
        var source = StorySource.FromZip(stream);
        Assert.Equal(["chapters/one.tale", "story.talewright.yaml"], source.Files.Keys.Order());
        Assert.True(StoryCompiler.Compile(source).Success);
    }

    [Fact]
    public void Rejects_a_compressible_story_file_over_the_expanded_file_limit()
    {
        var oversizedText = new string('x', 5 * 1024 * 1024 + 1);
        using var stream = CreateZip([new("chapter.tale", oversizedText)]);

        Assert.True(stream.Length < 100 * 1024);
        var error = Assert.Throws<InvalidDataException>(() => StorySource.FromZip(stream));

        Assert.Contains("chapter.tale", error.Message);
        Assert.Contains("5 MiB", error.Message);
    }

    [Fact]
    public void Rejects_story_files_that_exceed_the_total_expanded_size_limit()
    {
        var textAtFileLimit = new string('x', 5 * 1024 * 1024);
        var files = Enumerable.Range(0, 11)
            .Select(index => new KeyValuePair<string, string>($"chapter-{index}.tale", textAtFileLimit));
        using var stream = CreateZip(files);

        Assert.Throws<InvalidDataException>(() => StorySource.FromZip(stream));
    }

    [Fact]
    public void Rejects_archives_with_too_many_story_files()
    {
        var files = Enumerable.Range(0, 1_001)
            .Select(index => new KeyValuePair<string, string>($"chapter-{index}.tale", string.Empty));
        using var stream = CreateZip(files);

        var error = Assert.Throws<InvalidDataException>(() => StorySource.FromZip(stream));

        Assert.Contains("1,000 story files", error.Message);
    }

    private static MemoryStream CreateZip(IEnumerable<KeyValuePair<string, string>> files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Accepts_exact_expanded_file_and_total_limits()
    {
        var text = new string('x', 5 * 1024 * 1024);
        using var zip = CreateZip(Enumerable.Range(0, 10)
            .Select(i => new KeyValuePair<string, string>($"{i}.tale", text)));
        var source = StorySource.FromZip(zip);
        Assert.Equal(10, source.Files.Count);
        Assert.Equal(50L * 1024 * 1024, source.Files.Values.Sum(value => (long)value.Length));
    }

    [Fact]
    public void Rejects_one_byte_over_the_exact_total_limit()
    {
        var text = new string('x', 5 * 1024 * 1024);
        using var zip = CreateZip(Enumerable.Range(0, 10)
            .Select(i => new KeyValuePair<string, string>($"{i}.tale", text))
            .Append(new("extra.tale", "x")));
        Assert.Contains("50 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromZip(zip)).Message);
    }

    [Fact]
    public void Accepts_exact_story_file_count_limit()
    {
        using var zip = CreateZip(Enumerable.Range(0, 1_000)
            .Select(i => new KeyValuePair<string, string>($"{i}.tale", "")));
        Assert.Equal(1_000, StorySource.FromZip(zip).Files.Count);
    }
}
