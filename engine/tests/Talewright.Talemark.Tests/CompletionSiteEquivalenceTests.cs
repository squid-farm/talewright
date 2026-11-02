using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

// Temporary #398 migration proof: on-request sites must equal the eager list at every source position.
public sealed class CompletionSiteEquivalenceTests
{
    private const string ExpressionStory = """
        game:
          id: equivalence
          title: Equivalence
          start: start
        characters:
          hero:
            name: Ada Hart
            pronouns: she
            stats: { might: 2 }
            inventory: [lantern]
        items:
          lantern: { name: lantern }
          coin: { name: coin, plural: coins }
        factions:
          guild: { name: Guild }
        flags: [met, "quoted"]
        vars: { gold: 3 }
        triggers:
          - id: rich
            when: "var:gold >= 5 and not flag:met"
            do: >-
              set flag:met;
              var:gold += 1
          - id: escaped
            when: "has \"lantern\" and visited(start)"
            do: give hero coin 2
        """;

    private const string ExpressionTale = """
        === start ===
        @title Start {hero:first-name}
        @requires flag:met or var:gold > 1
        {hero:pronoun#one-many("is", "are")} {hero:pronoun:possessive-pronoun:determiner}{item:coin#one-many("coin", "coins")}{var:gold}
        hero: "Hello {var:gold} and {faction:guild:name}."
        {if hero:might >= 2 and has lantern}Strong{else}Weak{/if} {item:lan
        @if visited(start) and var:
        You see {image("cover.png", "A {hero:name}")}.
        * [Go {var:gold} (requires: flag:met and hero:might > 1) { set flag:met; give hero lantern; take hero coin 1; var:gold -= 1 } -> next
        * [Broken (requires flag:a) { set flag:b } -> start
        * [Check] { hero:might dc 3 }
          -> next
        @end
        -> END

        === next ===
        {hero:
        {var:gold + 
        {var:gold}{flag:met}
        -> start
        """;

    public static TheoryData<string> Corpora => new() { "fixtures", "expressions", "lost-relic", "realistic", "slow", "no-manifest", "crlf" };

    [Theory]
    [MemberData(nameof(Corpora))]
    public void On_request_sites_match_eager_sites_at_every_position(string corpus)
    {
        foreach (var source in Sources(corpus))
        {
            var analysis = StoryAnalyzer.Analyze(source);
            var byLine = new Dictionary<(string File, int Line), List<CompletionSite>>();
            foreach (var site in analysis.CompletionSites)
                for (var line = site.Range.Start.Line; line <= site.Range.End.Line; line++)
                {
                    if (!byLine.TryGetValue((site.Range.Start.File, line), out var list))
                        byLine.Add((site.Range.Start.File, line), list = []);
                    list.Add(site);
                }
            var checkedPositions = 0;
            var positionsWithSites = 0;
            foreach (var (file, text) in source.Files)
            {
                var lines = text.ReplaceLineEndings("\n").Split('\n');
                for (var line = 1; line <= lines.Length; line++)
                    for (var column = 1; column <= lines[line - 1].Length + 2; column++)
                    {
                        var position = new SourceLocation(file, line, column);
                        var expected = byLine.GetValueOrDefault((file, line), [])
                            .Where(site => Compare(site.Range.Start, position) <= 0 && Compare(position, site.Range.End) <= 0)
                            .Select(Describe).ToArray();
                        var actual = analysis.CompletionSitesAt(position).Select(Describe).ToArray();
                        Assert.True(expected.SequenceEqual(actual),
                            $"{corpus} {position}\nexpected:\n{string.Join("\n", expected)}\nactual:\n{string.Join("\n", actual)}");
                        checkedPositions++;
                        if (actual.Length > 0) positionsWithSites++;
                    }
            }
            Assert.True(checkedPositions > 0);
            Assert.True(positionsWithSites > 0, $"{corpus}: no positions had completion sites.");
        }
    }

    private static IEnumerable<StorySource> Sources(string corpus)
    {
        switch (corpus)
        {
            case "fixtures":
                foreach (var fixture in TestStories.All) yield return fixture.Source();
                break;
            case "expressions":
                yield return new(new Dictionary<string, string>
                {
                    [StorySource.ManifestFileName] = ExpressionStory, ["main.tale"] = ExpressionTale,
                }, ["images/cover.png"]);
                break;
            case "crlf":
                yield return new(new Dictionary<string, string>
                {
                    [StorySource.ManifestFileName] = ExpressionStory.ReplaceLineEndings("\r\n"),
                    ["main.tale"] = ExpressionTale.ReplaceLineEndings("\r\n"),
                });
                break;
            case "no-manifest":
                yield return new(new Dictionary<string, string> { ["main.tale"] = ExpressionTale });
                break;
            case "lost-relic":
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
                yield return StorySource.FromDirectory(Path.Combine(root!.FullName, "samples", "LostRelic"));
                break;
            case "realistic":
                yield return RealisticStory.Create(new RealisticStoryProfile { Chapters = 2 }).ToSource();
                break;
            case "slow":
                foreach (var shape in Enum.GetValues<SlowInputShape>()) yield return SlowInputStories.Create(shape, 5);
                break;
        }
    }

    private static string Describe(CompletionSite site) => string.Join(" | ",
        site.Text, site.Range, string.Join(",", site.Kinds), string.Join(",", site.Keywords),
        string.Join(",", site.Positions), site.FollowingDelimiter,
        site.Candidates is null ? "-" : string.Join(",", site.Candidates),
        site.KeywordDetails is null ? "-" : string.Join(",", site.KeywordDetails), site.IsImagePath);

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
}
