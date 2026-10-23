using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryLintTests
{
    private static StorySource Source(string id = "night-ferry", string scene = "ending.empty-handed") => new(
        new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = $"id: {id}\ntitle: Night Ferry\nversion: 1\nstart: {scene}\ncharacters:\n  player: {{first-name: Ada}}\n",
            ["main.tale"] = $"=== {scene}\n-> END",
        });

    [Fact]
    public void Documented_ids_pass_without_lint_findings()
    {
        var result = StoryLintAnalyzer.Analyze(StoryAnalyzer.Analyze(Source()));

        Assert.Empty(result.CompilerDiagnostics);
        Assert.Empty(result.RuleDiagnostics);
    }

    [Fact]
    public void Noncanonical_story_and_scene_ids_report_precise_authored_ranges()
    {
        var source = Source("Night_Ferry", "ending.empty_Handed");
        var result = StoryLintAnalyzer.Analyze(StoryAnalyzer.Analyze(source));

        Assert.Empty(result.CompilerDiagnostics);
        Assert.Collection(result.RuleDiagnostics,
            diagnostic =>
            {
                Assert.Equal(StoryLintRules.SceneIdStyle, diagnostic.Code);
                Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
                Assert.Equal(new SourceRange(new SourceLocation("main.tale", 1, 5),
                    new SourceLocation("main.tale", 1, 24)), diagnostic.Range);
                Assert.Equal("ending.empty_Handed", Slice(source, diagnostic.Range));
            },
            diagnostic =>
            {
                Assert.Equal(StoryLintRules.StoryIdStyle, diagnostic.Code);
                Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
                Assert.Equal(new SourceRange(new SourceLocation(StorySource.ManifestFileName, 1, 5),
                    new SourceLocation(StorySource.ManifestFileName, 1, 16)), diagnostic.Range);
                Assert.Equal("Night_Ferry", Slice(source, diagnostic.Range));
            });
    }

    [Fact]
    public void Quoted_manifest_id_range_excludes_yaml_quotes()
    {
        var initialSource = Source();
        var files = initialSource.Files.ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
            .Replace("id: night-ferry", "id: \"Night_Ferry\"", StringComparison.Ordinal);
        var storySource = new StorySource(files);
        var result = StoryLintAnalyzer.Analyze(StoryAnalyzer.Analyze(storySource));
        var diagnostic = Assert.Single(result.RuleDiagnostics,
            finding => finding.Code == StoryLintRules.StoryIdStyle);

        Assert.Equal("Night_Ferry", Slice(storySource, diagnostic.Range));
    }

    [Fact]
    public void Invalid_scene_ids_do_not_receive_duplicate_style_findings()
    {
        var analysis = StoryAnalyzer.Analyze(Source(scene: "bad!id"));
        var result = StoryLintAnalyzer.Analyze(analysis);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW1008");
        Assert.DoesNotContain(result.RuleDiagnostics, diagnostic => diagnostic.Code == StoryLintRules.SceneIdStyle);
    }

    [Fact]
    public void Configuration_sets_rule_severity_and_file_specific_suppression()
    {
        var configuration = LintConfiguration.Parse("""
            {
              "rules": { "TWL001": "off" },
              "suppressions": { "story.yaml": ["TWL002"] }
            }
            """);

        Assert.Equal(LintSeverity.Off, configuration.SeverityFor(StoryLintRules.SceneIdStyle));
        Assert.Equal(LintSeverity.Warning, configuration.SeverityFor(StoryLintRules.StoryIdStyle));
        Assert.True(configuration.IsSuppressed("story.yaml", StoryLintRules.StoryIdStyle));
        Assert.False(configuration.IsSuppressed("main.tale", StoryLintRules.StoryIdStyle));
    }

    [Fact]
    public void Cli_rule_overrides_take_precedence_over_configuration()
    {
        var configuration = LintConfiguration.Parse("""{"rules":{"TWL001":"off"}}""")
            .WithRuleOverrides(["TWL001=error"]);

        Assert.Equal(LintSeverity.Error, configuration.SeverityFor(StoryLintRules.SceneIdStyle));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"rules":{"UNKNOWN":"warning"}}""")]
    [InlineData("""{"rules":{"TWL001":"info"}}""")]
    [InlineData("""{"suppressions":{"../main.tale":["TWL001"]}}""")]
    public void Invalid_configuration_is_reported(string json)
    {
        Assert.Throws<LintConfigurationException>(() => LintConfiguration.Parse(json));
    }

    private static string Slice(StorySource source, SourceRange range)
    {
        var text = source.Files[range.Start.File].ReplaceLineEndings("\n");
        var lines = text.Split('\n');
        return range.Start.Line == range.End.Line
            ? lines[range.Start.Line - 1][(range.Start.Column - 1)..(range.End.Column - 1)]
            : throw new InvalidOperationException("This test helper expects single-line ranges.");
    }
}
