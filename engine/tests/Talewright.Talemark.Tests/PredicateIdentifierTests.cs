using Talewright.Core.State;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class PredicateIdentifierTests
{
    private static CompilationResult Compile(string condition, NamedPredicateRegistry predicates) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: predicates, title: Predicates, start: a }
                characters:
                  player: { first-name: Tester }
                """,
            ["main.tale"] = $"=== a\n@requires {condition}\n-> END",
        }), new CompilerOptions { Predicates = predicates });

    [Theory]
    [InlineData("CanGo")]
    [InlineData("_CanGo")]
    [InlineData("Can.Go")]
    [InlineData("Can-Go")]
    [InlineData("Can.Open-door_2")]
    public void Valid_predicates_parse_intact_and_evaluate(string name)
    {
        var calls = 0;
        var predicates = new NamedPredicateRegistry().Register(name, _ => { calls++; return true; });
        var result = Compile($"(@{name})==true and not (@{name} != true)", predicates);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.True(ExpressionEvaluator.IsTrue(story.Scenes[0].Requires!,
            new EvaluationContext(GameState.CreateInitial(story.World, 1), predicates)));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("@")]
    [InlineData("@2Bad")]
    [InlineData("@Can..Go")]
    [InlineData("@Can.-Go")]
    [InlineData("@Can-")]
    [InlineData("@Can.")]
    [InlineData("@Can+Go")]
    [InlineData("@Can-Go - 1")]
    [InlineData("@Can-Go-1")]
    [InlineData("@Café")]
    [InlineData("@can-Go")]
    public void Invalid_or_unregistered_source_has_located_diagnostics(string condition)
    {
        var result = Compile(condition, new NamedPredicateRegistry().Register("Can-Go", _ => true));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Location.Line == 2 && diagnostic.Location.Column > 1);
    }
}
