using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class ChoiceDiagnosticsTests
{
    private const string Manifest = """
        game:
          id: choice-diagnostics
          title: Choice diagnostics
          start: start
        characters:
          player: { first-name: Ada }
        items: { weapon.knife: Knife, weapon.sword: Sword, rope: Rope }
        flags: [weapon.known, ready]
        vars: { weapon.kills: 0, count: 0 }
        """;

    private static StorySource Source(string tale) => new(new Dictionary<string, string>
    {
        ["story.talewright.yaml"] = Manifest,
        ["main.tale"] = tale,
        ["other.tale"] = "=== weapon.keep\n-> END\n=== second\n-> END",
    });

    [Theory]
    [InlineData("* [Go to the")]
    [InlineData("* [Hello {player:first-name}")]
    [InlineData("* [Hello {player:first-name")]
    [InlineData("* [Go] (requires: flag:weapon.")]
    [InlineData("* [Go] { set flag:weapon.")]
    [InlineData("* [Go] ( requires : flag:weapon.")]
    public void Partial_choice_analysis_matches_the_compiler_without_scratch_diagnostics(string line)
    {
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Equal(["TW1011"], analysis.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code is "TW1004" or "TW2001" or "TW1001");
    }

    [Fact]
    public void Balanced_substitution_before_a_missing_bracket_stays_available()
    {
        const string line = "* [Hello {player:first-name} -> END";
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Equal(["TW1011"], analysis.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Contains("Place ']'", analysis.Diagnostics[0].Message);
        var expression = Assert.Single(analysis.Expressions, context => context.Kind == ExpressionContextKind.Substitution);
        Assert.Equal("player:first-name", expression.Text);
        Assert.Equal("player:first-name", Slice(source, expression.Range));
    }

    [Theory]
    [InlineData("* [Hello {player:first-name} (requires: flag:ready) { set flag:weapon.known } -> END")]
    [InlineData("* [Hello {player:first-name} -> END { set flag:weapon.known }")]
    [InlineData("* [Hello {player:first-name} { set flag:weapon.known } (requires: flag:ready)")]
    [InlineData("* [Hello {player:first-name} -> second { set flag:weapon.")]
    public void Ambiguous_missing_bracket_analysis_stops_at_the_first_clause(string line)
    {
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.DoesNotContain("Place ']'", Assert.Single(analysis.Diagnostics).Message);
        var expression = Assert.Single(analysis.Expressions);
        Assert.Equal(ExpressionContextKind.Substitution, expression.Kind);
        Assert.Equal("player:first-name", Slice(source, expression.Range));
    }

    [Fact]
    public void Unclosed_label_substitution_is_analyzed_without_a_published_brace_error()
    {
        const string line = "* [Hello {player:first-name";
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        var expression = Assert.Single(analysis.Expressions, context => context.Kind == ExpressionContextKind.Substitution);
        Assert.Equal("player:first-name", expression.Text);
        Assert.Equal("player:first-name", Slice(source, expression.Range));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW1004");
    }

    [Fact]
    public void Unclosed_image_call_keeps_genuine_diagnostics_without_scratch_noise()
    {
        const string line = """* [View {image("bad\q""";
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Equal(["TW1014", "TW1011"], analysis.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW1004");
    }

    [Fact]
    public void Unclosed_image_and_earlier_label_substitutions_are_each_analyzed_once()
    {
        const string line = """* [Hi {player:first-name} {image("portrait", "See {item:weapon.""";
        var source = Source("=== start\n" + line);
        var analysis = StoryAnalyzer.Analyze(source);
        var substitutions = analysis.Expressions.Where(context => context.Kind == ExpressionContextKind.Substitution).ToArray();

        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Equal(["TW1014", "TW1011"], analysis.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(["player:first-name", "item:weapon."], substitutions.Select(context => context.Text));
        Assert.All(substitutions, context => Assert.Equal(context.Text, Slice(source, context.Range)));
        var position = new SourceLocation("main.tale", 2, line.Length + 1);
        Assert.Equal(["weapon.knife", "weapon.sword"],
            StoryCompletion.Complete(analysis, position).Select(item => item.Label));
    }

    [Theory]
    [InlineData("* [Take {item:weapon.|", "weapon.knife,weapon.sword", "weapon.")]
    [InlineData("* [Take {item:weapon.|sword", "weapon.knife,weapon.sword", "weapon.sword")]
    [InlineData("* [Go] (requires: flag:weapon.|", "weapon.known", "weapon.")]
    [InlineData("* [Go] (requires: flag:weapon.|known", "weapon.known", "weapon.known")]
    [InlineData("* [Go] { set flag:weapon.|", "weapon.known", "weapon.")]
    [InlineData("* [Go] { set flag:weapon.|known", "weapon.known", "weapon.known")]
    [InlineData("* [Go] ( requires : flag:weapon.|", "weapon.known", "weapon.")]
    [InlineData("* -> weapon.|", "weapon.keep", "weapon.")]
    [InlineData("* -> weapon.|keep", "weapon.keep", "weapon.keep")]
    public void Dotted_prefixes_in_partial_choices_replace_the_whole_token(string line, string expected, string token)
    {
        var marked = "=== start\n" + line;
        var caret = marked.IndexOf('|');
        var prefix = marked[..caret].Split('\n');
        var source = Source(marked.Remove(caret, 1));
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        var position = new SourceLocation("main.tale", prefix.Length, prefix[^1].Length + 1);
        var completions = StoryCompletion.Complete(analysis, position);

        Assert.Equal(expected.Split(','), completions.Select(item => item.Label));
        var authored = line.Replace("|", "");
        Assert.All(completions, item =>
        {
            Assert.Equal(2, item.Range.Start.Line);
            Assert.Equal(2, item.Range.End.Line);
            Assert.Equal(token, authored[(item.Range.Start.Column - 1)..(item.Range.End.Column - 1)]);
            Assert.Equal(item.Label, item.NewText);
        });
    }

    private static string Slice(StorySource source, SourceRange range)
    {
        var lines = source.Files[range.Start.File].ReplaceLineEndings("\n").Split('\n');
        var start = lines.Take(range.Start.Line - 1).Sum(line => line.Length + 1) + range.Start.Column - 1;
        var end = lines.Take(range.End.Line - 1).Sum(line => line.Length + 1) + range.End.Column - 1;
        return string.Join('\n', lines)[start..end];
    }
}
