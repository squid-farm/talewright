using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SlowInputStoriesTests
{
    [Theory]
    [InlineData(SlowInputShape.ParagraphLines)]
    [InlineData(SlowInputShape.InlineSubstitutions)]
    [InlineData(SlowInputShape.NestedConditions)]
    [InlineData(SlowInputShape.UnmatchedEmphasis)]
    [InlineData(SlowInputShape.UnclosedLinks)]
    [InlineData(SlowInputShape.Choices)]
    [InlineData(SlowInputShape.ChoicesWithExpressions)]
    [InlineData(SlowInputShape.NestedParentheses)]
    [InlineData(SlowInputShape.UnaryPrefixes)]
    public void Small_inputs_are_reproducible_and_analyze_as_one_scene(SlowInputShape shape)
    {
        var source = SlowInputStories.Create(shape, 5);
        var repeated = SlowInputStories.Create(shape, 5);
        var compiled = StoryCompiler.Compile(source);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(source.Files, repeated.Files);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Single(compiled.Story!.Scenes);
        Assert.Single(analysis.Scenes);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(SlowInputShape.ParagraphLines, "{var:x}")]
    [InlineData(SlowInputShape.InlineSubstitutions, "{var:count}")]
    [InlineData(SlowInputShape.NestedConditions, "{if flag:ready}")]
    [InlineData(SlowInputShape.UnmatchedEmphasis, "*a ")]
    [InlineData(SlowInputShape.UnclosedLinks, "[a](")]
    [InlineData(SlowInputShape.Choices, "* [Choice ")]
    [InlineData(SlowInputShape.ChoicesWithExpressions, "* [Choice ")]
    public void Generated_input_contains_the_requested_number_of_shapes(SlowInputShape shape, string marker)
    {
        const int size = 10_000;
        var tale = SlowInputStories.Create(shape, size).Files["main.tale"];
        Assert.Equal(size, tale.Split(marker, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Recursive_expression_shapes_can_be_generated_without_parsing_them()
    {
        const int depth = 10_000;
        var parentheses = SlowInputStories.Create(SlowInputShape.NestedParentheses, depth).Files["main.tale"];
        var unary = SlowInputStories.Create(SlowInputShape.UnaryPrefixes, depth).Files["main.tale"];

        Assert.Contains(new string('(', depth) + "1" + new string(')', depth), parentheses);
        Assert.Contains(new string('-', depth) + "1", unary);
    }

    [Fact]
    public void Choice_ladder_reaches_the_requested_number_of_parsed_choices()
    {
        var result = StoryCompiler.Compile(SlowInputStories.Create(SlowInputShape.Choices, 1_000));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(1_000, Assert.Single(result.Story!.Scenes).Choices.Count);
    }

    [Fact]
    public void Expression_choice_ladder_exercises_per_choice_analysis()
    {
        var analysis = StoryAnalyzer.Analyze(SlowInputStories.Create(SlowInputShape.ChoicesWithExpressions, 20));

        Assert.Equal(20, Assert.Single(analysis.Scenes).Definition.Choices.Count);
        Assert.Equal(20, analysis.Expressions.Count(expression => expression.Kind == ExpressionContextKind.Condition));
        Assert.Equal(20, analysis.Expressions.Count(expression => expression.Kind == ExpressionContextKind.Effects));
    }

    [Fact]
    public void Paragraph_ladder_contains_parsed_substitutions_and_inline_conditions()
    {
        var analysis = StoryAnalyzer.Analyze(SlowInputStories.Create(SlowInputShape.ParagraphLines, 20));

        Assert.Equal(20, analysis.Substitutions.Count);
        Assert.Equal(20, analysis.Expressions.Count(expression => expression.Kind == ExpressionContextKind.Condition));
    }

    [Fact]
    public void Invalid_shapes_and_sizes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SlowInputStories.Create(SlowInputShape.Choices, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlowInputStories.Create((SlowInputShape)(-1), 1));
    }

    [Fact]
    public void Source_bytes_include_manifest_and_utf8_text_but_not_paths_or_images()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = "é",
            ["main.tale"] = "a🙂",
        }, ["images/example.png"]);

        Assert.Equal(7, PerformanceSource.Utf8ByteCount(source));
    }
}
