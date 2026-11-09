using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class InventoryCountSyntaxTests
{
    private static string Manifest()
    {
        var manifest = TestStories.ExpressionManifest("inventory-counts", "Inventory counts", "start");
        manifest.Character("guide", "Tomas", pronouns: "he");
        manifest.Character("true", "True");
        manifest.Character("not", "Not");
        manifest.Item("invitation", "Invitation");
        return manifest.Build();
    }

    private static CompilationResult Compile(string tale, string? manifest = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest ?? Manifest(),
            ["main.tale"] = tale,
        }));

    [Fact]
    public void Counts_render_arithmetic_and_conditions_for_each_character_and_default_to_zero()
    {
        var result = Compile("""
            === start
            ~ give player invitation 3; take player invitation
            ~ give guide invitation 3; take guide invitation
            Count: {player:inventory:invitation:count}; arithmetic: {player:inventory:invitation:count + 3}.
            Guide: {guide:inventory:invitation:count}.
            {if player:inventory:invitation:count >= 2}Enough.{else}Too few.{/if}
            Missing: {player:inventory:lantern:count}.
            Reserved names: {true:inventory:invitation:count}/{not:inventory:invitation:count}.
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var batch in story.GetScene(new SceneId("start")).Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);

        var rendered = new TemplateRenderer()
            .Render(CompilerTests.Templates(story.GetScene(new SceneId("start"))), log.State).ToPlainText();
        Assert.Contains("Count: 2; arithmetic: 5.", rendered);
        Assert.Contains("Guide: 2.", rendered);
        Assert.Contains("Enough.", rendered);
        Assert.Contains("Missing: 0.", rendered);
        Assert.Contains("Reserved names: 0/0.", rendered);
    }

    [Fact]
    public void Inventory_item_names_use_the_definition_even_when_the_character_holds_none()
    {
        var result = Compile("""
            === start
            Names: {player:inventory:invitation} / {player:inventory:invitation:name}.
            Keyword ids: {true:inventory:invitation} / {not:inventory:invitation:name}.
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var rendered = new TemplateRenderer()
            .Render(CompilerTests.Templates(story.GetScene(new SceneId("start"))),
                GameState.CreateInitial(story.World, 1)).ToPlainText();

        Assert.Equal("Names: Invitation / Invitation.\nKeyword ids: Invitation / Invitation.", rendered);
    }

    [Theory]
    [InlineData("missing:inventory:invitation", "missing", "Unknown character")]
    [InlineData("player:inventory:missing", "missing", "Unknown item")]
    [InlineData("missing:inventory:invitation:name", "missing", "Unknown character")]
    [InlineData("player:inventory:missing:name", "missing", "Unknown item")]
    public void Inventory_item_name_references_validate_the_character_and_item(
        string expression, string token, string message)
    {
        var result = Compile($"=== start\n{{{expression}}}\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        AssertRange(diagnostic, $"{{{expression}}}", token);
    }

    [Fact]
    public void Inventory_is_reserved_as_a_character_property_even_when_a_custom_stat_uses_that_id()
    {
        const string manifest = """
            game:
              id: inventory-reservation
              title: Inventory reservation
              start: start
              ruleset:
                base: rpg
                stats:
                  - { id: inventory }
            characters:
              player: { first-name: Ada }
            items:
              invitation: Invitation
            """;

        var result = Compile("""
            === start
            {player:inventory}
            -> END
            """, manifest);

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidExpression, diagnostic.Code);
        Assert.Contains("inventory namespace requires an item id", diagnostic.Message);
    }

    [Theory]
    [InlineData("missing:inventory:invitation:count", "missing", "Unknown character")]
    [InlineData("player:inventory:missing:count", "missing", "Unknown item")]
    [InlineData("player:inventory:invitation:quantity", "quantity", "Unknown inventory property")]
    public void Invalid_inventory_count_references_report_exact_token_ranges(
        string expression, string token, string message)
    {
        var result = Compile($"=== start\n{{{expression}}}\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        AssertRange(diagnostic, $"{{{expression}}}", token);
    }

    [Fact]
    public void Assigning_to_an_inventory_count_reports_a_read_only_diagnostic_and_exact_range()
    {
        const string reference = "player:inventory:invitation:count";
        var result = Compile($"=== start\n~ {reference} = 2\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, diagnostic.Code);
        Assert.Contains("read-only", diagnostic.Message);
        Assert.Contains("give player invitation", diagnostic.Message);
        Assert.Contains("take player invitation", diagnostic.Message);
        AssertRange(diagnostic, $"~ {reference} = 2", reference);
    }

    [Fact]
    public void Assigning_to_an_inventory_item_name_reports_a_read_only_diagnostic()
    {
        const string reference = "player:inventory:invitation:name";
        var result = Compile($"=== start\n~ {reference} = \"Other\"\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.UnknownNameOrProperty, diagnostic.Code);
        Assert.Contains("display names are read-only", diagnostic.Message);
        AssertRange(diagnostic, $"~ {reference} = \"Other\"", reference);
    }

    private static void AssertRange(Diagnostic diagnostic, string line, string text)
    {
        var column = 1 + line.IndexOf(text, StringComparison.Ordinal);
        Assert.Equal(new SourceLocation("main.tale", 2, column), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, column + text.Length), diagnostic.EndLocation);
    }
}
