using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class FormattingManipulatorTests
{
    private const string Manifest = """
        game:
          id: formatting
          title: Formatting
          start: start
        characters:
          player: {first-name: Ada}
        flags: [ready]
        """;

    private static CompilationResult Compile(string tale) => StoryCompiler.Compile(new StorySource(
        new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = tale,
        }, ["images/portrait.png"]));

    [Fact]
    public void Formatting_shorthand_and_modifiers_lower_to_the_same_style_model_and_rendering()
    {
        var result = Compile("""
            === start
            **{player:first-name}**

            {player:first-name#bold}

            _{player:first-name}_

            {player:first-name#italic}

            **_{player:first-name}_**

            {player:first-name#bold#italic}

            {player:first-name#upper#bold}

            {if true}**{player:first-name}**{/if}

            {if true}{player:first-name#bold}{/if}
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var scene = result.Story!.GetScene(new SceneId("start"));
        var paragraphs = CompilerTests.Templates(scene).OfType<ParagraphTemplate>().ToArray();
        var strongShorthand = StyledSubstitution(paragraphs[0]);
        var strongModifier = StyledSubstitution(paragraphs[1]);
        Assert.Equal([TextStyle.Strong], strongShorthand.Styles);
        Assert.Equal(strongShorthand.Styles, strongModifier.Styles);
        Assert.Equal(strongShorthand.Substitution.Value, strongModifier.Substitution.Value);
        Assert.Empty(strongShorthand.Substitution.Modifiers);
        Assert.Empty(strongModifier.Substitution.Modifiers);

        var italicShorthand = StyledSubstitution(paragraphs[2]);
        var italicModifier = StyledSubstitution(paragraphs[3]);
        Assert.Equal([TextStyle.Emphasis], italicShorthand.Styles);
        Assert.Equal(italicShorthand.Styles, italicModifier.Styles);
        Assert.Equal(italicShorthand.Substitution.Value, italicModifier.Substitution.Value);
        Assert.Empty(italicShorthand.Substitution.Modifiers);
        Assert.Empty(italicModifier.Substitution.Modifiers);

        var combinedShorthand = StyledSubstitution(paragraphs[4]);
        var combinedModifier = StyledSubstitution(paragraphs[5]);
        Assert.Equal([TextStyle.Strong, TextStyle.Emphasis], combinedShorthand.Styles);
        Assert.Equal([TextStyle.Strong | TextStyle.Emphasis], combinedModifier.Styles);
        Assert.Equal(combinedShorthand.Substitution.Value, combinedModifier.Substitution.Value);

        var state = GameState.CreateInitial(result.Story.World, 1);
        var rendered = new TemplateRenderer().Render(CompilerTests.Templates(scene), state)
            .Blocks.OfType<RenderedParagraph>().Select(paragraph => paragraph.Inlines.Single()).ToArray();
        Assert.Equal(new RenderedText("Ada", TextStyle.Strong), rendered[0]);
        Assert.Equal(rendered[0], rendered[1]);
        Assert.Equal(new RenderedText("Ada", TextStyle.Emphasis), rendered[2]);
        Assert.Equal(rendered[2], rendered[3]);
        Assert.Equal(new RenderedText("Ada", TextStyle.Strong | TextStyle.Emphasis), rendered[4]);
        Assert.Equal(rendered[4], rendered[5]);
        Assert.Equal(new RenderedText("ADA", TextStyle.Strong), rendered[6]);
        Assert.Equal(rendered[0], rendered[7]);
        Assert.Equal(rendered[0], rendered[8]);
    }

    [Fact]
    public void Text_modifiers_must_precede_formatting_modifiers()
    {
        var result = Compile("""
            === start
            {player:first-name#bold#upper}
            -> END
            """);

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidModifier, error.Code);
        Assert.Contains("cannot follow a modifier that produces formatted text", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, "{player:first-name#bold".Length + 1), error.Location);
    }

    [Fact]
    public void Formatting_modifiers_are_rejected_in_plain_text_image_alt()
    {
        const string line = """{image("portrait", "{player:first-name#bold}")}""";
        var result = Compile("=== start\n" + line + "\n-> END");

        var error = Assert.Single(result.Errors);
        Assert.Equal(CompilerDiagnosticCodes.InvalidModifier, error.Code);
        Assert.Contains("plain-text fields such as image alt text", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf("#bold", StringComparison.Ordinal) + 1), error.Location);
    }

    private static (IReadOnlyList<TextStyle> Styles, SubstitutionTemplate Substitution) StyledSubstitution(
        ParagraphTemplate paragraph)
    {
        var styles = new List<TextStyle>();
        TemplateInline inline = Assert.Single(paragraph.Inlines);
        while (inline is StyledTemplate styled)
        {
            styles.Add(styled.Style);
            inline = Assert.Single(styled.Children);
        }

        return (styles, Assert.IsType<SubstitutionTemplate>(inline));
    }
}
