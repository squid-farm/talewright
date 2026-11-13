using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class ReaderTextEscapeContextTests
{
    [Fact]
    public void Escapes_render_consistently_in_prose_speech_choices_and_image_alt()
    {
        const string punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        var escaped = string.Join(" ", punctuation.Select(character => $@"\{character}"));
        var literal = string.Join(" ", punctuation.ToCharArray());
        var authored = $@"before {escaped} \a \1 \— \ next";
        var expected = $@"before {literal} \a \1 \— \ next";
        var result = Compile($$"""
            === start
            {{authored}}

            > player: {{authored}}

            {image("portrait", "{{authored}}")}

            * [{{authored}}] -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var state = GameState.CreateInitial(story.World, 1);
        var renderer = new TemplateRenderer();
        var document = renderer.Render(CompilerTests.Templates(scene), state);

        Assert.Equal(expected, Assert.IsType<RenderedParagraph>(document.Blocks[0]).ToPlainText());
        var speech = Assert.IsType<RenderedSpeech>(document.Blocks[1]);
        Assert.Equal(expected, RenderedInline.ToPlainText(Assert.Single(speech.Paragraphs)));
        var image = Assert.IsType<RenderedParagraph>(document.Blocks[2]);
        Assert.Equal(expected, Assert.Single(image.Inlines.OfType<RenderedMedia>()).AltText);
        var choice = Assert.Single(scene.Choices);
        Assert.NotNull(choice.Label);
        Assert.Equal(expected, RenderedInline.ToPlainText(renderer.RenderInlines(choice.Label, state)));
    }

    [Fact]
    public void Alt_text_diagnostics_keep_authored_locations_after_escapes()
    {
        const string line = """{image("portrait", "Literal \q \. \\ then {nobody:name}")}""";
        var result = Compile($"=== start\n{line}\n-> END");
        var error = Assert.Single(result.Errors, diagnostic => diagnostic.Code == "TW2001");

        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf("nobody", StringComparison.Ordinal) + 1),
            error.Location);
    }

    private static CompilationResult Compile(string tale) => StoryCompiler.Compile(
        new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = tale,
        }, ["images/portrait.png"]));
}
