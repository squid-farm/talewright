using System.Text.Json;
using Talewright.Cli;
using Talewright.Authoring;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests;

public sealed class LintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-lint-{Guid.NewGuid():N}");

    public LintTests()
    {
        Directory.CreateDirectory(_root);
        WriteStory("Night_Ferry", "ending.empty_Handed", "-> END");
    }

    private (int Code, string Output, string Error) Run(params string[] options)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(["lint", _root, .. options], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Json_reports_both_rules_and_authored_ranges()
    {
        var result = Run("--format", "json");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal([StoryLintRules.SceneIdStyle, StoryLintRules.StoryIdStyle],
            diagnostics.Select(diagnostic => diagnostic.GetProperty("code").GetString()));
        Assert.Equal(1, diagnostics[0].GetProperty("line").GetInt32());
        Assert.Equal(5, diagnostics[0].GetProperty("column").GetInt32());
        Assert.Equal(24, diagnostics[0].GetProperty("endColumn").GetInt32());
        Assert.Equal("warning", diagnostics[0].GetProperty("severity").GetString());
        Assert.Equal(1, diagnostics[1].GetProperty("line").GetInt32());
        Assert.Equal(5, diagnostics[1].GetProperty("column").GetInt32());
        Assert.Equal(16, diagnostics[1].GetProperty("endColumn").GetInt32());
    }

    [Fact]
    public void Configuration_suppresses_only_the_selected_rule_in_the_selected_file()
    {
        var config = JsonSerializer.Serialize(new
        {
            suppressions = new Dictionary<string, string[]>
            {
                [StorySource.ManifestFileName] = [StoryLintRules.StoryIdStyle],
            },
        });
        File.WriteAllText(Path.Combine(_root, LintConfiguration.FileName), config);

        var result = Run("--format", "json");

        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostic = Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(StoryLintRules.SceneIdStyle, diagnostic.GetProperty("code").GetString());
    }

    [Fact]
    public void Command_line_rule_override_takes_precedence_over_config()
    {
        File.WriteAllText(Path.Combine(_root, LintConfiguration.FileName),
            """{"rules":{"TWL001":"off"}}""");

        var result = Run("--format", "json", "--rule", "TWL001=error");

        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        var overridden = Assert.Single(diagnostics,
            diagnostic => diagnostic.GetProperty("code").GetString() == StoryLintRules.SceneIdStyle);
        Assert.Equal("error", overridden.GetProperty("severity").GetString());
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.GetProperty("code").GetString() == StoryLintRules.StoryIdStyle);
    }

    [Fact]
    public void Command_line_rule_overrides_can_be_repeated()
    {
        var result = Run("--format", "json", "--rule", "TWL001=error", "--rule", "TWL002=error");

        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("error", diagnostic.GetProperty("severity").GetString()));
    }

    [Fact]
    public void Warning_policy_matches_validate()
    {
        Assert.Equal(0, Run("--format", "json").Code);

        var result = Run("--format", "json", "--warnings-as-errors");

        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.All(json.RootElement.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => Assert.Equal("warning", diagnostic.GetProperty("severity").GetString()));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"rules":{"UNKNOWN":"warning"}}""")]
    [InlineData("""{"suppressions":{"../main.tale":["TWL001"]}}""")]
    public void Invalid_configuration_is_an_explicit_nonzero_error(string config)
    {
        File.WriteAllText(Path.Combine(_root, LintConfiguration.FileName), config);

        var result = Run("--format", "json");

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TWCLI003", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Compiler_diagnostics_are_included_once_and_not_replaced_by_lint_findings()
    {
        WriteStory("night-ferry", "bad!id", "-> END");
        var expected = StoryCompiler.Compile(StorySource.FromDirectory(_root)).Diagnostics;

        var result = Run("--format", "json");

        Assert.Equal(1, result.Code);
        using var json = JsonDocument.Parse(result.Output);
        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(expected.Count, diagnostics.Length);
        Assert.Single(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW1008");
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.GetProperty("code").GetString() == StoryLintRules.SceneIdStyle);
    }

    [Fact]
    public void Invalid_format_is_an_explicit_invocation_error()
    {
        var result = Run("--format", "xml");

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("TWCLI001", result.Error);
    }

    [Fact]
    public void Human_output_displays_lint_rule_ids()
    {
        var result = Run();

        Assert.Equal(0, result.Code);
        Assert.Contains("warning TWL001", result.Output);
        Assert.Contains("warning TWL002", result.Output);
        Assert.Contains("Lint succeeded.", result.Output);
        Assert.Empty(result.Error);
    }

    private void WriteStory(string id, string scene, string tale)
    {
        File.WriteAllText(Path.Combine(_root, StorySource.ManifestFileName),
            $"id: {id}\ntitle: Night Ferry\nversion: 1\nstart: {scene}\ncharacters:\n  player: {{first-name: Ada}}\n");
        File.WriteAllText(Path.Combine(_root, "main.tale"), $"=== {scene}\n{tale}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
