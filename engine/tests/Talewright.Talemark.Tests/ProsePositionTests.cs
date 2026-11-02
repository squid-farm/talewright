using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class ProsePositionTests
{
    private static List<(string Text, int Line, int Column, int EndLine, int EndColumn)> Expressions(string tale) =>
        StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string> { ["main.tale"] = tale }))
            .Expressions
            .Where(expression => expression.Kind is ExpressionContextKind.Substitution or ExpressionContextKind.Condition)
            .Select(expression => (expression.Text, expression.Range.Start.Line, expression.Range.Start.Column,
                expression.Range.End.Line, expression.Range.End.Column))
            .ToList();

    [Fact]
    public void Multi_line_crlf_paragraph_with_tabs_maps_each_expression_to_its_own_line()
    {
        var expressions = Expressions("=== start\r\n\tHello {x}\r\n\tthen {if y}a{/if} and {z}\r\n-> END\r\n");

        Assert.Equal([("x", 2, 9, 2, 10), ("y", 3, 11, 3, 12), ("z", 3, 25, 3, 26)], expressions);
    }

    [Fact]
    public void Columns_count_utf16_code_units_outside_the_basic_multilingual_plane()
    {
        var expressions = Expressions("=== start\n\U0001F600 {x} \U0001F600{y}\n-> END\n");

        Assert.Equal([("x", 2, 5, 2, 6), ("y", 2, 11, 2, 12)], expressions);
    }

    [Fact]
    public void Adjacent_substitutions_and_line_end_substitutions_keep_their_own_ranges()
    {
        var expressions = Expressions("=== start\n{a}{b}\nend {c}\n-> END\n");

        Assert.Equal([("a", 2, 2, 2, 3), ("b", 2, 5, 2, 6), ("c", 3, 6, 3, 7)], expressions);
    }

    [Fact]
    public void Expression_after_an_unclosed_brace_maps_through_the_paragraph()
    {
        var expressions = Expressions("=== start\nBefore {x}\nafter {y\n-> END\n");

        Assert.Equal([("x", 2, 9, 2, 10), ("y", 3, 8, 3, 9)], expressions);
    }

    [Fact]
    public void Many_substitutions_on_one_line_each_map_to_their_own_column()
    {
        const int count = 10_000;
        var expressions = Expressions("=== start\n" + string.Concat(Enumerable.Repeat("{v} ", count)) + "\n-> END\n");

        Assert.Equal(count, expressions.Count);
        for (var index = 0; index < count; index++)
            Assert.Equal(("v", 2, 4 * index + 2, 2, 4 * index + 3), expressions[index]);
    }

    [Fact]
    public void Paragraph_lookups_stay_logarithmic_in_line_count()
    {
        const int lines = 10_000;
        var source = new SourceText();
        for (var line = 0; line < lines; line++)
            source.AppendLine("abcd", new SourceLocation("main.tale", line + 2, 3));

        for (var line = 0; line < lines; line++)
            Assert.Equal(new SourceLocation("main.tale", line + 2, 5), source.Locate(line * 5 + 2));

        var perLookup = (int)Math.Ceiling(Math.Log2(lines)) + 1;
        Assert.InRange(source.Probes, 1, (long)lines * perLookup);
    }

    [Fact]
    public void Paragraph_text_is_built_once_until_the_next_append()
    {
        var source = SourceText.Single("one", new SourceLocation("main.tale", 1, 1));

        Assert.Same(source.Text, source.Text);
        source.AppendLine("two", new SourceLocation("main.tale", 2, 1));
        Assert.Equal("one\ntwo", source.Text);
    }
}
