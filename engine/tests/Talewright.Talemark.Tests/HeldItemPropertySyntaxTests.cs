using Talewright.Core;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class HeldItemPropertySyntaxTests
{
    private const string Manifest = """
        game:
          id: held-item-properties
          title: Held item properties
          start: start
          talemark: 2
          ruleset: rpg
        characters:
          player: { first-name: Ada }
        items:
          sword:
            name: sword
            sharpness: 5
            durability: 100
            material: iron
            magical: false
          coin:
            name: coin
            divisible: true
            weight: 1
        """;

    [Fact]
    public void Repeated_changes_hit_the_front_copy_and_the_next_take_removes_it()
    {
        var result = Compile("""
            === start
            ~ give player sword 3
            ~ player:inventory:sword:sharpness += 2
            ~ player:inventory:sword:sharpness += 2
            Sharpness: {player:inventory:sword:sharpness}.
            ~ take player sword
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var effects = scene.Steps.OfType<EffectStep>().SelectMany(step => step.Effects).ToArray();
        var log = new EventLog(story.World, 1);
        foreach (var effect in effects.Take(3))
            EffectExecutor.Execute(effect, log, story.Predicates);

        Assert.Collection(log.State.GetItemGroups(EntityId.Player, new ItemId("sword")),
            group => AssertGroup(group, 1, 9),
            group => AssertGroup(group, 2, 5));
        Assert.Equal("Sharpness: 9.", new TemplateRenderer()
            .Render(CompilerTests.Templates(scene), log.State).ToPlainText());

        EffectExecutor.Execute(effects[3], log, story.Predicates);

        Assert.Collection(log.State.GetItemGroups(EntityId.Player, new ItemId("sword")),
            group => AssertGroup(group, 2, 5));
        Assert.Collection(EventLog.Replay(story.World, 1, log.Events).State
            .GetItemGroups(EntityId.Player, new ItemId("sword")),
            group => AssertGroup(group, 2, 5));
    }

    [Fact]
    public void Unheld_property_reads_its_declaration_and_a_change_emits_no_event()
    {
        var result = Compile("""
            === start
            Sharpness: {player:inventory:sword:sharpness}.
            ~ player:inventory:sword:sharpness += 2
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var log = new EventLog(story.World, 1);
        var effect = Assert.Single(scene.Steps.OfType<EffectStep>()).Effects.Single();
        EffectExecutor.Execute(effect, log, story.Predicates);

        Assert.Empty(log.Events);
        Assert.Empty(log.State.GetItemGroups(EntityId.Player, new ItemId("sword")));
        Assert.Equal("Sharpness: 5.", new TemplateRenderer()
            .Render(CompilerTests.Templates(scene), log.State).ToPlainText());
    }

    [Fact]
    public void Number_text_and_boolean_properties_accept_only_compatible_changes()
    {
        var result = Compile("""
            === start
            ~ give player sword
            ~ player:inventory:sword:sharpness = 7
            ~ player:inventory:sword:material = "silver"
            ~ player:inventory:sword:magical = true
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var effect in story.GetScene(new SceneId("start")).Steps.OfType<EffectStep>()
                     .SelectMany(step => step.Effects))
            EffectExecutor.Execute(effect, log, story.Predicates);

        var group = Assert.Single(log.State.GetItemGroups(EntityId.Player, new ItemId("sword")));
        Assert.Equal(new ItemPropertyValue.Number(ExactNumber.FromInteger(7)), group.Properties["sharpness"]);
        Assert.Equal(new ItemPropertyValue.Text("silver"), group.Properties["material"]);
        Assert.Equal(new ItemPropertyValue.Boolean(true), group.Properties["magical"]);
    }

    [Theory]
    [InlineData("{player:inventory:sword:missing}", CompilerDiagnosticCodes.UnknownNameOrProperty)]
    [InlineData("~ player:inventory:sword:sharpness = \"bright\"", CompilerDiagnosticCodes.TypeMismatch)]
    [InlineData("~ player:inventory:sword:material += \"silver\"", CompilerDiagnosticCodes.TypeMismatch)]
    [InlineData("~ player:inventory:sword:magical -= true", CompilerDiagnosticCodes.TypeMismatch)]
    public void Undeclared_and_incompatible_properties_report_compile_errors(string source, string code)
    {
        var result = Compile($"=== start\n{source}\n-> END");

        var diagnostic = Assert.Single(result.Errors);
        Assert.Equal(code, diagnostic.Code);
    }

    [Theory]
    [InlineData("{player:inventory:coin:weight}", "Divisible items have no individual copies")]
    [InlineData("~ player:inventory:coin:weight = 2", "Divisible items have no individual copies")]
    public void Divisible_item_properties_report_the_lack_of_individual_copies(string source, string message)
    {
        var result = Compile($"=== start\n{source}\n-> END");
        var diagnostic = Assert.Single(result.Errors);

        Assert.Equal(CompilerDiagnosticCodes.UnsupportedHeldItemPropertyAccess, diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, source.Contains("~ ", StringComparison.Ordinal) ? 25 : 24),
            diagnostic.Location);
    }

    [Fact]
    public void Held_item_properties_require_talemark_2_with_an_actionable_message()
    {
        var manifest = Manifest.Replace("talemark: 2", "talemark: 1", StringComparison.Ordinal);
        var result = Compile("=== start\n{player:inventory:sword:sharpness}\n-> END", manifest);
        var diagnostic = Assert.Single(result.Errors);

        Assert.Equal(CompilerDiagnosticCodes.UnsupportedHeldItemPropertyAccess, diagnostic.Code);
        Assert.Contains("game.talemark: 2", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 25), diagnostic.Location);
    }

    [Fact]
    public void Held_property_formatting_and_condition_reads_use_the_inventory_property_reference()
    {
        var property = new InventoryItemPropertyRef(EntityId.Player, new ItemId("sword"), "sharpness",
            new ItemPropertyValue.Number(ExactNumber.FromInteger(5)));

        Assert.Equal("player:inventory:sword:sharpness", ExpressionFormatter.Format(property));
        Assert.Equal(
            [(StoryStateReadKind.InventoryItemProperty, "player:inventory:sword:sharpness")],
            StoryCondition.FromExpressions([new BinaryExpr(BinaryOp.Greater, property, new NumberLiteral(1))])
                .Reads.Select(read => (read.Kind, read.Reference)));
    }

    private static CompilationResult Compile(string tale, string? manifest = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest ?? Manifest,
            ["main.tale"] = tale,
        }));

    private static void AssertGroup(HeldItemGroup group, int quantity, int sharpness)
    {
        Assert.Equal(ExactNumber.FromInteger(quantity), group.Quantity);
        Assert.Equal(new ItemPropertyValue.Number(ExactNumber.FromInteger(sharpness)), group.Properties["sharpness"]);
    }
}
