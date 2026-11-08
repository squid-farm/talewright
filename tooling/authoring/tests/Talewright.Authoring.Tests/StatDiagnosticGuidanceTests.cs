using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Rulesets.Generic;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;

namespace Talewright.Authoring.Tests;

public sealed class StatDiagnosticGuidanceTests
{
    [Fact]
    public void Exported_custom_ruleset_metadata_preserves_guidance_and_existing_declare_stat_fix()
    {
        var options = Options();
        var source = Source("base: none", "=== start\n~ player:spark += 1\n-> END\n");
        var direct = StoryAnalyzer.Analyze(source, options);
        var metadata = AuthoringProjectMetadata.FromJson(AuthoringProjectMetadata.Export(options).ToJson());
        var exported = metadata.Analyze(source);
        var directDiagnostic = Assert.Single(direct.Diagnostics, item => item.Code == "TW2001");
        var exportedDiagnostic = Assert.Single(exported.Diagnostics, item => item.Code == "TW2001");

        Assert.Equal(directDiagnostic.Message, exportedDiagnostic.Message);
        Assert.StartsWith("Unknown stat 'spark'", exportedDiagnostic.Message);
        Assert.Contains("ruleset: astral", exportedDiagnostic.Message);
        var fix = Assert.Single(StoryQuickFixes.ForMissingStat(exported, exportedDiagnostic).Fixes);
        Assert.Equal("Declare stat 'spark'", fix.Title);
    }

    [Theory]
    [InlineData("base: none", "hp", "rpg", false)]
    [InlineData("base: rpg, disable: [hp]", "hp", "rpg", true)]
    [InlineData("base: none", "spark", "astral", false)]
    [InlineData("base: astral, disable: [spark]", "spark", "astral", true)]
    public void Current_file_analysis_retains_ruleset_guidance(
        string ruleset, string stat, string provider, bool disabled)
    {
        var options = Options();
        var source = Source(ruleset, "=== start\n-> END\n");
        var workspace = new StoryWorkspace(source, options);
        var snapshot = StoryAnalyzer.Analyze(source, options);
        var text = $"=== start\n~ player:{stat} += 1\n-> END\n";

        var current = workspace.AnalyzeCurrentFile("main.tale", text, snapshot, CancellationToken.None);

        var diagnostic = Assert.Single(current.Diagnostics, item => item.Code == "TW2001");
        Assert.StartsWith($"Unknown stat '{stat}'", diagnostic.Message);
        Assert.Contains($"'{provider}'", diagnostic.Message);
        if (disabled)
            Assert.Contains("ruleset.disable", diagnostic.Message);
        else
            Assert.Contains($"ruleset: {provider}", diagnostic.Message);
    }

    private static CompilerOptions Options()
    {
        var rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets(), StringComparer.Ordinal)
        {
            ["astral"] = () => new RulesetBuilder("cosmic-core")
                .AddStat(new StatDefinition(new StatId("spark"), "Spark", StatCategory.Custom, 0, 100, 0))
                .WithSkillCheckRule(new D20SkillCheckRule())
                .Build(),
        };
        return new CompilerOptions { Rulesets = rulesets };
    }

    private static StorySource Source(string ruleset, string tale) => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] =
            $"game:\n  id: guidance\n  title: Guidance\n  start: start\n  ruleset: {{{ruleset}}}\n" +
            "characters:\n  player: {first-name: Ada}\n",
        ["main.tale"] = tale,
    });
}
