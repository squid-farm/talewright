using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class IntegerLiteralTests
{
    private static CompilationResult Compile(string source) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: integers, title: Integers, start: a }
                characters:
                  player: { first-name: Tester }
                vars: { count: 0 }
                """,
            ["main.tale"] = source,
        }));

    [Theory]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("0", 0)]
    [InlineData("-0", 0)]
    [InlineData("-(2147483648)", int.MinValue)]
    [InlineData("-((2147483648))", int.MinValue)]
    [InlineData("(-2147483648)", int.MinValue)]
    [InlineData("--2147483648", int.MaxValue)]
    [InlineData("-(-2147483648)", int.MaxValue)]
    [InlineData("---2147483648", -int.MaxValue)]
    [InlineData("-2147483648 + 1", int.MinValue + 1)]
    public void Literals_work_in_assignments_comparisons_and_substitutions(string source, int expected)
    {
        var result = Compile($"=== a\n@requires {source} == var:count\n~ var:count = {source}\n{{{source}}}\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var scene = story.Scenes[0];
        var log = new EventLog(story.World, 1);
        foreach (var effect in scene.EntryEffects)
        {
            EffectExecutor.Execute(effect.Effect, log, story.Predicates);
        }

        Assert.Equal(expected, log.State.GetVariable(new("count")));
        Assert.True(ExpressionEvaluator.IsTrue(scene.Requires!, EvaluationContext.For(log.State)));
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new TemplateRenderer().Render(scene.Body, log.State).ToPlainText());
        var amount = Assert.IsType<ChangeVariable>(Assert.Single(scene.EntryEffects).Effect).Amount;
        if (!source.Contains(" + ", StringComparison.Ordinal))
        {
            Assert.Equal(new NumberLiteral(expected), amount);
        }
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("999999999999999999999999999999999999999")]
    [InlineData("-999999999999999999999999999999999999999")]
    [InlineData("-(2147483648 + 0)")]
    public void Out_of_range_magnitudes_report_located_errors(string source)
    {
        var result = Compile($"=== a\n~ var:count = {source}\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic =>
            diagnostic.Code == "TW1002" && diagnostic.Location.Line == 2 && diagnostic.Location.Column > 1);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("-(")]
    [InlineData("-(2147483648")]
    [InlineData("-2147483648)")]
    public void Malformed_negation_reports_diagnostics(string source)
    {
        var result = Compile($"=== a\n~ var:count = {source}\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Location.Line == 2);
    }
}
