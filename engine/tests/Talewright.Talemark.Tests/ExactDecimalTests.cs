using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Core.Rulesets;
using Talewright.Rulesets.Generic;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public class ExactDecimalTests
{
    [Fact]
    public void Version_two_uses_exact_decimal_literals_initial_values_and_arithmetic()
    {
        var result = Compile(2, """
            vars: { quarter: 0.25 }
            """, """
            === a
            @requires .25 == 0.25 and 1 / 4 == 0.25 and 7 / 2 == 3.5 and var:quarter / 2 == .125 and 1 / 3 * 3 == 1 and 1 / 0 == 0 and var:quarter / 0 == 0
            ~ var:quarter += 0.25
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal(2, story.TalemarkVersion);
        Assert.Equal(ExactNumber.FromFraction(1, 4), story.World.ExactVariables[new VarId("quarter")]);

        var scene = story.Scenes[0];
        var log = new EventLog(story.World, 1);
        Assert.True(ExpressionEvaluator.IsTrue(scene.Requires!, EvaluationContext.For(log.State)));
        foreach (var batch in scene.Steps.OfType<EffectStep>())
        {
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        }

        Assert.Equal(ExactNumber.FromFraction(1, 2), log.State.GetExactVariable(new VarId("quarter")));
    }

    [Fact]
    public void Version_two_preserves_negative_fractional_values()
    {
        var result = Compile(2, """
            vars: { amount: 0 }
            """, """
            === a
            ~ var:amount = -0.25
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var batch in story.Scenes[0].Steps.OfType<EffectStep>())
        {
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        }

        Assert.Equal(ExactNumber.FromFraction(-1, 4), log.State.GetExactVariable(new VarId("amount")));
    }

    [Fact]
    public void Version_two_accepts_exponent_notation_for_manifest_initial_values()
    {
        var result = Compile(2, """
            vars: { hundred: 1e2, tiny: 1.25e-2 }
            """, """
            === a
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(ExactNumber.FromInteger(100), result.Story!.World.ExactVariables[new VarId("hundred")]);
        Assert.Equal(ExactNumber.FromFraction(1, 80), result.Story.World.ExactVariables[new VarId("tiny")]);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("1.")]
    [InlineData("1..2")]
    public void Malformed_version_two_decimals_report_a_source_location(string expression)
    {
        var result = Compile(2, "vars: { amount: 0 }", $"=== a\n~ var:amount = {expression}\n-> END");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Location.Line == 2);
    }

    [Fact]
    public void Version_one_rejects_decimal_initial_values_and_expressions()
    {
        var initialValue = Compile(1, "vars: { amount: 0.25 }", "=== a\n-> END");
        var expression = Compile(1, "vars: { amount: 0 }", "=== a\n~ var:amount = 0.25\n-> END");

        Assert.False(initialValue.Success);
        Assert.False(expression.Success);
        Assert.Contains(initialValue.Errors, diagnostic => diagnostic.Location.Line == 8);
        Assert.Contains(expression.Errors, diagnostic => diagnostic.Location.Line == 2);
    }

    [Fact]
    public void Version_one_division_by_zero_still_evaluates_to_zero()
    {
        var result = Compile(1, "vars: { amount: 0 }", "=== a\n@requires 1 / 0 == 0\n-> END");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var state = new EventLog(result.Story!.World, 1).State;
        Assert.True(ExpressionEvaluator.IsTrue(result.Story.Scenes[0].Requires!,
            EvaluationContext.For(state)));
    }

    [Fact]
    public void Resources_preserve_damage_and_whole_stats_round_each_incoming_change()
    {
        var result = CompileInventory(2, """
            characters: { player: { first-name: Tester, stats: { hp: 20, str: 10 } } }
            """, """
            === a
            ~ player:hp -= 7 * (1 - .25)
            ~ player:str += .49; player:str += .49
            ~ player:str += .5; player:str += -.5
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var log = new EventLog(result.Story!.World, 1);
        foreach (var batch in result.Story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, result.Story.Predicates);
        Assert.Equal(ExactNumber.Parse("14.75"), log.State.GetExactBaseStat(EntityId.Player, new("hp")));
        Assert.Equal(10, log.State.GetBaseStat(EntityId.Player, new("str")));
        var resourceCheck = new D20SkillCheckRule().CalculateTotal(log.State, new SkillCheckRequest(EntityId.Player, new("hp"), null));
        Assert.Equal(15, resourceCheck.Modifier);
        Assert.Equal(ExactNumber.Parse("14.75"), log.State.GetExactBaseStat(EntityId.Player, new("hp")));
    }

    [Fact]
    public void Whole_stat_rounds_negative_and_positive_amounts_independently()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: whole-boundaries
                  title: Whole boundaries
                  talemark: 2
                  start: a
                  ruleset:
                    stats: [{ id: score, category: custom, min: -20, max: 20 }]
                characters: { player: { first-name: Tester } }
                """,
            ["main.tale"] = """
                === a
                ~ player:score = -.5
                ~ player:score += -.49; player:score += -.49
                ~ player:score -= .5
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var log = new EventLog(result.Story!.World, 1);
        foreach (var batch in result.Story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, result.Story.Predicates);
        Assert.Equal(-2, log.State.GetBaseStat(EntityId.Player, new("score")));
        Assert.Equal(2, log.Events.OfType<StatChanged>().Count());
    }

    [Fact]
    public void Level_stat_remains_whole_even_when_declared_as_a_resource()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: whole-level
                  title: Whole level
                  talemark: 2
                  start: a
                  ruleset:
                    base: rpg
                    stats: [{ id: level, category: resource, min: 1, max: 20 }]
                characters: { player: { first-name: Tester, stats: { level: 2.4 } } }
                """,
            ["main.tale"] = "=== a\n~ player:level += .49; player:level += .5\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var log = new EventLog(result.Story!.World, 1);
        foreach (var batch in result.Story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, result.Story.Predicates);
        Assert.Equal(3, log.State.GetBaseStat(EntityId.Player, new("level")));
        Assert.Equal(ExactNumber.FromInteger(3), log.State.GetExactBaseStat(EntityId.Player, new("level")));
    }

    [Fact]
    public void Resource_initial_bounds_and_held_modifiers_are_exact_and_clamped()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: resources
                  title: Resources
                  talemark: 2
                  start: a
                  ruleset:
                    stats:
                      - { id: mana, category: resource, min: 0.25, max: 20.75, default: 10.5 }
                items: { charm: { modifiers: { mana: .25 } } }
                characters: { player: { first-name: Tester, stats: { mana: 20.25 }, inventory: [charm] }, guide: { first-name: Guide } }
                """,
            ["main.tale"] = """
                === a
                @requires player:mana == 20.5
                ~ player:mana += 1 / 3; player:mana += 1 / 3
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        Assert.Equal(ExactNumber.Parse("10.5"), log.State.GetExactBaseStat(new("guide"), new("mana")));
        Assert.True(ExpressionEvaluator.IsTrue(story.Scenes[0].Requires!, EvaluationContext.For(log.State)));
        foreach (var batch in story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        Assert.Equal(ExactNumber.Parse("20.75"), log.State.GetExactBaseStat(EntityId.Player, new("mana")));
        Assert.Equal(ExactNumber.Parse("20.75"), log.State.GetExactStat(EntityId.Player, new("mana")));
        log.AppendRange(StateChanges.AdjustStat(log.State, EntityId.Player, new("mana"), -100));
        Assert.Equal(ExactNumber.Parse(".25"), log.State.GetExactBaseStat(EntityId.Player, new("mana")));
    }

    [Fact]
    public void Divisible_inventory_keeps_exact_amounts_and_ordinary_items_round_each_change()
    {
        var result = CompileInventory(2, """
            items:
              credits: { name: credits, divisible: true }
              dust: { name: dust, divisible: true, uncountable: true, modifiers: { str: 2 } }
              rope: Rope
            characters:
              player: { first-name: Tester, inventory: { credits: 100, dust: 0.1, rope: 3 } }
            """, """
            === a
            @requires player has dust and player:inventory:dust:count == .1 and player:str == 12
            ~ take player credits 99 * (1 - .1)
            ~ take player rope .5
            ~ give player dust 1 / 3; take player dust 1 / 3
            ~ give player credits 0; take player credits 0; take player credits -1; give player dust -.1; give player rope -.5
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        Assert.True(ExpressionEvaluator.IsTrue(story.Scenes[0].Requires!, EvaluationContext.For(log.State)));
        foreach (var batch in story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);

        Assert.Equal(ExactNumber.Parse("10.9"), log.State.GetExactItemCount(EntityId.Player, new("credits")));
        Assert.Equal(ExactNumber.Parse(".1"), log.State.GetExactItemCount(EntityId.Player, new("dust")));
        Assert.Equal(2, log.State.GetItemCount(EntityId.Player, new("rope")));
        Assert.Equal(12, log.State.GetStat(EntityId.Player, new("str")));
        Assert.Equal(4, log.Events.Count);
        Assert.False(story.World.Items[new("rope")].Divisible);
        Assert.True(story.World.Items[new("dust")].Uncountable);
        Assert.True(story.World.Items[new("dust")].Divisible);
        Assert.False(story.World.Items[new("credits")].Uncountable);
        Assert.Equal("891/10", Assert.Single(log.Events.OfType<ExactItemsRemoved>(),
            item => item.Item == new ItemId("credits")).Quantity);
    }

    [Fact]
    public void Insufficient_fractional_take_removes_only_the_amount_held()
    {
        var result = CompileInventory(2, """
            items: { liquid: { divisible: true } }
            characters: { player: { first-name: Tester, inventory: { liquid: .25 } } }
            """, """
            === a
            @requires not player has liquid
            ~ take player liquid .5
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var batch in story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        Assert.Equal(ExactNumber.Zero, log.State.GetExactItemCount(EntityId.Player, new("liquid")));
        Assert.Equal("1/4", Assert.Single(log.Events.OfType<ExactItemsRemoved>()).Quantity);
        Assert.True(ExpressionEvaluator.IsTrue(story.Scenes[0].Requires!, EvaluationContext.For(log.State)));
        var integerRequest = new EventLog(story.World, 1);
        integerRequest.AppendRange(StateChanges.TakeItem(integerRequest.State, EntityId.Player, new("liquid"), 1));
        Assert.Equal("1/4", Assert.Single(integerRequest.Events.OfType<ExactItemsRemoved>()).Quantity);
    }

    [Fact]
    public void Repeated_fractional_operations_and_initial_whole_boundaries_remain_exact()
    {
        var result = CompileInventory(2, """
            items: { liquid: { divisible: true }, rope: Rope }
            characters: { player: { first-name: Tester, inventory: { liquid: 0, rope: .5 } } }
            """, """
            === a
            ~ give player liquid 1 / 3; give player liquid 1 / 3; give player liquid 1 / 3
            ~ take player rope .5
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var log = new EventLog(result.Story!.World, 1);
        Assert.Equal(1, log.State.GetItemCount(EntityId.Player, new("rope")));
        foreach (var batch in result.Story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, result.Story.Predicates);
        Assert.Equal(ExactNumber.One, log.State.GetExactItemCount(EntityId.Player, new("liquid")));
        Assert.Equal(0, log.State.GetItemCount(EntityId.Player, new("rope")));
        log.AppendRange(StateChanges.TakeItem(log.State, EntityId.Player, new("liquid"), 2));
        Assert.Equal(ExactNumber.Zero, log.State.GetExactItemCount(EntityId.Player, new("liquid")));
        Assert.Equal("1", Assert.IsType<ExactItemsRemoved>(log.Events[^1]).Quantity);
    }

    [Fact]
    public void Version_one_retains_integer_inventory_and_rejects_fractional_initial_values()
    {
        const string declarations = """
            items: { coin: { divisible: true } }
            characters: { player: { first-name: Tester, inventory: { coin: 3 } } }
            """;
        var result = CompileInventory(1, declarations, """
            === a
            ~ take player coin 1 / 2
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var log = new EventLog(result.Story!.World, 1);
        foreach (var batch in result.Story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, result.Story.Predicates);
        Assert.Equal(3, log.State.GetItemCount(EntityId.Player, new("coin")));
        Assert.Empty(log.Events);
        Assert.False(CompileInventory(1, declarations.Replace("coin: 3", "coin: .5"), "=== a\n-> END").Success);
    }

    [Theory]
    [InlineData("-0.25")]
    [InlineData("\"0.25\"")]
    [InlineData("bad")]
    public void Invalid_version_two_initial_quantities_are_diagnosed(string quantity)
    {
        var result = CompileInventory(2, $$"""
            items: { liquid: { divisible: true } }
            characters: { player: { first-name: Tester, inventory: { liquid: {{quantity}} } } }
            """, "=== a\n-> END");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Location.Line == 8);
    }

    private static CompilationResult CompileInventory(int version, string declarations, string tale) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                game:
                  id: exact-inventory
                  title: Exact inventory
                  talemark: {{version}}
                  start: a
                  ruleset: rpg
                {{declarations}}
                """,
            ["main.tale"] = tale,
        }));

    private static CompilationResult Compile(int version, string variables, string tale) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                game:
                  id: exact-decimals
                  title: Exact Decimals
                  talemark: {{version}}
                  start: a
                characters:
                  player: { first-name: Tester }
                {{variables}}
                """,
            ["main.tale"] = tale,
        }));
}
