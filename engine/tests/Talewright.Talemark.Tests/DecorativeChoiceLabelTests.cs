using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

/// <summary>TW2016: a choice label with decorative media must still have a provable accessible name.</summary>
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

    private static string AccessibleName(StoryDefinition story)
    {
        var label = story.GetScene(new SceneId("start")).Choices[0].Label!;
        var rendered = new TemplateRenderer().RenderInlines(label, GameState.CreateInitial(story.World, 1));
        return string.Concat(rendered.Select(inline => inline switch
        {
            RenderedText text => text.Text,
            RenderedMedia { Accessibility: MediaAccessibility.Descriptive } media => media.AltText,
            _ => "",
        })).Trim();
    }

    [Theory]
    [InlineData("""{"Continue"}{image("ornament.png")#decorative}""")]
    [InlineData("""{"Continue"#bold}{image("ornament.png")#decorative}""")]
    [InlineData("""{"continue"#capitalize}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "A map")}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "The {var:word} map")}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png")}{image("ornament.png")#decorative}""")]
    [InlineData("""{if true}Open{else}Close{/if}{image("ornament.png")#decorative}""")]
    public void Labels_with_a_provable_name_are_accepted_and_render_one(string label)
    {
        var source = Source(label);
        var result = StoryCompiler.Compile(source);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == CompilerDiagnosticCodes.MissingAccessibleChoiceLabel);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotEmpty(AccessibleName(result.Story!));
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("""{var:word}{image("ornament.png")#decorative}""")]
    [InlineData("""{" "}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "{var:word}")}{image("ornament.png")#decorative}""")]
    [InlineData("""{image("map.png", "{if false}Continue{/if}")}{image("ornament.png")#decorative}""")]
    [InlineData("""{if false}Continue{/if}{image("ornament.png")#decorative}""")]
    public void Labels_that_can_render_without_a_name_are_rejected_at_the_label(string label)
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

    [Theory]
    [InlineData("""{var:word}""")]
    [InlineData("""{image("map.png", "{var:word}")}""")]
    [InlineData("""{image("map.png", "{if false}Continue{/if}")}""")]
    public void Rejected_labels_really_have_no_name_when_rendered(string label)
    {
        var result = StoryCompiler.Compile(Source(label));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("", AccessibleName(result.Story!));
    }

    [Fact]
    public void Custom_modifiers_are_not_trusted_to_keep_a_literal_non_empty()
    {
        var options = new CompilerOptions
        {
            CompatibilityId = "test-v1",
            Modifiers = TextModifierRegistry.CreateDefault().Register(new DelegateTextModifier("erase", (_, _) => "")),
        };
        var source = Source("""{"Continue"#erase}{image("ornament.png")#decorative}""");

        var result = StoryCompiler.Compile(source, options);

        Assert.Single(result.Diagnostics, item => item.Code == CompilerDiagnosticCodes.MissingAccessibleChoiceLabel);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source, options).Diagnostics);
    }
}
