using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SpeechBlockTests
{
    private const string Manifest = """
        game: {id: speech-blocks, title: Speech Blocks, start: start}
        characters:
          player: {first-name: Ada}
          guide: {first-name: Bea}
        """;

    [Fact]
    public void Consecutive_speech_lines_continue_in_paragraphs_until_non_speech()
    {
        var story = Compile("""
            === start
            > player: First paragraph.
            > Tuesday: it's my least favorite day.
            >
            > Step 1: Stay low.
            > A line with {player:first-name} and colon: yes.

            Ordinary prose ends the speech.
            > player: A separate speech.
            -> END
            """);

        var blocks = new TemplateRenderer().Render(
            CompilerTests.Templates(story.GetScene(new("start"))),
            GameState.CreateInitial(story.World, 1)).Blocks;

        var firstSpeech = Assert.IsType<RenderedSpeech>(blocks[0]);
        Assert.Equal(2, firstSpeech.Paragraphs.Count);
        Assert.Equal(
            "Ada: First paragraph.\nTuesday: it's my least favorite day.\n\n" +
            "Step 1: Stay low.\nA line with Ada and colon: yes.",
            firstSpeech.ToPlainText());
        Assert.Equal("Ordinary prose ends the speech.", Assert.IsType<RenderedParagraph>(blocks[1]).ToPlainText());
        Assert.Equal("Ada: A separate speech.", Assert.IsType<RenderedSpeech>(blocks[2]).ToPlainText());
    }

    [Fact]
    public void Malformed_speech_after_a_non_speech_line_is_not_a_continuation()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = "=== start\n> player: First.\nOrdinary prose.\n> malformed\n-> END",
        }));

        var diagnostic = Assert.Single(result.Errors, error => error.Code == "TW1007");
        Assert.Equal(4, diagnostic.Location.Line);
    }

    private static Talewright.Talemark.Model.StoryDefinition Compile(string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
