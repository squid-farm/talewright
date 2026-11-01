using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class ImageAltTemplateTests
{
    private const string Manifest = """
        game:
          id: alt
          title: Alt
          start: gate
        characters:
          player: { first-name: Ada }
          guide: { first-name: Bea }
          image: { first-name: Iris }
        vars:
          place: "South"
        flags: [met]
        """;

    private static CompilationResult Compile(string tale) => StoryCompiler.Compile(
        new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = tale,
        }, ["images/portrait.png", "images/old_map-v2.png"]));

    private static Talewright.Talemark.Model.StoryDefinition Story(string tale)
    {
        var result = Compile(tale);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static IReadOnlyList<string> Alts(Talewright.Talemark.Model.StoryDefinition story)
    {
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(story.GetScene(new SceneId("gate"))),
            GameState.CreateInitial(story.World, 1));
        return document.Blocks.SelectMany(block => block switch
        {
            RenderedParagraph paragraph => paragraph.Inlines,
            RenderedSpeech speech => speech.Inlines,
            _ => [],
        }).OfType<RenderedMedia>().Select(media => media.AltText).ToArray();
    }

    [Fact]
    public void Alt_text_substitutions_render_with_the_state_where_the_image_appears()
    {
        var story = Story("""
            === gate
            {image("portrait", "{guide:first-name} at the {var:place} gate")}
            > guide: {image("portrait", "{if flag:met}{guide:first-name}{else}a stranger{/if}")}
            * [Enter {image("portrait", "**{guide:first-name}**")}] -> END
            """);
        Assert.Equal(["Bea at the South gate", "a stranger"], Alts(story));
        var scene = story.GetScene(new SceneId("gate"));
        var speech = Assert.IsType<SpeechTemplate>(CompilerTests.Templates(scene)[1]);
        var speechAlt = Assert.IsType<MediaTemplate>(Assert.Single(speech.Inlines)).AltTemplate;
        Assert.NotNull(speechAlt);
        var label = new TemplateRenderer().RenderInlines(scene.Choices[0].Label!, GameState.CreateInitial(story.World, 1));
        Assert.Equal("Bea", Assert.IsType<RenderedMedia>(label.Single(inline => inline is RenderedMedia)).AltText);
        Assert.DoesNotContain(Compile("""
            === gate
            {image("portrait", "{guide:first-name} at the gate")}
            -> END
            """).Diagnostics, diagnostic => diagnostic.Code == "TW2014");
    }

    [Fact]
    public void Escaped_braces_in_alt_text_stay_literal_and_string_escapes_still_decode()
    {
        var story = Story("""
            === gate
            {image("portrait", "\{guide:first-name\} meets {guide:first-name}")}
            {image("portrait", "Say \"{guide:first-name}\" and \\")}
            -> END
            """);
        Assert.Equal(["{guide:first-name} meets Bea", "Say \"Bea\" and \\"], Alts(story));
    }

    [Theory]
    [InlineData("""{image("old_map-v2")}""")]
    [InlineData("""{image("old_map-v2", "")}""")]
    [InlineData("""{image("old_map-v2", "   ")}""")]
    public void Missing_or_blank_alt_still_uses_the_filename_fallback(string call)
    {
        var result = Compile($"=== gate\n{call}\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("TW2014", warning.Code);
        var media = Assert.IsType<MediaTemplate>(Assert.Single(
            Assert.IsType<ParagraphTemplate>(CompilerTests.Templates(result.Story!.GetScene(new SceneId("gate")))[0]).Inlines));
        Assert.Equal(new MediaTemplate("images/old_map-v2.png", "Old map v2"), media);
        Assert.Null(media.AltTemplate);
    }

    [Fact]
    public void Unknown_character_in_alt_text_is_an_error_at_the_name()
    {
        const string line = """{image("portrait", "Say \"hi\" {nobody:name}")}""";
        var result = Compile("=== gate\n" + line + "\n-> END");
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, error => error.Code == "TW2001");
        Assert.Contains("Unknown character 'nobody'", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf("nobody", StringComparison.Ordinal) + 1), error.Location);
    }

    [Fact]
    public void Nested_images_in_alt_text_are_rejected_and_the_path_stays_literal()
    {
        const string nested = """{image("portrait", "See {image(\"coin\", \"A coin\")} now")}""";
        var nestedResult = Compile("=== gate\n" + nested + "\n-> END");
        var error = Assert.Single(nestedResult.Errors, error => error.Code == "TW1014");
        Assert.Contains("cannot contain {image(...)}", error.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, nested.LastIndexOf("{image(", StringComparison.Ordinal) + 1), error.Location);
        Assert.DoesNotContain(nestedResult.Diagnostics, diagnostic => diagnostic.Code == "TW2012");

        var path = Compile("""
            === gate
            {image("{guide}", "Alt")}
            -> END
            """);
        var missing = Assert.Single(path.Errors);
        Assert.Equal("TW2012", missing.Code);
        Assert.Contains("{guide}", missing.Message);
        Assert.DoesNotContain("Bea", missing.Message);
    }

    [Fact]
    public void Character_named_image_can_still_be_substituted_in_alt_text()
    {
        var story = Story("""
            === gate
            {image("portrait", "{image:first-name}")}
            -> END
            """);
        Assert.Equal("Iris", Assert.Single(Alts(story)));
    }
}
