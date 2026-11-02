using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class ReaderTextGrammarTests
{
    [Theory]
    [InlineData("Lost line\n---", "Lost line ---")]
    [InlineData("Lost line\n-", "Lost line -")]
    [InlineData("[Inventory]: empty", "[Inventory]: empty")]
    [InlineData("[the inn][1] is here", "[the inn][1] is here")]
    [InlineData("[Go](https://example.org) now", "[Go](https://example.org) now")]
    [InlineData("&amp; &copy; &#65;", "&amp; &copy; &#65;")]
    [InlineData("<b>tag</b>, https://example.org", "<b>tag</b>, https://example.org")]
    [InlineData("# Not heading; - not list; 1. not list", "# Not heading; - not list; 1. not list")]
    [InlineData("2*3*4 and snake_case_name", "2*3*4 and snake_case_name")]
    [InlineData("word*inner*word", "word*inner*word")]
    [InlineData("word*内側*word", "word*内側*word")]
    [InlineData("*é*", "[I: é]")]
    [InlineData("**bold**text", "**bold**text")]
    [InlineData("***both***", "***both***")]
    [InlineData("**unclosed and *also", "**unclosed and *also")]
    [InlineData("**bold *italic* bold**", "[B: bold ][B+I: italic][B:  bold]")]
    [InlineData("*a **b* c**", "*a [B: b* c]")]
    [InlineData("_under_ and __strong__", "[I: under] and [B: strong]")]
    [InlineData("`code {player:first-name} **x**`", "[C: code Mara **x**]")]
    [InlineData(@"`a \{b\} c`", "[C: a {b} c]")]
    [InlineData("`` a`b ``", "[C: a`b]")]
    [InlineData(@"\* \_ \[ \] \{ \} \\", "* _ [ ] { } \\")]
    [InlineData(@"\. \# \> \& \a", @"\. \# \> \& \a")]
    [InlineData("line one\\\nline two", "line one\nline two")]
    [InlineData("She said \"hi\" (quietly) [aside]", "She said \"hi\" (quietly) [aside]")]
    public void Approved_reader_text_corpus_is_preserved(
        string inline, string expected)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = $"=== start\n{inline}\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene),
            GameState.CreateInitial(story.World, 1));
        var paragraph = Assert.IsType<RenderedParagraph>(Assert.Single(document.Blocks));
        var actual = string.Concat(paragraph.Inlines.Select(Describe));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Formatting_and_code_scans_do_linear_work_on_a_mixed_paragraph()
    {
        var input = string.Concat(Enumerable.Repeat(
            "ordinary *prose* with `code` and **strong**.\n", 1_000));
        var result = InlineParser.ParseWithWork(
            SourceText.Single(input, SourceLocation.None),
            Symbols(),
            new DiagnosticBag());

        Assert.InRange(result.WorkUnits, input.Length, 10 * input.Length);
    }

    private static StorySymbols Symbols()
    {
        var options = CompilerOptions.Default;
        var world = new WorldDefinition(
            new RulesetBuilder("reader-text-test").Build(),
            [], [], [], [], new Dictionary<VarId, int>());
        return new StorySymbols
        {
            World = world,
            Flags = new HashSet<FlagId>(),
            Variables = new HashSet<VarId>(),
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
    }

    private static string Describe(RenderedInline inline) => inline switch
    {
        RenderedText { Style: TextStyle.None } text => text.Text,
        RenderedText text => $"[{StyleName(text.Style)}: {text.Text}]",
        RenderedLineBreak => "\n",
        _ => throw new NotSupportedException($"Unexpected rendered inline '{inline.GetType().Name}'."),
    };

    private static string StyleName(TextStyle style)
    {
        var names = new List<string>(3);
        if (style.HasFlag(TextStyle.Strong)) names.Add("B");
        if (style.HasFlag(TextStyle.Emphasis)) names.Add("I");
        if (style.HasFlag(TextStyle.Code)) names.Add("C");
        return string.Join('+', names);
    }
}
