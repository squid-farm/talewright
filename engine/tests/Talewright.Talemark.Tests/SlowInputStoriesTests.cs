using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class SlowInputStoriesTests
{
    [Theory]
    [InlineData(SlowInputShape.ParagraphLines, 41_169, "9B670763C32A77A8E2C16581E98D2C3B67EBE8E0C047BBC78620556EC783B4DE")]
    [InlineData(SlowInputShape.InlineSubstitutions, 11_170, "CA20B8280C4FF0FFA5D8AEFF6229CE2F2F645821429260E5E09944E44CEFFCD7")]
    [InlineData(SlowInputShape.NestedConditions, 22_177, "9C1A8F200AE271DB677837F35728DEA8C3FDAA520CE94C8B1FB334AD1630A938")]
    [InlineData(SlowInputShape.UnmatchedEmphasis, 3_170, "D551B35FFF047123539C9F295F67DD360C56B27CF45C5B3B15CC7944B9C12143")]
    [InlineData(SlowInputShape.UnclosedLinks, 4_170, "FB73B0A6B8BDEFE3EA1115BE6D01F141B0A063F448FAEABAD156E87E3EA7A8E1")]
    [InlineData(SlowInputShape.Choices, 22_052, "56E6FD41DBA8B8C98FCE143868A4F026F404AD2B204C1BBFE54EF9665908C94C")]
    [InlineData(SlowInputShape.ChoicesWithExpressions, 66_052, "818E5FFAD1414F187CEA376A2A651C61D210FC4E2C24FA498DDB22EA58F26624")]
    [InlineData(SlowInputShape.NestedParentheses, 2_185, "89095E9A6E6347A175498459DB0238FC6FC05FF4DEABE68F0BCFA87D26E4DD9B")]
    [InlineData(SlowInputShape.UnaryPrefixes, 1_185, "1A29128039B727EE0C4DA92398A8975EB993E4E6441314C1833DB2B83FDD2699")]
    [InlineData(SlowInputShape.BrokenChoiceLines, 60_162, "AAD0244F5AE926E297E40262F48870316E60603607388CD79E297DF41108931F")]
    [InlineData(SlowInputShape.UnclosedChoiceLabel, 1_175, "5533D6ED46329BC506E74B639E4DADF6A80E88791C2493109AA219C0F4E9C110")]
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
                      player:
                        first-name: Tester
                    vars:
                      x: 1
                      count: 1
                    flags:
                    - ready
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

    [Theory]
    [InlineData(SlowInputShape.BrokenChoiceLines, 5)]
    [InlineData(SlowInputShape.UnclosedChoiceLabel, 1)]
    public void Broken_choice_ladders_reproduce_the_expected_diagnostics(SlowInputShape shape, int expectedErrors)
    {
        var source = SlowInputStories.Create(shape, 5);
        var repeated = SlowInputStories.Create(shape, 5);
        var analysis = StoryAnalyzer.Analyze(source);
        var errors = analysis.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.Equal(source.Files, repeated.Files);
        Assert.Single(analysis.Scenes);
        Assert.Equal(expectedErrors, errors.Length);
        Assert.All(errors, error => Assert.Equal("TW1011", error.Code));
    }

    [Fact]
    public void Broken_choice_stress_shapes_reach_ten_thousand_lines_and_label_characters()
    {
        const int size = 10_000;
        const string brokenLine = "* [Go {var:count} (requires flag:a) { set flag:b } -> start";
        var lines = SlowInputStories.Create(SlowInputShape.BrokenChoiceLines, size).Files["main.tale"];
        var label = SlowInputStories.Create(SlowInputShape.UnclosedChoiceLabel, size).Files["main.tale"];

        Assert.Equal(size, lines.Split(brokenLine, StringSplitOptions.None).Length - 1);
        Assert.Contains("* [" + new string('a', size) + " -> start", label);
    }

    [Theory]
    [InlineData(SlowInputShape.BrokenChoiceLines, 10_000)]
    [InlineData(SlowInputShape.UnclosedChoiceLabel, 1)]
    public void Broken_choice_stress_shapes_have_linear_scanner_and_hint_work(
        SlowInputShape shape, int expectedLines)
    {
        var tale = SlowInputStories.Create(shape, 10_000).Files["main.tale"];
        var lines = tale.Split('\n').Where(line => line.StartsWith("* [", StringComparison.Ordinal)).ToArray();
        long work = 0;
        long characters = 0;
        Assert.Equal(expectedLines, lines.Length);
        foreach (var line in lines)
        {
            var open = line.IndexOf('[');
            var scan = DelimiterScanner.Scan(line, DelimiterScanMode.ChoiceLabel, open);
            Assert.False(scan.TryGetClose(open, out _));
            var hint = ChoiceSyntax.FindMissingBracketHint(line, open, scan, CancellationToken.None);
            work += scan.WorkUnits + hint.WorkUnits;
            characters += line.Length - open;
        }

        Assert.InRange(work, 0, 4 * characters);
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
