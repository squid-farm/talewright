using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class CharacterSheetVisibilityManifestTests
{
    [Fact]
    public void Compiles_boolean_and_condition_rules_with_visible_omitted_stats()
    {
        var story = Compile("""
              str: false
              dex: flag:known
              hp: player:dex >= 10
            """, flags: "flags: { known: true }");
        var state = GameState.CreateInitial(story.World, 42);

        Assert.False(story.IsStatVisible(new StatId("str"), state));
        Assert.True(story.IsStatVisible(new StatId("dex"), state));
        Assert.False(story.IsStatVisible(new StatId("hp"), state));
        Assert.True(story.IsStatVisible(new StatId("wis"), state));
    }

    [Fact]
    public void Shown_false_hides_all_stats_even_when_per_stat_rule_is_true()
    {
        var story = Compile("""
              shown: false
              str: true
            """);

        Assert.False(story.CharacterSheet.Stats);
        Assert.False(story.IsStatVisible(new StatId("str"), GameState.CreateInitial(story.World, 42)));
    }

    [Fact]
    public void Scalar_stats_value_is_rejected()
    {
        var result = CompileSource("false", scalarStats: true);

        Assert.Null(result.Story);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.InvalidConfigurationValue &&
            diagnostic.Message.Contains("character-sheet.stats", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_and_disabled_stat_ids_are_diagnosed()
    {
        var result = CompileSource("""
              wis: true
              mysterious: false
            """, ruleset: """
                base: rpg
                disable: [wis]
            """);

        Assert.Null(result.Story);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.UnknownConfigurationReference &&
            diagnostic.Message.Contains("Unknown stat 'wis'", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.UnknownConfigurationReference &&
            diagnostic.Message.Contains("Unknown stat 'mysterious'", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_condition_and_missing_ruleset_are_diagnosed()
    {
        var invalidCondition = CompileSource("dex: \"player:dex >=\"");
        Assert.Contains(invalidCondition.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.InvalidExpression);

        var missingRuleset = CompileSource("str: true", ruleset: "none");
        Assert.Contains(missingRuleset.Diagnostics, diagnostic =>
            diagnostic.Code == CompilerDiagnosticCodes.UnknownConfigurationReference &&
            diagnostic.Message.Contains("ruleset: rpg", StringComparison.Ordinal));
    }

    private static Talewright.Talemark.Model.StoryDefinition Compile(string rules, string flags = "")
    {
        var result = CompileSource(rules, flags);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static CompilationResult CompileSource(string rules, string flags = "", string ruleset = "rpg",
        bool scalarStats = false)
    {
        var rulesetYaml = ruleset.Contains('\n')
            ? "ruleset:\n" + Indent(ruleset, 4)
            : $"ruleset: {ruleset}";
        var statsYaml = scalarStats ? $"    stats: {rules}" : $"    stats:\n{Indent(rules, 6)}";
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                game:
                  id: visibility
                  title: Visibility
                  start: beginning
                  {{rulesetYaml}}
                  character-sheet:
                {{statsYaml}}
                characters:
                  player:
                    first-name: Ada
                    stats: { dex: 9 }
                {{flags}}
                """,
            ["main.tale"] = "=== beginning\nHello.\n-> END\n",
        });
        return StoryCompiler.Compile(source);
    }

    private static string Indent(string value, int spaces) =>
        string.Join('\n', value.Split('\n').Select(line => new string(' ', spaces) + line));
}
