using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class ChoiceDiagnosticsTests
{
    private const string Manifest = """
        game: {id: choice-diagnostics, title: Choice diagnostics, start: start}
        characters:
          player: {first-name: Ada}
        flags: [ready, left]
        vars: {count: 0}
        """;

    private static CompilationResult Compile(string tale, IEnumerable<string>? images = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = tale,
        }, images));

    [Fact]
    public void Requires_and_effects_are_accepted_in_either_order_and_once_each()
    {
        var result = Compile("""
            === start
            * [Read] (requires: flag:ready) { set flag:left } -> END
            * [Shove] { set flag:left } (requires: flag:ready) -> END
            * (requires: flag:ready) { set flag:left } -> END
            * { set flag:left } (requires: flag:ready) -> END
            * -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(5, result.Story!.Scenes[0].Choices.Count);
        Assert.All(result.Story.Scenes[0].Choices.Take(4), choice =>
        {
            Assert.NotNull(choice.Requires);
            Assert.Single(choice.Effects);
        });
    }

    [Theory]
    [InlineData("* [Go -> hall] -> END")]
    [InlineData("* [Say \"hello\" (quietly)] -> END")]
    [InlineData("* [Open [the] door \\] {var:count}] (requires: (flag:ready)) -> END")]
    public void Closed_labels_keep_reader_text_delimiters(string line)
    {
        var result = Compile("=== start\n" + line + "\n* -> END");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("* [Go] (requires: flag:ready) (requires: flag:left) -> END", "(requires:", "Duplicate '(requires:)'")]
    [InlineData("* [Go] { set flag:left } { set flag:ready } -> END", "{", "Duplicate effect block")]
    public void Duplicate_clauses_are_rejected_at_the_second_occurrence(string line, string marker, string message)
    {
        var result = Compile("=== start\n" + line);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1011");
        var second = line.IndexOf(marker, line.IndexOf(marker, StringComparison.Ordinal) + marker.Length, StringComparison.Ordinal);

        Assert.False(result.Success);
        Assert.Contains(message, diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, second + 1), diagnostic.Location);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW3002");
    }

    [Theory]
    [InlineData("* [Go] (requires: flag:ready) (requires: flag:left", "Duplicate '(requires:)'", "missing its closing ')'")]
    [InlineData("* [Go] { set flag:left } { set flag:ready", "Duplicate effect block", "missing its closing '}'")]
    public void Duplicate_incomplete_clauses_keep_both_relevant_diagnostics(string line, string duplicate, string unclosed)
    {
        var diagnostics = Compile("=== start\n" + line).Diagnostics;

        Assert.Equal(["TW1011", "TW1011"], diagnostics.Select(item => item.Code));
        Assert.Contains(duplicate, diagnostics[0].Message);
        Assert.Contains(unclosed, diagnostics[1].Message);
    }

    [Theory]
    [InlineData("* [Go -> END", "->")]
    [InlineData("* [Hello {player:first-name} (requires: flag:ready)", "(requires:")]
    [InlineData("* [Go { set flag:left", "{")]
    [InlineData("* [Go -> hall -> END", null)]
    [InlineData("* [Say \"-> hall\"", null)]
    [InlineData("* [Go [-> hall]", null)]
    [InlineData("* [Go [unfinished -> END", null)]
    [InlineData("* [Go { set flag:left -> END", null)]
    [InlineData("* [Go { set flag:left } -> END", null)]
    [InlineData("* [Go (requires: flag:ready { set flag:left", null)]
    [InlineData("* [Go to the", null)]
    [InlineData("* [Hello {player:first-name}", null)]
    [InlineData("* [Hello {player:first-name", null)]
    [InlineData("* [Hello {player:first-name] -> END", null)]
    public void Missing_bracket_hints_only_for_one_clear_boundary(string line, string? marker)
    {
        var diagnostic = Assert.Single(Compile("=== start\n" + line).Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('[') + 1), diagnostic.Location);
        Assert.StartsWith("Choice label is missing its closing ']'.", diagnostic.Message);
        if (marker is null)
            Assert.DoesNotContain("Place ']'", diagnostic.Message);
        else
            Assert.Contains($"Place ']' before '{marker}'", diagnostic.Message);
    }

    [Theory]
    [InlineData("( requires :")]
    [InlineData("(requires :")]
    [InlineData("( requires:")]
    public void Requires_whitespace_names_the_exact_marker(string spelling)
    {
        var line = $"* [Go] {spelling} flag:ready) -> END";
        var diagnostic = Assert.Single(Compile("=== start\n" + line).Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Contains("exact marker '(requires:)'", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('(') + 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("* [Go] (requires: flag:ready", "'(requires:' is missing its closing ')'.")]
    [InlineData("* [Go] { set flag:ready", "Effect block is missing its closing '}'.")]
    public void Incomplete_choice_clauses_report_only_their_own_error(string line, string message)
    {
        var diagnostic = Assert.Single(Compile("=== start\n" + line).Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Equal(message, diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOfAny(['(', '{']) + 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("* [Go] hall", true)]
    [InlineData("* hall", true)]
    [InlineData("* [Go] hall extra", false)]
    public void Bare_targets_get_a_divert_hint(string line, bool expectedHint)
    {
        var diagnostic = Assert.Single(Compile("=== start\n" + line).Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Equal(expectedHint, diagnostic.Message.Contains("Insert '->' before the target.", StringComparison.Ordinal));
    }

    [Fact]
    public void Unexpected_text_excerpts_fit_eighty_utf16_units_without_splitting_surrogates()
    {
        var emoji = char.ConvertFromUtf32(0x1F991);
        var inputs = new[] { new string('a', 81) + "!", new string('字', 90), new string('a', 78) + emoji + "ZZZZ" };
        foreach (var input in inputs)
        {
            var diagnostic = Assert.Single(Compile("=== start\n* [Go] " + input).Diagnostics);
            var excerpt = diagnostic.Message.Split('\'')[1];

            Assert.Equal("TW1011", diagnostic.Code);
            Assert.InRange(excerpt.Length, 1, 80);
            Assert.EndsWith("…", excerpt);
            Assert.False(char.IsHighSurrogate(excerpt[^2]));
            Assert.False(char.IsLowSurrogate(excerpt[^2]));
        }

        var exact = new string('a', 79) + "!";
        Assert.Contains($"Unexpected '{exact}'", Assert.Single(Compile("=== start\n* [Go] " + exact).Diagnostics).Message);
    }

    [Theory]
    [InlineData("* [Go] -> END { set flag:left }", "{", "Effect block is misplaced after the divert.")]
    [InlineData("* [Go] -> END (requires: flag:ready)", "(requires:", "'(requires:)' is misplaced after the divert.")]
    [InlineData("* [Go] -> END junk", "junk", "Unexpected 'junk' after the divert.")]
    [InlineData("* [Go] -> END{ set flag:left }", "{", "Effect block is misplaced after the divert.")]
    public void Trailing_choice_text_is_not_folded_into_the_target(string line, string marker, string message)
    {
        var result = Compile("=== start\n" + line);
        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.StartsWith(message, diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf(marker, StringComparison.Ordinal) + 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("* [Go] -> END ( requires : flag:ready)", "divert")]
    [InlineData("* [Go] skill-check athletics dc 1 ( requires : flag:ready)", "skill check")]
    public void Misplaced_spaced_requires_names_both_the_marker_and_terminal(string line, string terminal)
    {
        var outcomes = terminal == "skill check" ? "\nsuccess: -> END\nfailure: -> END" : "";
        var diagnostic = Assert.Single(Compile("=== start\n" + line + outcomes).Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Contains("exact marker '(requires:)'", diagnostic.Message);
        Assert.Contains($"cannot follow the {terminal}", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('(') + 1), diagnostic.Location);
    }

    [Fact]
    public void Unknown_divert_target_is_still_checked_before_the_trailing_clause()
    {
        const string line = "* [Go] -> missing { set flag:left }";
        var result = Compile("=== start\n" + line);

        Assert.Contains(result.Diagnostics, item => item.Code == "TW2002" &&
            item.Location == new SourceLocation("main.tale", 2, line.IndexOf("missing", StringComparison.Ordinal) + 1));
        Assert.Contains(result.Diagnostics, item => item.Code == "TW1011" &&
            item.Location == new SourceLocation("main.tale", 2, line.IndexOf('{') + 1));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1008");
    }

    [Theory]
    [InlineData("* [Try] skill-check athletics dc 14 { set flag:left }", "{", "Effect block is misplaced")]
    [InlineData("* [Try] skill-check athletics dc 14 -> END", "->", "Divert is misplaced")]
    [InlineData("* [Try] skill-check athletics dc 14 (requires: flag:ready)", "(requires:", "'(requires:)' is misplaced")]
    public void Skill_check_dc_stops_at_a_top_level_clause(string line, string marker, string message)
    {
        var result = Compile("=== start\n" + line + "\nsuccess: -> END\nfailure: -> END");
        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Contains(message, diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf(marker, StringComparison.Ordinal) + 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("* [Try] skill-check athletics dc 14 junk", "TW1001")]
    [InlineData("* [Try] skill-check athletics dc \"a -> b\"", "TW2003")]
    [InlineData("* [Try] skill-check athletics dc (14 -> 2)", "TW1001")]
    public void Skill_check_expression_errors_are_not_misclassified_as_trailing_clauses(string line, string code)
    {
        var result = Compile("=== start\n" + line + "\nsuccess: -> END\nfailure: -> END");

        Assert.Contains(result.Diagnostics, item => item.Code == code);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1011");
    }

    [Fact]
    public void Unclosed_outcome_effect_is_tw1009_without_a_false_missing_line()
    {
        const string tale = """
            === start
            * [Try] skill-check athletics dc 1
            success: { set flag:ready
            failure: -> END
            === other
            Stuck.
            """;
        var result = Compile(tale);
        var outcome = Assert.Single(result.Diagnostics, item => item.Code == "TW1009");

        Assert.Equal("Outcome effect block is missing its closing '}'.", outcome.Message);
        Assert.Equal(new SourceLocation("main.tale", 3, 10), outcome.Location);
        Assert.Contains(result.Diagnostics, item => item.Code == "TW3002" && item.Message.Contains("'other'"));
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("needs both"));
    }

    [Fact]
    public void Block_checks_preserve_outcome_and_trailing_clause_diagnostics()
    {
        const string block = """
            === start
            ::: skill-check athletics dc 1 { set flag:left }
            success: { set flag:ready
            failure: -> END
            :::
            -> END
            === other
            Stuck.
            """;
        var result = Compile(block);
        Assert.Contains(result.Diagnostics, item => item.Code == "TW1011" &&
            item.Message.Contains("after the skill check") && item.Location.Line == 2);
        var outcome = Assert.Single(result.Diagnostics, item => item.Code == "TW1009");
        Assert.Equal(new SourceLocation("main.tale", 3, 10), outcome.Location);
        Assert.DoesNotContain(result.Diagnostics, item => item.Message.Contains("needs both"));
        Assert.Contains(result.Diagnostics, item => item.Code == "TW3002" && item.Message.Contains("'other'"));
    }

    [Theory]
    [InlineData("(requires: flag:ready)", "'(requires:)' is misplaced after the block skill check.")]
    [InlineData("( requires : flag:ready)", "exact marker '(requires:)'")]
    public void Block_checks_do_not_suggest_putting_choice_conditions_before_skill_check(
        string condition, string expectedMessage)
    {
        var line = $"::: skill-check athletics dc 1 {condition}";
        var result = Compile($"=== start\n{line}\nsuccess: -> END\nfailure: -> END\n:::");
        var diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal("TW1011", diagnostic.Code);
        Assert.Contains(expectedMessage, diagnostic.Message);
        Assert.DoesNotContain("before 'skill-check'", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, line.IndexOf('(') + 1), diagnostic.Location);
    }

    [Fact]
    public void Missing_outcome_lines_and_invalid_outcome_expressions_keep_their_own_errors()
    {
        var missing = Compile("=== start\n* [Try] skill-check athletics dc 1\nsuccess: -> END");
        Assert.Contains(missing.Diagnostics, item => item.Code == "TW1009" && item.Message.Contains("needs both"));

        var invalid = Compile("""
            === start
            * [Try] skill-check athletics dc 1
            success: { var:count += } -> END
            failure: -> END
            """);
        Assert.Contains(invalid.Diagnostics, item => item.Code == "TW1001");
        Assert.DoesNotContain(invalid.Diagnostics, item => item.Code == "TW1009");
    }

    [Fact]
    public void Dropping_a_malformed_choice_does_not_invent_a_dead_end_in_its_scene()
    {
        var result = Compile("=== start\nHello.\n* [Go -> END\n=== other\nStuck.");

        Assert.Contains(result.Diagnostics, item => item.Code == "TW1011");
        var deadEnd = Assert.Single(result.Diagnostics, item => item.Code == "TW3002");
        Assert.Contains("'other'", deadEnd.Message);
        Assert.Contains(Compile("=== start\nHello.").Diagnostics, item => item.Code == "TW3002");
    }

    [Theory]
    [InlineData("false", false, true)]
    [InlineData("true", true, true)]
    [InlineData("true", false, false)]
    [InlineData("false", true, false)]
    public void An_unreachable_malformed_choice_does_not_hide_a_real_dead_end(
        string condition, bool inElse, bool expectedDeadEnd)
    {
        var branch = inElse ? "{else}\n* [unfinished" : "* [unfinished";
        var result = Compile($"=== start\n{{if {condition}}}\n{branch}\n{{/if}}\nStuck.");

        Assert.Contains(result.Diagnostics, item => item.Code == "TW1011");
        Assert.Equal(expectedDeadEnd, result.Diagnostics.Any(item => item.Code == "TW3002"));
    }

    [Fact]
    public void Choice_labels_keep_quoted_string_and_image_diagnostics()
    {
        const string badString = """* [{"bad\q"}] -> END""";
        const string unclosedString = """* [{"bad\q""";
        Assert.Contains(Compile("=== start\n" + badString).Diagnostics, item => item.Code == "TW1001");
        var incomplete = Compile("=== start\n" + unclosedString);
        Assert.Contains(incomplete.Diagnostics, item => item.Code == "TW1001");
        Assert.Contains(incomplete.Diagnostics, item => item.Code == "TW1011");

        const string nested = """* [Go {image("portrait", "See {image(\"coin\", \"A coin\")} now")}] -> END""";
        var images = Compile("=== start\n" + nested, ["images/portrait.png", "images/coin.png"]);
        var diagnostic = Assert.Single(images.Diagnostics);
        Assert.Equal("TW1014", diagnostic.Code);
        Assert.Contains("cannot contain {image(...)}", diagnostic.Message);

        const string unclosedImage = """* [View {image("bad\q""";
        var incompleteImage = Compile("=== start\n" + unclosedImage);
        Assert.Equal(["TW1014", "TW1011"], incompleteImage.Diagnostics.Select(item => item.Code));
        Assert.Equal(new SourceLocation("main.tale", 2, unclosedImage.IndexOf('\\') + 1),
            incompleteImage.Diagnostics[0].Location);
    }

    [Fact]
    public void Missing_bracket_classification_and_scanning_have_linear_work()
    {
        var inputs = new[]
        {
            "[" + new string('x', 100_000),
            "[" + string.Concat(Enumerable.Repeat("[x]", 10_000)),
            "[" + string.Concat(Enumerable.Repeat("{var:count}", 9_000)) + " -> END",
            "[" + string.Concat(Enumerable.Repeat("{var:count}", 9_000)) + " {var:count " + new string('x', 10_000),
            "[" + string.Concat(Enumerable.Repeat("\\[", 50_000)) + " -> END",
        };
        foreach (var input in inputs)
        {
            var scan = DelimiterScanner.Scan(input, DelimiterScanMode.ChoiceLabel);
            var hint = ChoiceSyntax.FindMissingBracketHint(input, 0, scan, CancellationToken.None);
            var repeated = ChoiceSyntax.FindMissingBracketHint(input, 0, scan, CancellationToken.None);

            Assert.Equal(hint, repeated);
            Assert.InRange(scan.WorkUnits + hint.WorkUnits, 0, 4 * input.Length);
        }
    }

    [Fact]
    public void Delimiter_scanning_and_hint_classification_honor_cancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DelimiterScanner.Scan("[unfinished", DelimiterScanMode.ChoiceLabel, cancellationToken: source.Token));
        var scan = DelimiterScanner.Scan("[unfinished", DelimiterScanMode.ChoiceLabel);
        Assert.Throws<OperationCanceledException>(() =>
            ChoiceSyntax.FindMissingBracketHint("[unfinished", 0, scan, source.Token));
        Assert.Throws<OperationCanceledException>(() =>
            ChoiceSyntax.TryReadSpacedRequires("( requires : flag:ready)", 0, source.Token, out _));
        var codeScan = DelimiterScanner.Scan("{", DelimiterScanMode.CodeGroup);
        Assert.Throws<OperationCanceledException>(() =>
            ChoiceSyntax.FindTrailingClause("{", 0, codeScan, 0, source.Token));
    }
}
