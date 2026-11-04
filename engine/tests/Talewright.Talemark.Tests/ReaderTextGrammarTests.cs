using System.Text.Json;
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
    [InlineData("Lost line\n---", "Lost line\n---")]
    [InlineData("Lost line\n-", "Lost line\n-")]
    [InlineData("[Inventory]: empty", "[Inventory]: empty")]
    [InlineData("[the inn][1] is here", "[the inn][1] is here")]
    [InlineData("[Go](https://example.org) now", "[Go](https://example.org) now")]
    [InlineData("![a [b]](p)", "![a [b]](p)")]
    [InlineData("![a](p(q))", "![a](p(q))")]
    [InlineData("An unfinished ![image] remains prose.", "An unfinished ![image] remains prose.")]
    [InlineData(@"\![alt](path)", "![alt](path)")]
    [InlineData("&amp; &copy; &#65;", "&amp; &copy; &#65;")]
    [InlineData("<b>tag</b>, https://example.org", "<b>tag</b>, https://example.org")]
    [InlineData("# Not heading; - not list; 1. not list", "# Not heading; - not list; 1. not list")]
    [InlineData("2*3*4 and snake_case_name", "2*3*4 and snake_case_name")]
    [InlineData("word*inner*word", "word*inner*word")]
    [InlineData("word*内側*word", "word*内側*word")]
    [InlineData("*é*", "[I: é]")]
    [InlineData("*(door)*", "[I: (door)]")]
    [InlineData("*door!*", "[I: door!]")]
    [InlineData("_(door)_", "[I: (door)]")]
    [InlineData("_door!_", "[I: door!]")]
    [InlineData("**(door)**", "[B: (door)]")]
    [InlineData("__door!__", "[B: door!]")]
    [InlineData("**bold**text", "**bold**text")]
    [InlineData("***both***", "***both***")]
    [InlineData("**_combined_**", "[B+I: combined]")]
    [InlineData("__*combined*__", "[B+I: combined]")]
    [InlineData("_**combined**_", "[B+I: combined]")]
    [InlineData("*__combined__*", "[B+I: combined]")]
    [InlineData("**unclosed and *also", "**unclosed and *also")]
    [InlineData("**bold *italic* bold**", "[B: bold ][B+I: italic][B:  bold]")]
    [InlineData("*a **b* c**", "*a [B: b* c]")]
    [InlineData("_under_ and __strong__", "[I: under] and [B: strong]")]
    [InlineData("`code {player:first-name} **x**`", "`code Mara [B: x]`")]
    [InlineData(@"`a \{b\} c`", "`a {b} c`")]
    [InlineData("`` a`b ``", "`` a`b ``")]
    [InlineData(@"\* \_ \[ \] \{ \} \\", "* _ [ ] { } \\")]
    [InlineData(@"\. \# \> \& \= \a \1 \—", @". # > & = \a \1 \—")]
    [InlineData(@"\{player}", "{player}")]
    [InlineData(@"A \{player\} and \*emphasis* stay literal", "A {player} and *emphasis* stay literal")]
    [InlineData(@"\* [Go] -> hall", "* [Go] -> hall")]
    [InlineData(@"\> guide: Hello", "> guide: Hello")]
    [InlineData(@"\= ordinary text", "= ordinary text")]
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
    public void Emphasis_matches_the_shared_editor_grammar_corpus()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "reader-text-emphasis-parity.json");
        var cases = JsonSerializer.Deserialize<EmphasisParityCase[]>(
            File.ReadAllText(fixturePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(cases);

        foreach (var testCase in cases)
        {
            var source = new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = TestStories.DefaultYaml,
                ["main.tale"] = $"=== start\n{testCase.Input}\n-> END",
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

            Assert.Equal(testCase.Expected, actual);
        }
    }

    [Fact]
    public void Unclosed_inline_diagnostic_suggests_an_escaped_brace()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = "=== start\n{unfinished\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        var error = Assert.Single(result.Errors,
            diagnostic => diagnostic.Code == CompilerDiagnosticCodes.UnclosedInlineConstruct);

        Assert.Equal("""Unclosed '{'. Use '\{' for a literal brace.""", error.Message);
    }

    [Fact]
    public void Formatting_and_reader_text_scans_do_linear_work_on_a_mixed_paragraph()
    {
        var input = string.Concat(Enumerable.Repeat(
            "ordinary *prose* with `code` and **strong**.\n", 1_000));
        var result = InlineParser.ParseWithWork(
            SourceText.Single(input, SourceLocation.None),
            Symbols(),
            new DiagnosticBag());

        Assert.InRange(result.WorkUnits, input.Length, 10 * input.Length);
    }

    [Fact]
    public void Rejected_image_text_keeps_its_formatting_boundaries()
    {
        var diagnostics = new DiagnosticBag();
        var inlines = InlineParser.Parse(
            SourceText.Single("**![a](p)** and ![a](p)*after*", SourceLocation.None),
            Symbols(), diagnostics);

        Assert.Equal(2, diagnostics.Items.Count(item => item.Code == CompilerDiagnosticCodes.LegacyImageSyntax));
        var styled = Assert.IsType<StyledTemplate>(inlines[0]);
        Assert.Equal(TextStyle.Strong, styled.Style);
        Assert.Equal("![a](p)", Assert.IsType<TextTemplate>(Assert.Single(styled.Children)).Text);
        Assert.Equal(" and ![a](p)*after*",
            string.Concat(inlines.Skip(1).Select(inline => Assert.IsType<TextTemplate>(inline).Text)));
    }

    [Fact]
    public void Every_ascii_punctuation_character_can_be_escaped_as_reader_text()
    {
        const string punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        foreach (var character in punctuation)
        {
            var diagnostics = new DiagnosticBag();
            var inlines = InlineParser.Parse(
                SourceText.Single("\\" + character, SourceLocation.None),
                Symbols(),
                diagnostics);

            Assert.Empty(diagnostics.Items);
            Assert.Equal(character.ToString(),
                string.Concat(inlines.Select(inline => Assert.IsType<TextTemplate>(inline).Text)));
        }
    }

    [Theory]
    [InlineData("line one\nline two", "line one\nline two")]
    [InlineData("first\n{if false}hidden{/if}\nlast", "first\nlast")]
    [InlineData("{if false}hidden{/if}\nfirst", "first")]
    [InlineData("first\n{if false}hidden{/if}", "first")]
    [InlineData("first\n{if true}shown{/if}\nlast", "first\nshown\nlast")]
    public void Reader_text_line_breaks_render_as_written_except_for_empty_lines(
        string inline, string expected)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = $"=== start\n{inline}\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var scene = result.Story!.GetScene(new SceneId("start"));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene),
            GameState.CreateInitial(result.Story.World, 1));
        var paragraph = Assert.IsType<RenderedParagraph>(Assert.Single(document.Blocks));
        Assert.Equal(expected, string.Concat(paragraph.Inlines.Select(Describe)));
    }

    [Theory]
    [InlineData("line one\\\nline two", 2, 9)]
    [InlineData("line one\\", 2, 9)]
    [InlineData("> player: line\\", 2, 15)]
    public void Unpaired_trailing_backslashes_report_the_replacement(
        string inline, int line, int column)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = $"=== start\n{inline}\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        var error = Assert.Single(result.Errors,
            diagnostic => diagnostic.Code == CompilerDiagnosticCodes.TrailingReaderTextBackslash);

        Assert.Equal("Line breaks now render as written; remove the trailing '\\'.", error.Message);
        Assert.Equal(new SourceLocation("main.tale", line, column), error.Location);
    }

    [Fact]
    public void Paired_trailing_backslashes_render_as_one_literal_backslash()
    {
        const string inline = "line one\\\\";
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = $"=== start\n{inline}\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var scene = result.Story!.GetScene(new SceneId("start"));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene),
            GameState.CreateInitial(result.Story.World, 1));
        var paragraph = Assert.IsType<RenderedParagraph>(Assert.Single(document.Blocks));
        Assert.Equal("line one\\", string.Concat(paragraph.Inlines.Select(Describe)));
    }

    [Theory]
    [InlineData("*", 65)]
    [InlineData("**", 65)]
    [InlineData("_", 65)]
    [InlineData("__", 65)]
    [InlineData("*", 10_000)]
    [InlineData("**", 10_000)]
    [InlineData("_", 10_000)]
    [InlineData("__", 10_000)]
    public void Unmatched_formatting_openers_remain_literal_with_linear_work(string marker, int count)
    {
        var input = string.Concat(Enumerable.Repeat($"{marker}a ", count));
        var diagnostics = new DiagnosticBag();
        var result = InlineParser.ParseWithWork(
            SourceText.Single(input, SourceLocation.None), Symbols(), diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(input, string.Concat(result.Inlines.Select(inline => Assert.IsType<TextTemplate>(inline).Text)));
        Assert.InRange(result.WorkUnits, input.Length, 10 * input.Length);
    }

    [Fact]
    public void Unmatched_openers_do_not_count_toward_matched_formatting_depth()
    {
        var prefix = string.Concat(Enumerable.Repeat("*unclosed ", 100));
        var diagnostics = new DiagnosticBag();
        var result = InlineParser.ParseWithWork(
            SourceText.Single(prefix + "*matched*", SourceLocation.None), Symbols(), diagnostics);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(prefix, string.Concat(result.Inlines.SkipLast(1).Select(inline => Assert.IsType<TextTemplate>(inline).Text)));
        var formatted = Assert.IsType<StyledTemplate>(result.Inlines[^1]);
        Assert.Equal(TextStyle.Emphasis, formatted.Style);
        Assert.Equal("matched", Assert.IsType<TextTemplate>(Assert.Single(formatted.Children)).Text);
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

    private sealed record EmphasisParityCase(string Input, string Expected);

    private static string StyleName(TextStyle style)
    {
        var names = new List<string>(3);
        if (style.HasFlag(TextStyle.Strong)) names.Add("B");
        if (style.HasFlag(TextStyle.Emphasis)) names.Add("I");
        if (style.HasFlag(TextStyle.Code)) names.Add("C");
        return string.Join('+', names);
    }
}
