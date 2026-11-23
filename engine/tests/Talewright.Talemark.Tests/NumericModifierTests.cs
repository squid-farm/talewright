using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class NumericModifierTests
{
    [Fact]
    public void Version_two_modifiers_round_exact_values_and_compose_in_arithmetic_and_comparisons()
    {
        var conditions = string.Join(" and ", new[]
        {
            "(2.5)#round == 3", "(-2.5)#round == -3",
            "2.499999#round == 2", "2.500001#round == 3",
            "(-2.499999)#round == -2", "(-2.500001)#round == -3",
            "0#round == 0", "2#round == 2", "(-2)#round-up == -2",
            "0.5#round == 1", "(-0.5)#round == -1",
            "0.1#round == 0", "-0.1#round == 0",
            "0.1#round-up == 1", "-0.1#round-up == 0",
            "0.1#round-down == 0", "-0.1#round-down == -1",
            "(1 / 3)#round == 0", "(1 / 3)#round-up == 1",
            "(-1 / 3)#round-down == -1",
            "1.2#round-down#round-up == 1", "1.2#round-up#round-down == 2",
            "(0.4 + 0.2)#round + 2 == 3",
            "2147483648.6#round == 2147483649",
            "-2147483648.6#round == -2147483649",
        });
        var result = Compile(2, "vars: { amount: 0 }",
            $"=== a\n@requires {conditions}\n-> END");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var traces = new List<ExpressionTrace>();
        var context = EvaluationContext.For(new EventLog(story.World, 1).State) with
        {
            Trace = trace => traces.Add(trace),
        };
        Assert.True(ExpressionEvaluator.IsTrue(story.Scenes[0].Requires!, context));
        Assert.Contains(Descendants(Assert.Single(traces)), trace =>
            trace.Expression is UnaryExpr { Op: UnaryOp.Round } &&
            trace.Result is { } value && value.ExactNumber == ExactNumber.FromInteger(3));
    }

    [Fact]
    public void Numeric_modifiers_are_pure_until_an_explicit_assignment_and_feed_one_many()
    {
        var result = Compile(2, "vars: { amount: 0.6, rounded: 0 }", """
            === a
            @requires var:amount == 0.6
            {var:amount#round} {var:amount} {(0.6)#round#one-many("one", "many")}
            ~ var:rounded = (var:amount)#round
            ~ var:rounded += 0.6#round-up
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var scene = story.Scenes[0];
        var log = new EventLog(story.World, 1);
        Assert.True(ExpressionEvaluator.IsTrue(scene.Requires!, EvaluationContext.For(log.State)));
        Assert.Equal("1 0.6 one",
            new TemplateRenderer().Render(CompilerTests.Templates(scene), log.State).ToPlainText());
        Assert.Equal(ExactNumber.Parse("0.6"), log.State.GetExactVariable(new VarId("amount")));

        foreach (var batch in scene.Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);

        Assert.Equal(ExactNumber.Parse("0.6"), log.State.GetExactVariable(new VarId("amount")));
        Assert.Equal(ExactNumber.FromInteger(2), log.State.GetExactVariable(new VarId("rounded")));
    }

    [Fact]
    public void Numeric_modifiers_work_in_yaml_embedded_expressions()
    {
        const string manifest = """
            game:
              id: rounding-manifest
              title: Rounding manifest
              talemark: 2
              start: a
            characters:
              player: { first-name: Tester }
            flags: [rounded]
            triggers:
              - id: rounded
                when: "(0.6)#round-up == 1"
                do: set flag:rounded
            """;
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = "=== a\n-> END",
        }));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var trigger = Assert.Single(story.Triggers);
        Assert.True(ExpressionEvaluator.IsTrue(trigger.When,
            EvaluationContext.For(new EventLog(story.World, 1).State)));
    }

    [Fact]
    public void Talemark_one_rejects_numeric_modifiers_with_a_version_guidance_diagnostic()
    {
        var result = Compile(1, "vars: { amount: 0 }",
            "=== a\n@requires 1#round == 1\n-> END");

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("TW2006", diagnostic.Code);
        Assert.Contains("game.talemark: 2", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 12), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, 18), diagnostic.EndLocation);
    }

    [Fact]
    public void Nonnumeric_modifier_diagnostic_highlights_the_complete_operator()
    {
        var result = Compile(2, "vars: { amount: 0 }",
            "=== a\n@requires \"text\"#round == \"text\"\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("TW2006", diagnostic.Code);
        Assert.Equal(new SourceLocation("main.tale", 2, 17), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, 23), diagnostic.EndLocation);
    }

    [Theory]
    [InlineData("=== a\n@requires 1#round(2) == 1\n-> END", 12, 18)]
    [InlineData("=== a\n@requires 1# == 1\n-> END", 12, 13)]
    public void Argument_and_incomplete_modifier_diagnostics_have_precise_ranges(
        string tale, int startColumn, int endColumn)
    {
        var result = Compile(2, "vars: { amount: 0 }", tale);

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("TW2006", diagnostic.Code);
        Assert.Equal(new SourceLocation("main.tale", 2, startColumn), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, endColumn), diagnostic.EndLocation);
    }

    [Fact]
    public void Numeric_modifier_chains_accept_256_modifiers()
    {
        var modifiers = string.Concat(Enumerable.Repeat("#round", 256));
        var result = Compile(2, "vars: { amount: 0 }",
            $"=== a\n@requires 1{modifiers} == 1\n-> END");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == "TW1016");
    }

    [Fact]
    public void Numeric_modifier_chains_report_257_modifiers_at_the_crossing()
    {
        var modifiers = string.Concat(Enumerable.Repeat("#round", 257));
        var result = Compile(2, "vars: { amount: 0 }",
            $"=== a\n@requires 1{modifiers} == 1\n-> END");

        var diagnostic = Assert.Single(result.Diagnostics, item =>
            item.Code == "TW1016");
        Assert.Equal("Expression is nested more than 256 levels deep.",
            diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2,
            12 + (256 * "#round".Length)),
            diagnostic.Location);
    }

    [Fact]
    public void Numeric_modifiers_count_existing_prefix_depth()
    {
        var validExpression = new string('-', 255) + "1#round";
        var validResult = Compile(2, "vars: { amount: 0 }",
            $"=== a\n@requires {validExpression} == 1\n-> END");

        Assert.True(validResult.Success, string.Join("\n", validResult.Diagnostics));
        Assert.DoesNotContain(validResult.Diagnostics, diagnostic =>
            diagnostic.Code == "TW1016");

        var invalidExpression = new string('-', 256) + "1#round";
        var invalidResult = Compile(2, "vars: { amount: 0 }",
            $"=== a\n@requires {invalidExpression} == 1\n-> END");
        var diagnostic = Assert.Single(invalidResult.Diagnostics, item =>
            item.Code == "TW1016");

        Assert.Equal("Expression is nested more than 256 levels deep.",
            diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 268), diagnostic.Location);
    }

    [Theory]
    [InlineData(2, "=== a\n@requires \"text\"#round == \"text\"\n-> END", "requires a number-valued expression")]
    [InlineData(2, "=== a\n@requires 1#round(2) == 1\n-> END", "takes no arguments")]
    [InlineData(2, "=== a\n@requires 1#rounding == 1\n-> END", "Unknown numeric modifier")]
    [InlineData(2, "=== a\n@requires 1# == 1\n-> END", "Write a numeric modifier")]
    [InlineData(2, "=== a\n{1#upper#round}\n-> END", "before text modifiers")]
    public void Invalid_numeric_modifier_forms_report_actionable_diagnostics(
        int version, string tale, string expectedMessage)
    {
        var result = Compile(version, "vars: { amount: 0 }", tale);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal("TW2006", diagnostic.Code);
        Assert.Contains(expectedMessage, diagnostic.Message);
    }

    [Fact]
    public void Numeric_modifier_formatting_preserves_grouping_and_round_trips()
    {
        const string source = "(var:amount + 1)#round-down#round-up";
        var result = Compile(2, "vars: { amount: 0 }", $"=== a\n{{{source}}}\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var value = Assert.IsType<SubstitutionTemplate>(
            Assert.Single(CompilerTests.Templates(result.Story!.Scenes[0]).OfType<ParagraphTemplate>().Single().Inlines)).Value;

        var formatted = ExpressionFormatter.Format(value);
        Assert.Equal(source, formatted);

        var reparsed = Compile(2, "vars: { amount: 0 }", $"=== a\n{{{formatted}}}\n-> END");
        Assert.True(reparsed.Success, string.Join("\n", reparsed.Diagnostics));
        var reparsedValue = Assert.IsType<SubstitutionTemplate>(
            Assert.Single(CompilerTests.Templates(reparsed.Story!.Scenes[0]).OfType<ParagraphTemplate>().Single().Inlines)).Value;
        Assert.Equal(value, reparsedValue);
    }

    private static CompilationResult Compile(int version, string variables, string tale) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = $$"""
                game:
                  id: numeric-modifiers
                  title: Numeric modifiers
                  talemark: {{version}}
                  start: a
                characters:
                  player: { first-name: Tester }
                {{variables}}
                """,
            ["main.tale"] = tale,
        }));

    private static IEnumerable<ExpressionTrace> Descendants(ExpressionTrace trace)
    {
        yield return trace;
        foreach (var child in trace.Children)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
