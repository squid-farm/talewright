using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class NestingLimitTests
{
    private static CompilationResult Compile(string tale, string? manifest = null) => StoryCompiler.Compile(new StorySource(
        new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest ?? TestStories.DefaultYaml,
            ["main.tale"] = tale,
        }));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Expressions_accept_the_agreed_nesting_limit(bool condition, bool useParentheses)
    {
        var expression = useParentheses
            ? new string('(', 256) + (condition ? "true" : "1") + new string(')', 256)
            : condition
                ? string.Concat(Enumerable.Repeat("not ", 256)) + "true"
                : new string('-', 256) + "1";
        var tale = condition
            ? $"=== start\n@requires {expression}\n-> END"
            : $"=== start\n~ var:depth = 0\n~ var:depth = {expression}\n-> END";

        var result = Compile(tale);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "TW1016");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Expressions_report_the_first_nesting_level_over_the_limit(bool condition, bool useParentheses)
    {
        var expression = useParentheses
            ? new string('(', 257) + (condition ? "true" : "1") + new string(')', 257)
            : condition
                ? string.Concat(Enumerable.Repeat("not ", 257)) + "true"
                : new string('-', 257) + "1";
        var line = condition ? $"@requires {expression}" : $"~ var:depth = {expression}";
        var tale = condition
            ? $"=== start\n{line}\n-> END"
            : $"=== start\n~ var:depth = 0\n{line}\n-> END";

        var result = Compile(tale);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Expression is nested more than 256 levels deep.", diagnostic.Message);
        var crossingOffset = condition && !useParentheses ? 256 * 4 : 256;
        var sourceLine = condition ? 2 : 3;
        Assert.Equal(new SourceLocation("main.tale", sourceLine,
                line.IndexOf(expression, StringComparison.Ordinal) + crossingOffset + 1),
            diagnostic.Location);
    }

    [Fact]
    public void Expression_nesting_counts_parentheses_and_prefix_operators_together()
    {
        var expression = new string('(', 128) + string.Concat(Enumerable.Repeat("not ", 129)) +
            "true" + new string(')', 128);

        var result = Compile($"=== start\n@requires {expression}\n-> END");
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");

        Assert.Equal("Expression is nested more than 256 levels deep.", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 10 + 128 + (128 * 4) + 1), diagnostic.Location);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Block_conditions_observe_the_depth_limit(int depth, bool expectedSuccess)
    {
        var tale = "=== start\n" + string.Concat(Enumerable.Repeat("{if true}\n", depth)) +
            "inside\n" + string.Concat(Enumerable.Repeat("{/if}\n", depth)) + "-> END";

        var result = Compile(tale);

        Assert.Equal(expectedSuccess, result.Success);
        if (expectedSuccess)
        {
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1016");
        }
        else
        {
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");
            Assert.Equal("Conditions are nested more than 64 levels deep.", diagnostic.Message);
            Assert.Equal(new SourceLocation("main.tale", 66, 1), diagnostic.Location);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1005");
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Inline_conditions_observe_the_depth_limit(int depth, bool expectedSuccess)
    {
        var inline = string.Concat(Enumerable.Repeat("{if true}", depth)) + "inside" +
            string.Concat(Enumerable.Repeat("{/if}", depth));

        var result = Compile($"=== start\n{inline}\n-> END");

        Assert.Equal(expectedSuccess, result.Success);
        if (!expectedSuccess)
        {
            var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");
            Assert.Equal("Conditions are nested more than 64 levels deep.", diagnostic.Message);
            Assert.Equal(new SourceLocation("main.tale", 2, ((depth - 1) * 9) + 1), diagnostic.Location);
        }
    }

    [Fact]
    public void One_hundred_thousand_parentheses_fails_safely_and_compilation_continues()
    {
        var expression = new string('(', 100_000) + "true" + new string(')', 100_000);

        var result = Compile($"=== start\n@requires {expression}\n-> END\n=== after\n@hub-limit 0\n-> END");

        Assert.Contains(result.Diagnostics, item => item.Code == "TW1016");
        Assert.Contains(result.Diagnostics, item => item.Code == "TW1006");
    }

    [Fact]
    public void One_hundred_thousand_block_conditions_are_skipped_linearly_and_recover()
    {
        const int depth = 100_000;
        var tale = "=== start\n" + string.Concat(Enumerable.Repeat("{if true}\n", depth)) +
            string.Concat(Enumerable.Repeat("{/if}\n", depth)) + "-> END";

        var result = Compile(tale);

        Assert.Contains(result.Diagnostics, item => item.Code == "TW1016");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1005");
    }

    [Fact]
    public void Inline_conditionals_report_excess_nesting_and_resume_after_the_closing_marker()
    {
        var nested = string.Concat(Enumerable.Repeat("{if true}", 65)) + "hidden" +
            string.Concat(Enumerable.Repeat("{/if}", 65)) + " visible";
        var result = Compile($"=== start\n{nested}\n-> END");
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");

        Assert.Equal("Conditions are nested more than 64 levels deep.", diagnostic.Message);
        Assert.True(diagnostic.Location.Line == 2);
    }

    [Fact]
    public void Ten_thousand_inline_conditions_are_skipped_without_recursive_folding()
    {
        const int depth = 10_000;
        var inline = string.Concat(Enumerable.Repeat("{if true}", depth)) + "hidden" +
            string.Concat(Enumerable.Repeat("{/if}", depth)) + " visible";

        var result = Compile($"=== start\n{inline}\n-> END");

        Assert.Contains(result.Diagnostics, item => item.Code == "TW1016");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "TW1005");
    }

    [Fact]
    public void Markdig_nesting_failure_becomes_a_located_diagnostic()
    {
        var opens = new string('[', 15_000);
        var closes = new string(']', 15_000);
        var result = Compile($"=== start\nordinary {opens}middle{closes}\n-> END");
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "TW1016");

        Assert.Equal("Markdown is nested too deeply for safe parsing.", diagnostic.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, 1), diagnostic.Location);
    }
}
