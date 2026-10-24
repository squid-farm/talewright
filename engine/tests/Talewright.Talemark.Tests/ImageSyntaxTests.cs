using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class ImageSyntaxTests
{
    private const string Manifest = """
        id: images
        title: Images
        start: start
        characters:
          player: { first-name: Ada }
          image: { first-name: Iris }
          imagery: { first-name: Ivy }
        """;

    private static CompilationResult Compile(string text) => StoryCompiler.Compile(
        new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = text,
        }));

    private static Talewright.Talemark.Model.StoryDefinition Story(string text)
    {
        var result = Compile(text);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Theory]
    [InlineData("""{image("maps/camp", "The camp")}""", "maps/camp", "The camp")]
    [InlineData("""{image("maps/sector (west), v2", "Joel's {player:name} #upper **map**")}""",
        "maps/sector (west), v2", "Joel's {player:name} #upper **map**")]
    [InlineData("""{image("a\"b", "He said \"Go\"; \\ remains.")}""", "a\"b", "He said \"Go\"; \\ remains.")]
    [InlineData("""{ image ( "camp" , "A } ] { camp" ) }""", "camp", "A } ] { camp")]
    [InlineData("""{image("camp")}""", "camp", "")]
    [InlineData("""{image("camp", "")}""", "camp", "")]
    [InlineData("""{image("camp", "  ")}""", "camp", "  ")]
    [InlineData("""{image("absent.bmp", "No lookup yet")}""", "absent.bmp", "No lookup yet")]
    public void Image_calls_produce_literal_media_without_asset_lookup(string call, string path, string alt)
    {
        var result = Compile($"=== start\n{call}\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("TW201", StringComparison.Ordinal));
        var paragraph = Assert.IsType<ParagraphTemplate>(result.Story!.GetScene(new SceneId("start")).Body[0]);
        Assert.Equal(new MediaTemplate(path, alt), Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void Images_work_in_prose_dialogue_choices_formatting_and_conditions()
    {
        var story = Story("""
            === start
            Before {image("camp", "Camp")} after.

            > player: See {image("map", "A } ] map")}.
            * [Pick {image("coin", "A } ] coin")}] -> END
            * [Otherwise] -> END

            **{image("bold")}**

            {if true}{image("conditional")}{/if}
            """);
        var scene = story.GetScene(new SceneId("start"));
        var prose = Assert.IsType<ParagraphTemplate>(scene.Body[0]);
        Assert.Equal(new MediaTemplate("camp", "Camp"), prose.Inlines.OfType<MediaTemplate>().Single());
        var speech = Assert.IsType<SpeechTemplate>(scene.Body[1]);
        Assert.Equal(new MediaTemplate("map", "A } ] map"), speech.Inlines.OfType<MediaTemplate>().Single());
        Assert.Equal(new MediaTemplate("coin", "A } ] coin"), scene.Choices[0].Label.OfType<MediaTemplate>().Single());
        var bold = Assert.IsType<StyledTemplate>(Assert.IsType<ParagraphTemplate>(scene.Body[2]).Inlines.Single());
        Assert.Equal(new MediaTemplate("bold", ""), bold.Children.Single());
        var conditional = Assert.IsType<ConditionalInlineTemplate>(Assert.IsType<ParagraphTemplate>(scene.Body[3]).Inlines.Single());
        Assert.Equal(new MediaTemplate("conditional", ""), conditional.Then.Single());
    }

    [Fact]
    public void Images_in_code_formatting_do_not_leak_internal_placeholders()
    {
        var story = Story("=== start\n`{image(\"camp\")}`\n-> END");
        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("start")).Body[0]);
        var code = Assert.IsType<StyledTemplate>(paragraph.Inlines.Single());
        Assert.Equal(TextStyle.Code, code.Style);
        Assert.Equal(new MediaTemplate("camp", ""), code.Children.Single());
    }

    [Fact]
    public void Escaped_image_spelling_is_literal_prose()
    {
        var story = Story("""
            === start
            \{image("camp")\}
            -> END
            """);
        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("start")).Body[0]);
        Assert.DoesNotContain(paragraph.Inlines, inline => inline is MediaTemplate);
        Assert.Equal("{image(\"camp\")}", string.Concat(paragraph.Inlines.OfType<TextTemplate>().Select(text => text.Text)));
    }

    [Fact]
    public void Image_character_references_and_prefixes_remain_substitutions()
    {
        var story = Story("=== start\n{image:name} {image:first-name#possessive} {imagery:name}\n-> END");
        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("start")).Body[0]);
        Assert.Equal(3, paragraph.Inlines.OfType<SubstitutionTemplate>().Count());
        Assert.Empty(paragraph.Inlines.OfType<MediaTemplate>());
    }

    [Fact]
    public void Scene_titles_remain_plain_strings()
    {
        var story = Story("=== start\n@title {image(\"camp\")}\n-> END");
        Assert.Equal("{image(\"camp\")}", story.GetScene(new SceneId("start")).Title);
    }

    [Fact]
    public void Non_image_string_decoding_is_unchanged()
    {
        var story = Story("""
            === start
            {"trailing\"}
            -> END
            """);
        var paragraph = Assert.IsType<ParagraphTemplate>(story.GetScene(new SceneId("start")).Body[0]);
        var substitution = Assert.IsType<SubstitutionTemplate>(paragraph.Inlines.Single());
        Assert.Equal(new TextLiteral("trailing\\"), substitution.Value);
    }

    [Theory]
    [InlineData("""{image()}""", "double quotes")]
    [InlineData("""{image(camp)}""", "double quotes")]
    [InlineData("""{image('camp')}""", "double quotes")]
    [InlineData("""{image("")}""", "non-empty path")]
    [InlineData("""{image("  ")}""", "non-empty path")]
    [InlineData("""{image("camp",)}""", "double quotes")]
    [InlineData("""{image("camp", alt)}""", "double quotes")]
    [InlineData("""{image("camp", "Alt", "extra")}""", "Expected ')'")]
    [InlineData("""{image("camp" "Alt")}""", "Expected ')'")]
    [InlineData("""{image("camp", "Alt"}""", "Expected ')'")]
    [InlineData("""{image("camp")#upper}""", "Unexpected text")]
    [InlineData("""{image("camp", "\q")}""", "Unknown image string escape")]
    [InlineData("""{image("camp", "unclosed)}""", "closing double quote")]
    [InlineData("""{image("camp")""", "closing '}'")]
    public void Invalid_images_have_actionable_diagnostics_and_do_not_hide_the_next_scene(string call, string message)
    {
        var result = Compile($"=== start\n{call}\n-> END\n=== next\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "TW1014" && error.Message.Contains(message, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Code is "TW1008" or "TW3010");
    }

    [Fact]
    public void Invalid_escape_has_an_exact_utf16_source_location()
    {
        const string line = """🦑 {image("camp", "bad \q")}""";
        var result = Compile("=== start\r\n" + line + "\r\n-> END");
        var error = Assert.Single(result.Errors, error => error.Code == "TW1014");
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('\\') + 1), error.Location);
    }

    [Fact]
    public void Multiline_image_diagnostics_map_to_the_authored_line()
    {
        var result = Compile("=== start\n{image(\"camp\",\n  bad)}\n-> END");
        var error = Assert.Single(result.Errors, error => error.Code == "TW1014");
        Assert.Equal(new SourceLocation("main.tale", 3, 3), error.Location);
    }

    [Theory]
    [InlineData("![The camp](camp)", """{image("camp", "The camp")}""")]
    [InlineData("![Joel's camp](maps/camp)", """{image("maps/camp", "Joel's camp")}""")]
    [InlineData("""![A "quote"](camp)""", """{image("camp", "A \"quote\"")}""")]
    [InlineData("![{player:first-name}'s camp](camp)", """{image("camp", "{player:first-name}'s camp")}""")]
    public void Markdown_images_name_the_escaped_replacement(string old, string replacement)
    {
        var result = Compile("=== start\n" + old + "\n-> END");
        var error = Assert.Single(result.Errors, error => error.Code == "TW1015");
        Assert.Equal(new SourceLocation("main.tale", 2, 1), error.Location);
        Assert.Contains(replacement, error.Message);
    }

    [Fact]
    public void Markdown_diagnostic_position_survives_earlier_masked_constructs()
    {
        const string line = """🦑 {player:first-name} {image("camp")} ![Old](old)""";
        var result = Compile("=== start\r\n" + line + "\r\n-> END");
        var error = Assert.Single(result.Errors, error => error.Code == "TW1015");
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf("![", StringComparison.Ordinal) + 1), error.Location);
    }

    [Fact]
    public void Markdown_images_in_dialogue_and_choices_are_rejected()
    {
        var result = Compile("=== start\n> player: ![Camp](camp)\n* [![Coin](coin)] -> END");
        Assert.Equal(2, result.Errors.Count(error => error.Code == "TW1015"));
    }
}
