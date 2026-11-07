using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

/// <summary>
/// TW2016 reports only labels that are certain to have no accessible name. Text, substitutions, conditions
/// and descriptive images are trusted; the compiler does not check what they render.
/// </summary>
public sealed class DecorativeChoiceLabelTests
{
    private const string Yaml = """
        game: { id: labels, title: Labels, start: start }
        characters:
          player: { first-name: Ada }
        vars: { word: "" }
        """;

    private static StorySource Source(string label) => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] = Yaml,
        ["main.tale"] = $"=== start\n* [{label}] -> END",
    }, ["images/map.png", "images/ornament.png"]);

    [Theory]
    [InlineData("""Continue {image("ornament.png")#decorative}""")]
    [InlineData("""{"Continue"}{image("ornament.png")#decorative}""")]
    [InlineData("""{var:word}{image("ornament.png")#decorative}""")]
    [InlineData("""{player:first-name}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "A map")}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "{var:word}")}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png")}{image("ornament.png")#decorative}""")]
    [InlineData("""{if var:word == "go"}Continue{/if}{image("ornament.png")#decorative}""")]
    [InlineData("""{if var:word == "go"}{image("ornament.png")#decorative}{else}Go{/if}""")]
    public void Labels_with_text_substitutions_or_descriptive_images_are_trusted(string label)
    {
        var source = Source(label);
        var result = StoryCompiler.Compile(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == CompilerDiagnosticCodes.MissingAccessibleChoiceLabel);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("""{image("ornament.png")#decorative}""")]
    [InlineData("""{image("ornament.png")#decorative} {image("map.png")#decorative}""")]
    [InlineData("""{if var:word == "go"}{image("ornament.png")#decorative}{/if}""")]
    [InlineData("""{if var:word == "go"}{image("ornament.png")#decorative}{else}{image("map.png")#decorative}{/if}""")]
    public void Labels_with_only_decorative_images_are_rejected_at_the_label(string label)
    {
        var source = Source(label);
        var result = StoryCompiler.Compile(source);

        var diagnostic = Assert.Single(result.Diagnostics,
            item => item.Code == CompilerDiagnosticCodes.MissingAccessibleChoiceLabel);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new SourceLocation("main.tale", 2, 4), diagnostic.Location);
        Assert.False(result.Success);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Fact]
    public void Custom_modifiers_are_trusted_to_name_the_label()
    {
        var options = new CompilerOptions
        {
            CompatibilityId = "test-v1",
            Modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("erase", (_, _) => "")),
        };
        var source = Source("""{"Continue"#erase}{image("ornament.png")#decorative}""");

        var result = StoryCompiler.Compile(source, options);

        Assert.DoesNotContain(result.Diagnostics, item => item.Code == CompilerDiagnosticCodes.MissingAccessibleChoiceLabel);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source, options).Diagnostics);
    }
}
