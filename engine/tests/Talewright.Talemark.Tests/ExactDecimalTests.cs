using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
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
