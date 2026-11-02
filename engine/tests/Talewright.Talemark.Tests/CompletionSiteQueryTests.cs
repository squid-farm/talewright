using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class CompletionSiteQueryTests
{
    private const string Manifest = """
        game:
          id: query
          title: Query
          start: start
        characters:
          hero:
            name: Ada Hart
            pronouns: she
        items:
          lantern: { name: lantern }
        flags: [met]
        vars: { gold: 3 }
        triggers:
          - id: rich
            when: "var:gold >= 5 and not flag:met"
            do: set flag:met
        """;

    [Fact]
    public void Query_at_the_start_of_an_expression_returns_its_leading_reference_slot()
    {
        var (analysis, position) = Analyze("=== start\nGold: {|var:gold}\n-> END");

        var site = Assert.Single(analysis.CompletionSitesAt(position), site => site.Text == "var");
        Assert.Equal(position, site.Range.Start);
        Assert.Contains("var:", site.Keywords);
        Assert.Contains(AuthoringSymbolKind.Character, site.Kinds);
    }

    [Fact]
    public void Query_at_the_end_of_an_expression_includes_the_slot_ending_there()
    {
        var (analysis, position) = Analyze("=== start\nGold: {var:gold|}\n-> END");

        var site = Assert.Single(analysis.CompletionSitesAt(position));
        Assert.Equal("gold", site.Text);
        Assert.Equal(position, site.Range.End);
        Assert.Equal([AuthoringSymbolKind.Variable], site.Kinds);
    }

    [Fact]
    public void Query_inside_a_namespaced_reference_returns_the_slot_after_its_prefix()
    {
        var (analysis, position) = Analyze("=== start\n@requires flag:m|et\n-> END");

        var site = Assert.Single(analysis.CompletionSitesAt(position));
        Assert.Equal("met", site.Text);
        Assert.Equal([AuthoringSymbolKind.Flag], site.Kinds);
        Assert.Equal(position.Column - 1, site.Range.Start.Column);
    }

    [Theory]
    [InlineData("@requires @Can.|", "Can.")]
    [InlineData("@requires @Can.Op|", "Can.Op")]
    [InlineData("@requires @Can.Op|en-door", "Can.Open-door")]
    public void Query_keeps_dotted_predicate_prefixes_in_one_slot(string line, string text)
    {
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Can.Open-door", _ => true),
        };
        var (analysis, position) = Analyze($"=== start\n{line}\n-> END", options);

        // Analysis can report the same predicate site twice, as before; completion de-duplicates its items.
        var sites = analysis.CompletionSitesAt(position);
        Assert.NotEmpty(sites);
        Assert.All(sites, site =>
        {
            Assert.Equal(text, site.Text);
            Assert.Equal([AuthoringSymbolKind.Predicate], site.Kinds);
        });
    }

    [Fact]
    public void Query_at_the_boundary_of_adjacent_substitutions_returns_only_the_slots_touching_it()
    {
        var (analysis, position) = Analyze("=== start\n{var:gold}|{flag:met}\n-> END");

        Assert.Empty(analysis.CompletionSitesAt(position));
        var endOfFirst = position with { Column = position.Column - 1 };
        Assert.Equal("gold", Assert.Single(analysis.CompletionSitesAt(endOfFirst)).Text);
        var startOfSecond = position with { Column = position.Column + 1 };
        Assert.Equal("flag", Assert.Single(analysis.CompletionSitesAt(startOfSecond)).Text);
    }

    [Fact]
    public void Query_returns_nothing_for_unknown_files_and_positions_outside_any_slot()
    {
        var (analysis, _) = Analyze("=== start\nPlain prose.|\n-> END");

        Assert.Empty(analysis.CompletionSitesAt(new SourceLocation("missing.tale", 2, 1)));
        Assert.Empty(analysis.CompletionSitesAt(new SourceLocation("main.tale", 2, 3)));
    }

    [Fact]
    public void Repeated_queries_return_equal_sites()
    {
        var (analysis, position) = Analyze("=== start\nGold: {var:go|ld}\n-> END");

        Assert.Equal(Describe(analysis.CompletionSitesAt(position)), Describe(analysis.CompletionSitesAt(position)));
    }

    [Fact]
    public void Indexed_query_matches_a_scan_of_every_site_at_every_position()
    {
        var sources = new[]
        {
            RealisticStory.Create(new RealisticStoryProfile { Chapters = 1 }).ToSource(),
            new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = Manifest.ReplaceLineEndings("\r\n"),
                ["main.tale"] = "=== start\r\n{var:gold}{flag:met} {hero:pronoun#one-many(\"is\", \"are\")}\r\n" +
                    "* [Go {var:gold} (requires: flag:met) { set flag:met } -> start\r\n{var:\r\n-> END",
            }),
        };
        foreach (var source in sources)
        {
            var analysis = StoryAnalyzer.Analyze(source);
            var byLine = analysis.AllCompletionSites()
                .SelectMany(site => Enumerable.Range(site.Range.Start.Line, site.Range.End.Line - site.Range.Start.Line + 1)
                    .Select(line => (Key: (site.Range.Start.File, line), Site: site)))
                .ToLookup(entry => entry.Key, entry => entry.Site);
            var positionsWithSites = 0;
            foreach (var (file, text) in source.Files)
            {
                var lines = text.ReplaceLineEndings("\n").Split('\n');
                for (var line = 1; line <= lines.Length; line++)
                    for (var column = 1; column <= lines[line - 1].Length + 2; column++)
                    {
                        var position = new SourceLocation(file, line, column);
                        var expected = Describe(byLine[(file, line)].Where(site =>
                            Compare(site.Range.Start, position) <= 0 && Compare(position, site.Range.End) <= 0));
                        var actual = Describe(analysis.CompletionSitesAt(position));
                        Assert.True(expected.SequenceEqual(actual), $"{position}\nexpected:\n{string.Join("\n", expected)}\nactual:\n{string.Join("\n", actual)}");
                        if (actual.Length > 0) positionsWithSites++;
                    }
            }
            Assert.True(positionsWithSites > 0);
        }
    }

    private static (StoryAnalysis Analysis, SourceLocation Position) Analyze(string marked, CompilerOptions? options = null)
    {
        var caret = marked.IndexOf('|');
        var lines = marked[..caret].Split('\n');
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = marked.Remove(caret, 1),
        });
        return (StoryAnalyzer.Analyze(source, options), new SourceLocation("main.tale", lines.Length, lines[^1].Length + 1));
    }

    private static string[] Describe(IEnumerable<CompletionSite> sites) => sites.Select(site => string.Join(" | ",
        site.Text, site.Range, string.Join(",", site.Kinds), string.Join(",", site.Keywords),
        string.Join(",", site.Positions), site.FollowingDelimiter, site.Candidates?.Count, site.IsImagePath)).ToArray();

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
