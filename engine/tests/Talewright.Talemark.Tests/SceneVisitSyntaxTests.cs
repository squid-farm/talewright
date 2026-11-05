using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SceneVisitSyntaxTests
{
    private static readonly string Manifest = CreateManifest();

    private static string CreateManifest()
    {
        var manifest = TestStories.ExpressionManifest("visits", "Visits", "start");
        manifest.Character("guide", "Guide", pronouns: "they");
        return manifest.Build();
    }

    private static CompilationResult Compile(string tale, string? manifest = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest ?? Manifest,
            ["main.tale"] = tale,
        }));

    [Fact]
    public void Current_and_named_scene_visit_counts_work_for_a_hub_and_render_on_entry()
    {
        var result = Compile("""
            === start
            {if scene:player:visits == 1}First entry.{/if}
            * [Visit camp] -> camp
            === camp
            @hub offer
            @tags camp
            @requires scene:player:visits == 1 and start:player:visits == 1 and player visited start
            * [Return] -> start
            * [Stay] -> camp
            === destination
            @tags offer
            @requires scene:player:visits == 1 and start:player:visits == 1 and player visited start
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var state = GameState.CreateInitial(story.World, 1);
        state.Apply(new SceneEntered(new SceneId("start")));
        var start = story.GetScene(new SceneId("start"));
        var rendered = new TemplateRenderer().Render(CompilerTests.Templates(start), state);
        Assert.Contains("First entry.", rendered.ToPlainText());

        state.Apply(new SceneEntered(new SceneId("camp")));
        Assert.True(ExpressionEvaluator.IsTrue(story.GetScene(new SceneId("camp")).Requires!, EvaluationContext.For(state)));
        state.Apply(new SceneEntered(new SceneId("camp")));
        Assert.False(ExpressionEvaluator.IsTrue(story.GetScene(new SceneId("camp")).Requires!, EvaluationContext.For(state)));

        var offer = Assert.Single(StoryDiverts.From(story).HubOffers);
        Assert.Equal(
            [
                (StoryStateReadKind.SceneVisitCount, "scene:player:visits"),
                (StoryStateReadKind.SceneVisitCount, "start:player:visits"),
                (StoryStateReadKind.VisitedScene, "player visited start"),
            ],
            offer.Condition.Reads.Select(read => (read.Kind, read.Reference)));
        Assert.Equal("destination", offer.Scene.Value);
    }

    [Fact]
    public void Visit_counts_are_numeric_and_the_current_scene_alias_evaluates()
    {
        var result = Compile("""
            === start
            @requires scene:player:visits == 1 and camp:player:visits >= 0 and flag:player:visits >= 0 and flag:ready == false
            -> END
            === camp
            @requires scene:player:visits == 0
            -> END
            === flag
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var state = GameState.CreateInitial(result.Story!.World, 1);
        Assert.True(ExpressionEvaluator.IsTrue(result.Story.GetScene(new SceneId("camp")).Requires!,
            EvaluationContext.For(state)));
        state.Apply(new SceneEntered(new SceneId("start")));
        Assert.True(ExpressionEvaluator.IsTrue(result.Story.GetScene(new SceneId("start")).Requires!,
            EvaluationContext.For(state)));
        Assert.False(ExpressionEvaluator.IsTrue(result.Story.GetScene(new SceneId("camp")).Requires!,
            EvaluationContext.For(state)));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("not")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("visited")]
    [InlineData("has")]
    [InlineData("flag")]
    [InlineData("var")]
    [InlineData("faction")]
    [InlineData("item")]
    public void Scene_ids_matching_expression_words_can_be_counted(string scene)
    {
        var result = Compile($"""
            === start
            @requires {scene}:player:visits == 0 and not player visited {scene}
            -> END
            === {scene}
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var state = GameState.CreateInitial(result.Story!.World, 1);
        var requires = result.Story.GetScene(new SceneId("start")).Requires!;
        Assert.True(ExpressionEvaluator.IsTrue(requires, EvaluationContext.For(state)));
        state.Apply(new SceneEntered(new SceneId(scene)));
        Assert.False(ExpressionEvaluator.IsTrue(requires, EvaluationContext.For(state)));
    }

    [Theory]
    [InlineData("guide:visits > 0")]
    [InlineData("guide:player:visits > 0")]
    [InlineData("guide visited camp")]
    public void Other_character_visit_queries_are_rejected(string expression)
    {
        var result = Compile($"=== start\n@requires {expression}\n-> END\n=== camp\n-> END");

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, error.Code);
        Assert.Contains("Only player visits are tracked", error.Message);
    }

    [Theory]
    [InlineData("@requires visited(camp)\n-> END", "camp")]
    [InlineData("@requires not visited( camp-site )\n-> END", "camp-site")]
    [InlineData("@requires visited(temple.hall)\n-> END", "temple.hall")]
    [InlineData("{visited(camp)}\n-> END", "camp")]
    [InlineData("* (requires visited(camp)) -> END", "camp")]
    [InlineData("~ player:hp = visited(camp)\n-> END", "camp")]
    public void Former_visited_call_gets_a_replacement_hint(string body, string scene)
    {
        var result = Compile($"=== start\n{body}\n=== camp\n-> END\n=== camp-site\n-> END\n=== temple.hall\n-> END");

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidExpression, error.Code);
        Assert.Contains($"player visited {scene}", error.Message);
    }

    [Theory]
    [InlineData("visited(camp)")]
    [InlineData("\"visited(camp)\"")]
    public void Former_visited_call_in_a_trigger_gets_a_replacement_hint(string expression)
    {
        var manifest = Manifest +
            $"\ntriggers:\n  - id: revisit\n    when: {expression}\n    do: set flag:ready\n";
        var result = Compile("=== start\n-> END\n=== camp\n-> END", manifest);

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidExpression, error.Code);
        Assert.Equal(StorySource.ManifestFileName, error.Location.File);
        Assert.Contains("player visited camp", error.Message);
    }

    [Theory]
    [InlineData("visited()")]
    [InlineData("visited(1)")]
    [InlineData("visited(camp, other)")]
    [InlineData("visited(camp")]
    [InlineData("visited(camp + 1)")]
    public void Malformed_visited_calls_do_not_get_misleading_replacement_hints(string expression)
    {
        var result = Compile($"=== start\n@requires {expression}\n-> END\n=== camp\n-> END");

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidExpression, error.Code);
        Assert.DoesNotContain("player visited", error.Message);
    }

    [Theory]
    [InlineData("missing:player:visits > 0", CompilerDiagnosticCodes.UnknownScene)]
    [InlineData("player visited missing", CompilerDiagnosticCodes.UnknownScene)]
    [InlineData("scene:player:visits", CompilerDiagnosticCodes.TypeMismatch)]
    [InlineData("scene:player:visits == true", CompilerDiagnosticCodes.TypeMismatch)]
    public void Visit_expressions_report_unknown_scenes_and_type_errors(string expression, string code)
    {
        var result = Compile($"=== start\n@requires {expression}\n-> END\n=== camp\n-> END");

        Assert.Contains(result.Errors, error => error.Code == code);
    }

    [Fact]
    public void Scene_and_character_ids_cannot_collide()
    {
        var manifest = TestStories.ExpressionManifest("visits", "Visits", "start");
        manifest.Character("camp", "Camp");
        var result = Compile("=== start\n-> END\n=== camp\n-> END", manifest.Build());

        var error = Assert.Single(result.Errors, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.SceneCharacterIdCollision);
        Assert.Contains("Scene 'camp' and character 'camp'", error.Message);
        Assert.Contains("story.talewright.yaml:", error.Message);
    }

    [Theory]
    [InlineData("=== scene\n-> END")]
    public void Reserved_scene_alias_cannot_be_a_scene_id(string tale)
    {
        var result = Compile(tale);

        Assert.Contains(result.Errors, error => error.Code == CompilerDiagnosticCodes.InvalidSceneId);
    }

    [Fact]
    public void Reserved_scene_alias_cannot_be_a_character_id()
    {
        var manifest = TestStories.ExpressionManifest("visits", "Visits", "start");
        manifest.Character("scene", "Scene");
        var result = Compile("=== start\n-> END", manifest.Build());

        Assert.Contains(result.Errors, error => error.Code == CompilerDiagnosticCodes.InvalidConfigurationValue);
    }
}
