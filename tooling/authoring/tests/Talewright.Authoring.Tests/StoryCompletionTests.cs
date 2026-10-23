using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryCompletionTests
{
    private const string Manifest = """
        id: complete
        title: Complete
        start: first
        characters:
          player: {first-name: Ada}
          guide: {first-name: Bea}
        items:
          rope: {name: Rope}
        factions:
          guild: {name: Guild}
        flags: [ready]
        vars: {coins: 0}
        """;

    [Theory]
    [InlineData("-> sec|", "second")]
    [InlineData("* [Go] -> sec|", "second")]
    [InlineData("-> |", "END")]
    [InlineData("> gu|", "guide")]
    [InlineData("@req|", "requires")]
    [InlineData("@requires player:h|", "hp")]
    [InlineData("@requires flag:re|", "ready")]
    [InlineData("@requires var:co|", "coins")]
    [InlineData("@requires faction:gu|", "guild")]
    [InlineData("@requires player has ro|", "rope")]
    [InlineData("@requires visited(sec|", "second")]
    [InlineData("~ give player ro|", "rope")]
    [InlineData("~ give gu|", "guide")]
    [InlineData("~ give |", "guide")]
    [InlineData("@requires   |", "flag:")]
    [InlineData("::: skill-check athletics dc   |", "var:")]
    [InlineData("~ set flag:re|", "ready")]
    [InlineData("{player:first-name#cap|", "capitalize")]
    [InlineData("{gu|", "guide")]
    [InlineData("::: skill-check athletics dc var:co|", "coins")]
    [InlineData("::: skill-check player h|", "hp")]
    [InlineData("::: skill-check player |", "hp")]
    [InlineData("::: skill-check |", "guide")]
    [InlineData("{player:pronoun.su|", "pronoun.subject")]
    [InlineData("{player:first-name# cap|", "capitalize")]
    public void Completes_only_parser_recognized_reference_slots(string line, string expected)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        Assert.Contains(StoryCompletion.Complete(analysis, position), item => item.Label == expected);
    }

    [Theory]
    [InlineData("Normal prose sec|")]
    [InlineData("// -> sec|")]
    [InlineData("=== sec|")]
    [InlineData("@title sec|")]
    [InlineData("@requires \"sec|\" == \"second\"")]
    [InlineData("{unknown:h|}")]
    [InlineData("~ give |")]
    public void Avoids_irrelevant_scene_suggestions(string line)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        Assert.DoesNotContain(StoryCompletion.Complete(analysis, position), item => item.Label == "second");
    }

    [Theory]
    [InlineData("start: sec|", "second")]
    [InlineData("start: |", "second")]
    [InlineData("start: \"sec|ond\"", "second")]
    [InlineData("start: \"\\u0073ec|ond\"", "second")]
    [InlineData("ruleset: {base: gen|}", "generic")]
    [InlineData("ruleset: {disable: [h|]}", "hp")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    inventory: [ro|]", "rope")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    stats: {h|: 10}", "hp")]
    [InlineData("items:\n  rope:\n    name: Rope\n    modifiers: {h|: 1}", "hp")]
    [InlineData("creation:\n  stats: {h|: [10, 15]}", "hp")]
    [InlineData("creation:\n  presets:\n    strong:\n      stats: {h|: 15}", "hp")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    inventory: {ro|: 1}", "rope")]
    [InlineData("triggers:\n  - id: check\n    when: 'flag:re|'\n    do: set flag:ready", "ready")]
    public void Completes_manifest_story_values_and_expressions(string fragment, string expected)
    {
        // Replace duplicate top-level declarations so YAML remains parseable.
        var key = fragment.Split(':')[0];
        var yaml = string.Join('\n', Manifest.Split('\n').TakeWhile(line => line != ""));
        var lines = yaml.Split('\n').ToList();
        var start = lines.FindIndex(line => line.StartsWith(key + ":", StringComparison.Ordinal));
        if (start >= 0)
        {
            var end = start + 1;
            while (end < lines.Count && (lines[end].StartsWith(' ') || lines[end].Length == 0)) end++;
            lines.RemoveRange(start, end - start);
        }
        yaml = string.Join('\n', lines) + "\n" + fragment;
        var caret = yaml.IndexOf('|');
        var text = yaml.Remove(caret, 1);
        var position = Position(StorySource.ManifestFileName, yaml[..caret]);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = text,
            ["main.tale"] = "=== first\n-> second\n=== second\n-> END"
        });
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.True(StoryCompletion.Complete(analysis, position).Any(item => item.Label == expected),
            $"At {position}: {string.Join("; ", analysis.CompletionSites.Select(site => $"{site.Text} {site.Range}"))}");
    }

    [Fact]
    public void Replaces_the_entire_token_and_preserves_utf16_and_crlf_positions()
    {
        var (analysis, position) = Analyze("=== first\r\nA 🦑 {player:first-name#cap|italize}\r\n-> END");
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == "capitalize");
        Assert.Equal(new SourceLocation("main.tale", 2, 25), item.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, 35), item.Range.End);
    }

    [Fact]
    public void Missing_project_metadata_is_explicitly_unavailable()
    {
        var (analysis, position) = Analyze("=== first\n-> sec|");
        analysis = analysis.WithDiagnostic(new(DiagnosticSeverity.Error, "TW5001", "Missing metadata", new(StorySource.ManifestFileName, 1, 1)));
        Assert.Throws<InvalidOperationException>(() => StoryCompletion.Complete(analysis, position));
    }

    [Fact]
    public void Broken_yaml_has_no_fabricated_manifest_completions()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = "start: \"sec",
            ["main.tale"] = "=== second\n-> END"
        });
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.NotEmpty(analysis.Diagnostics);
        Assert.Empty(StoryCompletion.Complete(analysis, new(StorySource.ManifestFileName, 1, 12)));
    }

    [Fact]
    public void Numeric_effect_operands_do_not_offer_new_effect_commands()
    {
        var (analysis, position) = Analyze("=== first\n~ player:hp += |");
        var items = StoryCompletion.Complete(analysis, position);
        Assert.Contains(items, item => item.Label == "var:");
        Assert.DoesNotContain(items, item => item.Label is "set" or "clear" or "give" or "take");
    }

    private static (StoryAnalysis Analysis, SourceLocation Position) Analyze(string marked)
    {
        var caret = marked.IndexOf('|');
        var position = Position("main.tale", marked[..caret]);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = marked.Remove(caret, 1),
            ["other.tale"] = "=== second\n-> END"
        });
        return (StoryAnalyzer.Analyze(source), position);
    }

    private static SourceLocation Position(string file, string prefix)
    {
        var lines = prefix.ReplaceLineEndings("\n").Split('\n');
        return new(file, lines.Length, lines[^1].Length + 1);
    }
}
