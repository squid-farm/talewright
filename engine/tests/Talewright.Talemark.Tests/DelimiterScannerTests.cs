using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class DelimiterScannerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public void Empty_and_single_character_regions_make_progress(string text)
    {
        foreach (var mode in Enum.GetValues<DelimiterScanMode>())
        {
            var result = DelimiterScanner.Scan(text, mode);

            Assert.Empty(result.Pairs);
            Assert.Empty(result.Issues);
            Assert.InRange(result.WorkUnits, 0, text.Length);
        }
    }

    [Fact]
    public void Scan_results_keep_source_offsets_when_scanning_a_region()
    {
        const string text = "ignored {}";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.ReaderText, 8);

        Assert.True(result.TryGetClose(8, out var close));
        Assert.Equal(9, close);
        Assert.Single(result.Pairs);
    }

    [Fact]
    public void Reader_text_scans_inline_braces_but_not_escaped_braces()
    {
        const string text = """Prose "quotes" {image("p}", "alt")} \{literal\}""";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.ReaderText);

        var brace = Assert.Single(result.Pairs, pair => pair.Kind == DelimiterKind.Brace);
        Assert.Equal(text.IndexOf('{'), brace.OpenOffset);
        Assert.Equal(text.IndexOf("} ", StringComparison.Ordinal), brace.CloseOffset);
        Assert.Equal(2, result.Pairs.Count(pair => pair.Kind == DelimiterKind.QuotedString));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Choice_labels_nest_brackets_and_skip_brackets_inside_inline_constructs()
    {
        const string text = """[Open [the] door (quietly); "quotes" {image("p]", "alt")}] tail]""";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.ChoiceLabel);

        Assert.Equal(2, result.Pairs.Count(pair => pair.Kind == DelimiterKind.Bracket));
        Assert.Equal(1, result.Pairs.Count(pair => pair.Kind == DelimiterKind.Brace));
        Assert.Equal(2, result.Pairs.Count(pair => pair.Kind == DelimiterKind.QuotedString));
        Assert.Contains(result.Issues, issue =>
            issue.Kind == DelimiterKind.Bracket && issue.Offset == text.Length - 1 &&
            issue.Issue == DelimiterIssueKind.StrayCloser);
    }

    [Fact]
    public void Choice_labels_skip_only_approved_escaped_delimiters()
    {
        const string text = """[Open \] literal \\ and \[ nested]""";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.ChoiceLabel);

        var bracket = Assert.Single(result.Pairs, pair => pair.Kind == DelimiterKind.Bracket);
        Assert.Equal(0, bracket.OpenOffset);
        Assert.Equal(text.Length - 1, bracket.CloseOffset);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Code_groups_nest_delimiters_and_ignore_quoted_delimiters()
    {
        const string text = "(call(\")]}{}\", ([x])))";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup);

        Assert.Equal(3, result.Pairs.Count(pair => pair.Kind == DelimiterKind.Parenthesis));
        Assert.Equal(1, result.Pairs.Count(pair => pair.Kind == DelimiterKind.Bracket));
        Assert.Equal(1, result.Pairs.Count(pair => pair.Kind == DelimiterKind.QuotedString));
        Assert.Empty(result.Issues);
        Assert.True(result.TryGetClose(0, out var close));
        Assert.Equal(text.Length - 1, close);
        Assert.True(result.TryGetOpen(close, out var open));
        Assert.Equal(0, open);
    }

    [Fact]
    public void Code_groups_report_mismatched_openers_and_stray_closers_once()
    {
        const string text = "([)]";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup);

        Assert.Contains(new DelimiterPair(DelimiterKind.Parenthesis, 0, 2), result.Pairs);
        Assert.Contains(new DelimiterIssue(DelimiterKind.Bracket, 1, DelimiterIssueKind.MismatchedOpener), result.Issues);
        Assert.Contains(new DelimiterIssue(DelimiterKind.Bracket, 3, DelimiterIssueKind.StrayCloser), result.Issues);
        Assert.Equal(text.Length, DispositionOffsets(result).Distinct().Count());
    }

    [Fact]
    public void Code_groups_report_a_mismatched_closer_when_another_group_is_open()
    {
        var result = DelimiterScanner.Scan("(]", DelimiterScanMode.CodeGroup);

        Assert.Contains(new DelimiterIssue(DelimiterKind.Bracket, 1, DelimiterIssueKind.MismatchedCloser), result.Issues);
        Assert.Contains(new DelimiterIssue(DelimiterKind.Parenthesis, 0, DelimiterIssueKind.UnclosedOpener), result.Issues);
    }

    [Fact]
    public void Unclosed_strings_and_openers_are_located_at_their_source_offsets()
    {
        const string text = "(\"unclosed";

        var result = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup);

        Assert.Contains(new DelimiterIssue(DelimiterKind.QuotedString, 1, DelimiterIssueKind.UnclosedOpener), result.Issues);
        Assert.Contains(new DelimiterIssue(DelimiterKind.Parenthesis, 0, DelimiterIssueKind.UnclosedOpener), result.Issues);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public void Seeded_generated_regions_are_deterministic_and_dispose_each_delimiter_once()
    {
        var random = new Random(41_392);
        const string alphabet = "()[]{}abc";
        var text = new string(Enumerable.Range(0, 8_000)
            .Select(_ => alphabet[random.Next(alphabet.Length)])
            .ToArray());

        var first = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup);
        var second = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup);

        Assert.Equal(first.Pairs, second.Pairs);
        Assert.Equal(first.Issues, second.Issues);
        Assert.Equal(first.WorkUnits, second.WorkUnits);
        Assert.Equal(text.Count(IsCodeDelimiter), DispositionOffsets(first).Distinct().Count());
        AssertPairsDoNotCross(first.Pairs);
    }

    [Fact]
    public void Adversarial_shapes_have_linear_work_up_to_one_hundred_thousand_characters()
    {
        var inputs = new[]
        {
            new string('(', 100_000),
            new string(')', 100_000),
            string.Concat(Enumerable.Repeat("([)]", 25_000)),
            new string('(', 50_000) + new string(')', 50_000),
            "\"" + new string(')', 99_998) + "\"",
            string.Concat(Enumerable.Repeat("()", 50_000)),
        };

        foreach (var input in inputs)
        {
            var result = DelimiterScanner.Scan(input, DelimiterScanMode.CodeGroup);

            Assert.InRange(result.WorkUnits, input.Length, 2 * input.Length);
        }
    }

    private static IEnumerable<int> DispositionOffsets(DelimiterScanResult result) =>
        result.Pairs.SelectMany(pair => new[] { pair.OpenOffset, pair.CloseOffset })
            .Concat(result.Issues.Select(issue => issue.Offset));

    private static bool IsCodeDelimiter(char character) => character is '(' or ')' or '[' or ']' or '{' or '}';

    private static void AssertPairsDoNotCross(IReadOnlyList<DelimiterPair> pairs)
    {
        for (var left = 0; left < pairs.Count; left++)
        {
            for (var right = left + 1; right < pairs.Count; right++)
            {
                var first = pairs[left];
                var second = pairs[right];
                Assert.False(first.OpenOffset < second.OpenOffset && second.OpenOffset < first.CloseOffset &&
                    first.CloseOffset < second.CloseOffset);
                Assert.False(second.OpenOffset < first.OpenOffset && first.OpenOffset < second.CloseOffset &&
                    second.CloseOffset < first.CloseOffset);
            }
        }
    }
}
