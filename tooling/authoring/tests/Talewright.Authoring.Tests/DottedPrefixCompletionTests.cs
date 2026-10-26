using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class DottedPrefixCompletionTests
{
    [Theory]
    [InlineData("{item:weapon.|}", "weapon.knife,weapon.sword")]
    [InlineData("{weapon.|}", "weapon.keeper")]
    [InlineData("{faction:weapon.|}", "weapon.knights")]
    [InlineData("{flag:weapon.|}", "weapon.known")]
    [InlineData("{var:weapon.|}", "weapon.kills")]
    [InlineData("{player:weapon.|}", "weapon.knowledge")]
    [InlineData("-> weapon.|", "weapon.keep")]
    public void Dotted_prefix_filters_names_and_replaces_the_whole_token(string line, string expected)
    {
        const string manifest = """
            game:
              id: dotted-completion
              title: Dotted completion
              start: start
              ruleset:
                base: generic
                stats: [{ id: weapon.knowledge, default: 1 }]
            characters:
              player: { first-name: Ada }
              weapon.keeper: { first-name: Bea }
            items: { weapon.knife: Knife, weapon.sword: Sword, rope: Rope }
            factions: { weapon.knights: { name: Knights }, guild: { name: Guild } }
            flags: [weapon.known, ready]
            vars: { weapon.kills: 0, coins: 0 }
            """;
        var marked = "=== start\n" + line + "\n-> END\n=== weapon.keep\n-> END";
        var caret = marked.IndexOf('|');
        var prefix = marked[..caret].Split('\n');
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = marked.Remove(caret, 1),
        });
        var completions = StoryCompletion.Complete(StoryAnalyzer.Analyze(source),
            new SourceLocation("main.tale", prefix.Length, prefix[^1].Length + 1));
        Assert.Equal(expected.Split(','), completions.Select(item => item.Label));
        var authoredLine = line.Replace("|", "");
        Assert.All(completions, item =>
        {
            Assert.Equal(2, item.Range.Start.Line);
            Assert.Equal(2, item.Range.End.Line);
            Assert.Equal("weapon.", authoredLine[(item.Range.Start.Column - 1)..(item.Range.End.Column - 1)]);
            Assert.Equal(item.Label, item.NewText);
        });
    }
}
