using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Formatting;

namespace Talewright.Talemark.Tests;

public sealed class CustomItemPropertyTests
{
    private const string Manifest = """
        game:
          id: custom-item-properties
          title: Custom item properties
          start: start
          talemark: 2
          ruleset: rpg
        characters:
          player: { first-name: Ada }
        items:
          rope:
            name: Rope
            weight: 1.25
            fragile: false
            color: blue
          pronoun:
            material: silver
        """;

    [Fact]
    public void Typed_item_properties_bind_evaluate_render_and_preserve_item_ids_named_pronoun()
    {
        var result = Compile("""
            === start
            @requires item:rope:weight >= 1.25 and item:rope:fragile == false and item:rope:color == "blue" and item:pronoun:material == "silver"
            Rope: {item:rope:weight}, {item:rope:color}, {item:rope:fragile}.
            Pronoun item: {item:pronoun:material}.
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.PossibleItemEngineKey);
        var scene = story.GetScene(new SceneId("start"));
        Assert.True(ExpressionEvaluator.IsTrue(scene.Requires!,
            EvaluationContext.For(GameState.CreateInitial(story.World, 1))));
        Assert.Equal(
            "Rope: 1.25, blue, false.\nPronoun item: silver.",
            new TemplateRenderer().Render(CompilerTests.Templates(scene),
                GameState.CreateInitial(story.World, 1)).ToPlainText());

        Assert.Equal("item:rope:color",
            ExpressionFormatter.Format(new ItemPropertyRef(new ItemId("rope"), "color",
                new ItemPropertyValue.Text("blue"))));
    }

    [Fact]
    public void Near_engine_item_keys_are_nonblocking_hints_with_exact_ranges()
    {
        var manifest = Manifest.Replace("name: Rope", "nme: Rope", StringComparison.Ordinal);
        var result = Compile("=== start\n{item:rope:nme}\n-> END", manifest);
        var hint = Assert.Single(result.Diagnostics, diagnostic =>
                diagnostic.Code == CompilerDiagnosticCodes.PossibleItemEngineKey);

        Assert.True(result.Success);
        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Equal(new SourceLocation(StorySource.ManifestFileName, 11, 5), hint.Location);
        Assert.Equal(new SourceLocation(StorySource.ManifestFileName, 11, 8), hint.EndLocation);
        Assert.Contains("engine key 'name'", hint.Message);
    }

    [Theory]
    [InlineData("{item:rope:unknown}", "unknown", "Unknown item property", CompilerDiagnosticCodes.UnknownNameOrProperty)]
    [InlineData("{item:rope:weight + \"x\"}", "weight", "Arithmetic needs numbers", CompilerDiagnosticCodes.TypeMismatch)]
    public void Unknown_properties_and_type_mismatches_point_at_the_item_property(
        string expression, string property, string message, string expectedCode)
    {
        var result = Compile($"=== start\n{expression}\n-> END");
        var diagnostic = Assert.Single(result.Errors);

        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        AssertRange(diagnostic, expression, property);
    }

    [Theory]
    [InlineData("weight", "Item properties are read-only.")]
    [InlineData("unknown", "Unknown item property")]
    public void Item_properties_cannot_be_assigned_and_unknown_properties_stay_unknown(
        string property, string message)
    {
        var line = $"~ item:rope:{property} = 2";
        var result = Compile($"=== start\n{line}\n-> END");
        var diagnostic = Assert.Single(result.Errors);

        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        AssertRange(diagnostic, line, property);
    }

    private static CompilationResult Compile(string tale, string? manifest = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest ?? Manifest,
            ["main.tale"] = tale,
        }));

    private static void AssertRange(Diagnostic diagnostic, string line, string property)
    {
        var column = 1 + line.IndexOf(property, StringComparison.Ordinal);
        Assert.Equal(new SourceLocation("main.tale", 2, column), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, column + property.Length), diagnostic.EndLocation);
    }
}
