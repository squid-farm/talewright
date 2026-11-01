using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SlowInputStoriesTests
{
    [Theory]
    [InlineData(SlowInputShape.ParagraphLines, 41_166, "737ABC47E5BD491AA0F45930FF916A0B765B13956083A695A315B121162C8055")]
    [InlineData(SlowInputShape.InlineSubstitutions, 11_167, "30CF5996FC9C085D26B5A461DD961FD870AEBF7DC4884B3C8580985C98C7C062")]
    [InlineData(SlowInputShape.NestedConditions, 22_174, "355D6230101072136C8087BC4CA5F75834D258C064F142B1A7AB75DD2070A8A8")]
    [InlineData(SlowInputShape.UnmatchedEmphasis, 3_167, "E81CA2347132C809944BC1432F3D28036FD137F6D8EF099814BDFC186AAF5E92")]
    [InlineData(SlowInputShape.UnclosedLinks, 4_167, "2D9B68D53911BD882088BA164EC3213076ABD25E7CA24689B7D5BD0145B6B644")]
    [InlineData(SlowInputShape.Choices, 22_049, "053FD46B604D0ACAA1B25F5F15B4F22B5F87323046FDE820FA2FFEFC7FEBA37E")]
    [InlineData(SlowInputShape.ChoicesWithExpressions, 67_049, "E40444CDCF6441CDF91F84224A153CD3F8378EB6D1E254B7160F94A78114DECA")]
    [InlineData(SlowInputShape.NestedParentheses, 2_182, "BC9E630280B9216D3D844B5B8758885957F34B878B851F9D81411299ACCEA390")]
    [InlineData(SlowInputShape.UnaryPrefixes, 1_182, "7C72C05219A6D9588E23CCBFA04EEA4BA81257F40742FEC355B23930E4FF0C97")]
    public void Reference_ladder_preserves_source_bytes(SlowInputShape shape, int expectedBytes, string expectedHash)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var size in new[] { 1, 5, 10, 20, 50, 100, 500, 1_000, 10_000 })
        {
            var source = SlowInputStories.Create(shape, size);
            if (size == 1_000)
            {
                Assert.Equal("""
                    game:
                      id: slow-input
                      title: Slow input
                      start: start
                      seed: 42
                    characters:
                      player: {first-name: Tester}
                    vars: {x: 1, count: 1}
                    flags: [ready]
                    """, source.Files[StorySource.ManifestFileName]);
                Assert.Equal(expectedBytes, PerformanceSource.Utf8ByteCount(source));
            }

            hash.AppendData(Encoding.UTF8.GetBytes(size.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            foreach (var (path, text) in source.Files.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(path));
                hash.AppendData([0]);
                hash.AppendData(Encoding.UTF8.GetBytes(text));
                hash.AppendData([0]);
            }
        }

        Assert.Equal(expectedHash, Convert.ToHexString(hash.GetHashAndReset()));
    }

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
