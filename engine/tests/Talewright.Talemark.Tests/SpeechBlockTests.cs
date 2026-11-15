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

    [Fact]
    public void Speech_keeps_indentation_after_one_optional_space_and_trims_line_ends()
    {
        var story = Compile("=== start\n  > guide:  \tHello  there \t\n" +
                            "\t> \tNext   line  \n>no delimiter\n>\tTab without space\n>   \n" +
                            ">   Another paragraph\n\n> guide:\tTab after colon\n  -> END");
        var blocks = new TemplateRenderer().Render(CompilerTests.Templates(story.GetScene(new("start"))),
            GameState.CreateInitial(story.World, 1)).Blocks;

        var speech = Assert.IsType<RenderedSpeech>(blocks[0]);
        Assert.Equal([" \tHello  there\n\tNext   line\nno delimiter\n\tTab without space", "  Another paragraph"],
            speech.Paragraphs.Select(RenderedInline.ToPlainText));
        var next = Assert.IsType<RenderedSpeech>(blocks[1]);
        Assert.Equal("\tTab after colon", RenderedInline.ToPlainText(Assert.Single(next.Paragraphs)));
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Consecutive_bare_continuations_preserve_extra_spacing_only_between_paragraphs()
    {
        var story = Compile("=== start\n> player: First.\n>\n>\n>\n> Second.\n>\n>\n-> END");
        var block = Assert.Single(new TemplateRenderer().Render(
            CompilerTests.Templates(story.GetScene(new("start"))),
            GameState.CreateInitial(story.World, 1)).Blocks);
        var speech = Assert.IsType<RenderedSpeech>(block);
        Assert.Equal(4, speech.Paragraphs.Count);
        Assert.All(speech.Paragraphs.Skip(1).Take(2),
            paragraph => Assert.IsType<RenderedBlankLineInline>(Assert.Single(paragraph)));
        Assert.Equal("Ada: First.\n\n\n\nSecond.", speech.ToPlainText());
    }

    [Fact]
    public void Different_speaker_recovery_keeps_delimiter_indentation_and_reports_marker()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = "=== start\n> player: Hello\n  > guide:  \t{unknown}\n-> END",
        }));

        var separator = Assert.Single(result.Errors, error => error.Code == "TW1018");
        Assert.Equal((3, 3), (separator.Location.Line, separator.Location.Column));
        var unknown = Assert.Single(result.Errors, error => error.Code == "TW2001");
        Assert.Equal((3, 15), (unknown.Location.Line, unknown.Location.Column));
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
