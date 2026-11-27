using Talewright.Core;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class ItemVariableTests
{
    private const string Manifest = """
        game:
          id: item-variables
          title: Item variables
          start: start
          talemark: 2
        characters:
          player: {first-name: Ada}
        items:
          iron-ore: {name: iron ore, heat: 5}
          charcoal: {name: charcoal, divisible: true}
          steel: {name: steel}
          pants: {name: pants, name-agreement: plural}
        """;

    [Fact]
    public void Items_assign_copy_render_compare_and_spend_without_loops()
    {
        var story = Compile("""
            === start
            ~ var:first = item:iron-ore
            ~ var:second = item:charcoal
            ~ var:copy = var:first
            ~ give player var:first 3
            ~ give player var:second 1.5
            {var:copy}: {var:first:heat}; {player:inventory:var:second:count}.
            * [Combine {var:first} and {var:second}] (requires player has var:first and player has var:second and var:first == iron-ore) { take player var:first; take player var:second; give player steel } -> after
            === after
            -> END
            """);
        var scene = story.Scenes[0];
        var log = new EventLog(story.World, 1);
        foreach (var effect in scene.Steps.OfType<EffectStep>().SelectMany(step => step.Effects))
            EffectExecutor.Execute(effect, log, story.Predicates);
        Assert.Equal("iron-ore", log.State.GetTextVariable(new VarId("copy")));
        Assert.Equal("iron ore: 5; 1.5.", new TemplateRenderer()
            .Render(CompilerTests.Templates(scene), log.State).ToPlainText());
        var choice = Assert.Single(scene.Steps.OfType<ChoiceStep>()).Choice;
        Assert.True(ExpressionEvaluator.IsTrue(choice.Requires!, EvaluationContext.For(log.State)));
        foreach (var effect in choice.Effects)
            EffectExecutor.Execute(effect, log, story.Predicates);
        Assert.Equal(2, log.State.GetItemCount(EntityId.Player, new ItemId("iron-ore")));
        Assert.Equal(ExactNumber.Parse("0.5"), log.State.GetExactItemCount(EntityId.Player, new ItemId("charcoal")));
        Assert.Equal(1, log.State.GetItemCount(EntityId.Player, new ItemId("steel")));
    }

    [Theory]
    [InlineData("true", "true", "==", true)]
    [InlineData("false", "false", "==", true)]
    [InlineData("false", "false", "!=", false)]
    [InlineData("true", "false", "!=", true)]
    [InlineData("true", "item:true", "==", true)]
    [InlineData("skill-check", "item:skill-check", "==", true)]
    public void Keyword_item_ids_can_be_compared_and_formatted(string assigned, string expected, string op, bool matches)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest + "\n  \"true\": True item\n  \"false\": False item\n  skill-check: Skill item\n",
            ["main.tale"] = $"""
                === start
                ~ var:chosen = item:{assigned}
                * [Use] (requires var:chosen {op} {expected}) -> END
                """,
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        EffectExecutor.Execute(story.Scenes[0].Steps.OfType<EffectStep>().Single().Effects.Single(), log, story.Predicates);
        var condition = story.Scenes[0].Steps.OfType<ChoiceStep>().Single().Choice.Requires!;
        Assert.Equal(matches, ExpressionEvaluator.IsTrue(condition, EvaluationContext.For(log.State)));

        var formatted = ExpressionFormatter.Format(condition);
        var formattedItem = assigned == "skill-check" ? "item:skill-check" : expected.Replace("item:", "", StringComparison.Ordinal);
        Assert.Equal($"var:chosen {op} {formattedItem}", formatted);
        var reparsed = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = source.Files[StorySource.ManifestFileName],
            ["main.tale"] = $"=== start\n~ var:chosen = item:{assigned}\n* [Use] (requires {formatted}) -> END",
        }));
        Assert.True(reparsed.Success, string.Join("\n", reparsed.Diagnostics));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Item_name_copy_keeps_legacy_output_including_modifiers(int version)
    {
        var story = Compile("""
            === start
            ~ var:word = item:pants
            {var:word}. {var:word#that}. {var:word#upper}.
            -> END
            """, version);
        var log = new EventLog(story.World, 1);
        EffectExecutor.Execute(story.Scenes[0].Steps.OfType<EffectStep>().Single().Effects.Single(),
            log, story.Predicates);
        Assert.Equal("pants. that pants. PANTS.", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());
        Assert.Equal("pants", log.State.GetTextVariable(new VarId("word")));
    }

    [Fact]
    public void Group_reference_follows_values_not_front_position_and_take_only_spends_that_group()
    {
        var story = Compile("""
            === start
            ~ var:chosen = item:iron-ore
            {var:chosen}: {var:chosen:heat}; {player:inventory:var:chosen:count}.
            * [Use] (requires player has var:chosen and var:chosen == iron-ore) { take player var:chosen 10 } -> end
            === end
            -> END
            """);
        var ore = new ItemId("iron-ore");
        var log = new EventLog(story.World, 1);
        log.AppendRange(StateChanges.GiveItem(EntityId.Player, ore, 3));
        var reference = ItemReference.ForGroup(EntityId.Player, ore, log.State.GetItemGroups(EntityId.Player, ore)[0]);
        log.AppendRange(StateChanges.SetTextVariable(log.State, new VarId("chosen"), reference.Encode()));
        log.AppendRange(StateChanges.SetItemProperty(log.State, EntityId.Player, ore, "heat",
            new ItemPropertyValue.Number(7)));
        Assert.Equal("iron ore: 5; 2.", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());

        var choice = story.Scenes[0].Steps.OfType<ChoiceStep>().Single().Choice;
        Assert.True(ExpressionEvaluator.IsTrue(choice.Requires!, EvaluationContext.For(log.State)));
        EffectExecutor.Execute(choice.Effects.Single(), log, story.Predicates);
        var remaining = Assert.Single(log.State.GetItemGroups(EntityId.Player, ore));
        Assert.Equal(new ItemPropertyValue.Number(7), remaining.Properties["heat"]);
        Assert.Equal(ExactNumber.One, remaining.Quantity);
        Assert.Equal(": 0; 0.", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());
        Assert.False(ExpressionEvaluator.IsTrue(choice.Requires!, EvaluationContext.For(log.State)));
        var before = log.Events.Count;
        var operand = new ResolvedItemOperand(new TextVarRef(new VarId("chosen")));
        EffectExecutor.Execute(new GiveItem(EntityId.Player, operand, new NumberLiteral(1)), log, story.Predicates);
        EffectExecutor.Execute(new TakeItem(EntityId.Player, operand, new NumberLiteral(1)), log, story.Predicates);
        Assert.Equal(before, log.Events.Count);
        var replayed = Assert.Single(EventLog.Replay(story.World, 1, log.Events).State
            .GetItemGroups(EntityId.Player, ore));
        Assert.Equal(remaining.Quantity, replayed.Quantity);
        Assert.Equal(remaining.Properties, replayed.Properties);
    }

    [Theory]
    [InlineData("")]
    [InlineData("removed-item")]
    public void Unset_or_removed_item_is_nothing(string stored)
    {
        var story = Compile("""
            === start
            ~ var:chosen = item:iron-ore
            {var:chosen}: {player:inventory:var:chosen:count}.
            * [Use] (requires player has var:chosen) { give player var:chosen; take player var:chosen } -> end
            === end
            -> END
            """);
        var log = new EventLog(story.World, 1);
        log.AppendRange(StateChanges.SetTextVariable(log.State, new VarId("chosen"), stored));
        Assert.Equal(": 0.", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());
        var choice = story.Scenes[0].Steps.OfType<ChoiceStep>().Single().Choice;
        Assert.False(ExpressionEvaluator.IsTrue(choice.Requires!, EvaluationContext.For(log.State)));
        var before = log.Events.Count;
        foreach (var effect in choice.Effects)
            EffectExecutor.Execute(effect, log, story.Predicates);
        Assert.Equal(before, log.Events.Count);
    }

    [Theory]
    [InlineData("~ var:n = 1\n~ var:n = var:chosen", "holds an item, not a number")]
    [InlineData("~ give player steel var:chosen", "holds an item, not a number")]
    [InlineData("{var:chosen + 1}", "holds an item, not a number")]
    [InlineData("{-var:chosen}", "holds an item, not a number")]
    [InlineData("{var:chosen#round}", "holds an item, not a number")]
    [InlineData("{if var:chosen > 1}bad{/if}", "holds an item, not a number")]
    [InlineData("{if var:chosen == unknown}bad{/if}", "Unknown item 'unknown'")]
    [InlineData("{if var:chosen == item:iron-ore:heat}bad{/if}", "bare item id")]
    [InlineData("{if var:chosen == \"iron-ore\"}bad{/if}", "bare item id")]
    [InlineData("{if \"iron-ore\" == iron-ore}bad{/if}", "Unknown name")]
    public void Diagnoses_item_number_and_comparison_mistakes(string source, string message)
    {
        var result = Result($"=== start\n~ var:chosen = item:iron-ore\n{source}\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("item:unknown", "unknown")]
    public void Unknown_comparison_items_report_the_item_id_location(string operand, string id)
    {
        var line = $"{{if var:chosen == {operand}}}bad{{/if}}";
        var result = Result($"=== start\n~ var:chosen = item:iron-ore\n{line}\n-> END");
        var error = Assert.Single(result.Errors, diagnostic => diagnostic.Code == "TW2001");
        Assert.Contains($"Unknown item '{id}'", error.Message);
        Assert.Equal((3, line.IndexOf(id, StringComparison.Ordinal) + 1), (error.Location.Line, error.Location.Column));
    }

    [Fact]
    public void Item_expressions_format_and_reparse()
    {
        foreach (var source in new[] { "var:chosen == iron-ore", "var:chosen:heat" })
        {
            var story = Compile($"=== start\n~ var:chosen = item:iron-ore\n{{{source}}}\n-> END");
            var expression = Assert.IsType<SubstitutionTemplate>(
                Assert.Single(Assert.IsType<ParagraphTemplate>(CompilerTests.Templates(story.Scenes[0]).Single()).Inlines)).Value;
            Assert.Equal(source, ExpressionFormatter.Format(expression));
            Assert.True(Result($"=== start\n~ var:chosen = item:iron-ore\n{{{ExpressionFormatter.Format(expression)}}}\n-> END").Success);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Fixed_item_named_var_remains_valid_in_inventory_references(int version)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest.Replace("talemark: 2", $"talemark: {version}", StringComparison.Ordinal)
                + "\n  var: {name: token}\n",
            ["main.tale"] = "=== start\n~ give player var 2\n{player:inventory:var:name}: {player:inventory:var:count}.\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        EffectExecutor.Execute(story.Scenes[0].Steps.OfType<EffectStep>().Single().Effects.Single(), log, story.Predicates);
        Assert.Equal("token: 2.", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());
    }

    [Theory]
    [InlineData(1, "Item variables are available only in Talemark 2")]
    [InlineData(2, "does not hold an item")]
    public void Text_variables_are_not_silently_accepted_as_items(int version, string message)
    {
        var result = Result("=== start\n~ var:chosen = \"iron-ore\"\n~ give player var:chosen\n-> END", version);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_text_variable_explains_why_an_item_assignment_does_not_change_its_type()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest + "\nvars:\n  chosen: \"\"\n",
            ["main.tale"] = "=== start\n~ var:chosen = item:iron-ore\n~ give player var:chosen\n-> END",
        }));
        var error = Assert.Single(result.Errors);
        Assert.Equal("TW2003", error.Code);
        Assert.Equal(("main.tale", 3), (error.Location.File, error.Location.Line));
        Assert.Contains("holds text", error.Message);
        Assert.Contains("declaration under 'vars:' fixed its type", error.Message);
        Assert.Contains("Remove its 'vars:' entry", error.Message);
        Assert.Contains("its first assignment can be an item", error.Message);
    }

    [Theory]
    [InlineData("\"label\"", "text")]
    [InlineData("1", "a number")]
    public void Earlier_assignment_is_identified_across_files(string initialValue, string type)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["z.tale"] = "=== later\n~ var:chosen = item:iron-ore\n~ give player var:chosen\n-> END",
            ["a.tale"] = $"=== start\n~ var:chosen = {initialValue}\n-> later",
        }));
        var error = Assert.Single(result.Errors, diagnostic => diagnostic.Location.Line == 3);
        Assert.Equal("TW2003", error.Code);
        Assert.Equal("z.tale", error.Location.File);
        Assert.Contains($"holds {type}", error.Message);
        Assert.Contains("first assignment at a.tale:2:", error.Message);
        Assert.Contains("Change that first assignment to an item", error.Message);
    }

    [Fact]
    public void Earlier_assignment_is_identified_in_the_same_file()
    {
        var result = Result("=== start\n~ var:chosen = \"label\"\n~ var:chosen = item:iron-ore\n~ give player var:chosen\n-> END");
        var error = Assert.Single(result.Errors);
        Assert.Equal(("TW2003", "main.tale", 4), (error.Code, error.Location.File, error.Location.Line));
        Assert.Contains("first assignment at main.tale:2:", error.Message);
    }

    [Fact]
    public void First_item_assignment_still_rejects_later_text_assignment()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["a.tale"] = "=== start\n~ var:chosen = item:iron-ore\n-> later",
            ["z.tale"] = "=== later\n~ var:chosen = \"label\"\n~ give player var:chosen\n-> END",
        }));
        var error = Assert.Single(result.Errors);
        Assert.Equal(("TW2003", "z.tale", 2), (error.Code, error.Location.File, error.Location.Line));
        Assert.Contains("needs an item reference", error.Message);
    }

    [Fact]
    public void Text_variable_can_still_copy_an_item_name()
    {
        var story = Compile("=== start\n~ var:chosen = \"label\"\n~ var:chosen = item:iron-ore\n{var:chosen}\n-> END");
        var log = new EventLog(story.World, 1);
        foreach (var effect in story.Scenes[0].Steps.OfType<EffectStep>().SelectMany(step => step.Effects))
            EffectExecutor.Execute(effect, log, story.Predicates);
        Assert.Equal("iron ore", new TemplateRenderer()
            .Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText());
    }

    private static StoryDefinition Compile(string source, int version = 2)
    {
        var result = Result(source, version);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static CompilationResult Result(string source, int version = 2) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest.Replace("talemark: 2", $"talemark: {version}", StringComparison.Ordinal),
            ["main.tale"] = source,
        }));
}
