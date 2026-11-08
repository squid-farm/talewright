using Talewright.Authoring;
using Talewright.Core.Rulesets;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Authoring.Tests;

public sealed class StoryQuickFixesTests
{
    [Theory]
    [InlineData(
        "game:\n  id: quick-fix\n  title: Quick Fix\n  version: 1\n  start: start\ncharacters:\n  player: {first-name: Ada}\n",
        "\nflags:\n  - missing\n")]
    [InlineData(
        "game:\n  id: quick-fix\n  title: Quick Fix\n  version: 1\n  start: start\nflags: [ready]\ncharacters:\n  player: {first-name: Ada}\n",
        ", missing")]
    public void Missing_flag_adds_a_declaration_to_the_root_manifest(string manifest, string newText)
    {
        var analysis = AnalyzeFlag(manifest, "=== start\n{if flag:missing}Never.{/if}\n-> END\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingFlag(analysis, diagnostic);

        var fix = Assert.Single(result.Fixes);
        Assert.Null(result.DisabledReason);
        Assert.Equal("Declare flag 'missing'", fix.Title);
        Assert.Equal(StoryQuickFixKind.TextEdit, fix.Kind);
        Assert.Equal(StorySource.ManifestFileName, fix.File);
        Assert.Equal(newText, fix.NewText);
        Assert.Equal(fix.Range.Start, fix.Range.End);
    }

    [Fact]
    public void Missing_flag_appends_to_a_block_list_without_rewriting_comments_or_crlf()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\nflags:\r\n  # keep this comment\r\n  - ready # keep this too\r\nitems: {}\r\ncharacters:\r\n  player: {first-name: Ada}\r\n";
        var analysis = AnalyzeFlag(manifest, "=== start\r\n{if flag:missing}Never.{/if}\r\n-> END\r\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingFlag(analysis, diagnostic);
        var fix = Assert.Single(result.Fixes);

        Assert.Equal("  - missing\r\n", fix.NewText);
        Assert.Equal(new SourceLocation(StorySource.ManifestFileName, 8, 1), fix.Range.Start);
        Assert.Equal(fix.Range.Start, fix.Range.End);
    }

    [Fact]
    public void Missing_flag_section_preserves_a_missing_final_newline()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}";
        var analysis = AnalyzeFlag(manifest, "=== start\n{if flag:missing}Never.{/if}\n-> END\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var fix = Assert.Single(StoryQuickFixes.ForMissingFlag(analysis, diagnostic).Fixes);

        Assert.Equal("\n\nflags:\n  - missing", fix.NewText);
    }

    [Theory]
    [InlineData("game: {id: x}\n", "true", "\nflags:\n  - 'true'\n")]
    [InlineData("flags: []\n", "false", "'false'")]
    [InlineData("flags: [ready]\n", "Null", ", 'Null'")]
    [InlineData("flags:\n  - ready\n", "yes", "  - yes\n")]
    [InlineData("flags: [ready]\n", "true-ish", ", true-ish")]
    public void Yaml_keyword_spellings_are_quoted_in_every_list_shape(string manifest, string item, string newText)
    {
        var result = ManifestListEditor.Append(manifest, StorySource.ManifestFileName, ManifestVocabulary.Flags, item);

        Assert.Null(result.Reason);
        Assert.Equal(newText, result.Edit!.NewText);
    }

    [Fact]
    public void Applying_a_reserved_flag_fix_declares_it_as_a_string()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\nflags: []\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n{if flag:true}Never.{/if}\n-> END\n";
        var analysis = AnalyzeFlag(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var fix = Assert.Single(StoryQuickFixes.ForMissingFlag(analysis, diagnostic).Fixes);
        var edited = AnalyzeFlag(ApplyInsertion(manifest, fix), story);

        Assert.Contains("flags: ['true']", ApplyInsertion(manifest, fix));
        Assert.Empty(edited.Diagnostics);
    }

    [Fact]
    public void Malformed_yaml_is_not_edited_and_returns_a_reason()
    {
        var result = ManifestListEditor.Append("flags: [unfinished", StorySource.ManifestFileName, ManifestVocabulary.Flags, "missing");

        Assert.Null(result.Edit);
        Assert.Contains("invalid YAML", result.Reason);
    }

    [Theory]
    [InlineData("flags: not-a-list")]
    [InlineData("flags: [invalid value]")]
    public void Unsupported_flag_sections_return_a_disabled_reason(string flags)
    {
        var manifest = $"game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n{flags}\ncharacters:\n  player: {{first-name: Ada}}\n";
        var analysis = AnalyzeFlag(manifest, "=== start\n{if flag:missing}Never.{{/if}}\n-> END\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingFlag(analysis, diagnostic);

        Assert.Empty(result.Fixes);
        Assert.NotNull(result.DisabledReason);
    }

    [Fact]
    public void An_effect_declared_flag_does_not_get_a_declaration_fix()
    {
        var analysis = AnalyzeFlag(
            "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n",
            "=== start\n~ set flag:missing\n{if flag:missing}Seen.{/if}\n-> END\n");

        Assert.DoesNotContain(analysis.Diagnostics, item => item.Code == "TW2004");
    }

    [Theory]
    [InlineData("{if var:gold > 3}Rich.{/if}", "gold", "a number", "0")]
    [InlineData("{if var:gold + 1 == 4}Rich.{/if}", "gold", "a number", "0")]
    [InlineData("{if -var:gold < 0}Owed.{/if}", "gold", "a number", "0")]
    [InlineData("{if 3 == (var:gold)}Three.{/if}", "gold", "a number", "0")]
    [InlineData("{if var:weather == \"rain\"}Wet.{/if}", "weather", "text", "\"\"")]
    [InlineData("{if \"rain\" != var:weather}Dry.{/if}", "weather", "text", "\"\"")]
    [InlineData("~ var:mood = \"calm\"\n{if var:weather == var:mood}Same.{/if}", "weather", "text", "\"\"")]
    [InlineData("~ var:total = 1\n{if var:total == var:bonus}Same.{/if}", "bonus", "a number", "0")]
    [InlineData("~ var:total = 1\n~ var:total += var:bonus", "bonus", "a number", "0")]
    [InlineData("~ var:total = 1\n~ var:total = var:bonus", "bonus", "a number", "0")]
    [InlineData("~ var:mood = \"calm\"\n~ var:mood = var:weather", "weather", "text", "\"\"")]
    [InlineData("~ give player rope var:count", "count", "a number", "0")]
    public void Missing_variable_infers_its_declaration_from_usage(
        string body, string variable, string description, string value)
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\nitems:\n  rope: Rope\n";
        var story = $"=== start\n{body}\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingVariable(analysis, diagnostic);

        Assert.Null(result.DisabledReason);
        var fix = Assert.Single(result.Fixes);
        Assert.Equal($"Declare variable '{variable}' as {description}", fix.Title);
        Assert.Equal(StoryQuickFixKind.TextEdit, fix.Kind);
        Assert.Equal(StorySource.ManifestFileName, fix.File);
        Assert.Equal($"\nvars:\n  {variable}: {value}\n", fix.NewText);
        Assert.Contains($"\nvars:\n  {variable}: {value}\n", ApplyInsertion(manifest, fix));
        var edited = AnalyzeManifest(ApplyInsertion(manifest, fix), story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("The purse holds {var:gold}.")]
    [InlineData("{if var:gold}Seen.{/if}")]
    [InlineData("{if var:gold == var:other}Same.{/if}")]
    public void Missing_variable_with_undetermined_type_offers_number_and_text(string body)
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\nvars: {}\n";
        var story = $"=== start\n{body}\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = analysis.Diagnostics.First(item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingVariable(analysis, diagnostic);

        Assert.Null(result.DisabledReason);
        Assert.Collection(result.Fixes,
            number =>
            {
                Assert.Equal("Declare variable 'gold' as a number", number.Title);
                Assert.Equal("gold: 0", number.NewText);
            },
            text =>
            {
                Assert.Equal("Declare variable 'gold' as text", text.Title);
                Assert.Equal("gold: \"\"", text.NewText);
            });
    }

    [Fact]
    public void Missing_variable_substitution_accepts_either_declaration()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\nThe purse holds {var:gold}.\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var fixes = StoryQuickFixes.ForMissingVariable(analysis, diagnostic).Fixes;

        Assert.Equal(2, fixes.Count);
        foreach (var fix in fixes)
        {
            var edited = AnalyzeManifest(ApplyInsertion(manifest, fix), story);
            Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void Missing_variable_appends_to_a_flow_mapping()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters: {player: {first-name: Ada}}\nvars: {gold: 1}\n";
        const string story = "=== start\n{if var:weather == \"rain\"}Wet.{/if}\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var fix = Assert.Single(StoryQuickFixes.ForMissingVariable(analysis, diagnostic).Fixes);

        Assert.Equal(", weather: \"\"", fix.NewText);
        var updated = ApplyInsertion(manifest, fix);
        Assert.Contains("vars: {gold: 1, weather: \"\"}", updated);
        Assert.DoesNotContain(AnalyzeManifest(updated, story).Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Missing_variable_preserves_comments_and_crlf_in_block_mappings()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\nvars:\r\n  gold: 1 # keep gold\r\n  # keep this comment\r\n  weather: \"sunny\"\r\ncharacters:\r\n  player: {first-name: Ada}\r\n";
        const string story = "=== start\r\n{if var:gold > var:debt}Rich.{/if}\r\n-> END\r\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var fix = Assert.Single(StoryQuickFixes.ForMissingVariable(analysis, diagnostic).Fixes);

        Assert.Equal("  debt: 0\r\n", fix.NewText);
        Assert.Equal(new SourceLocation(StorySource.ManifestFileName, 9, 1), fix.Range.Start);
        var updated = ApplyInsertion(manifest, fix);
        Assert.Contains("  gold: 1 # keep gold\r\n  # keep this comment\r\n  weather: \"sunny\"\r\n  debt: 0\r\ncharacters:", updated);
        Assert.DoesNotContain(AnalyzeManifest(updated, story).Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("vars:\n  gold: 1\n", "=== start\n{if var:gold > 1}Rich.{/if}\n-> END\n")]
    [InlineData("", "=== start\n~ var:gold = 1\n{if var:gold > 1}Rich.{/if}\n-> END\n")]
    public void Declared_or_assigned_variables_do_not_get_declaration_fixes(string vars, string story)
    {
        var manifest = $"game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {{first-name: Ada}}\n{vars}";

        var analysis = AnalyzeManifest(manifest, story);

        Assert.DoesNotContain(analysis.Diagnostics, item => item.Code == "TW2004");
    }

    [Fact]
    public void Missing_variable_in_a_supporting_manifest_declaration_gets_no_fix()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        var analysis = AnalyzeManifest(manifest, "=== start\n{if var:gold > 1}Rich.{/if}\n-> END\n",
            ("world/economy.talewright.yaml", "vars:\n  gold: 1\n"));

        Assert.DoesNotContain(analysis.Diagnostics, item => item.Code == "TW2004");
    }

    [Fact]
    public void Flag_and_variable_fixes_ignore_each_others_diagnostics()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        var analysis = AnalyzeManifest(manifest, "=== start\n{if flag:ready}Ready.{/if}\n{if var:gold > 1}Rich.{/if}\n-> END\n");
        var diagnostics = analysis.Diagnostics.Where(item => item.Code == "TW2004").ToArray();
        Assert.Equal(2, diagnostics.Length);

        Assert.Empty(StoryQuickFixes.ForMissingVariable(analysis, diagnostics[0]).Fixes);
        Assert.Null(StoryQuickFixes.ForMissingVariable(analysis, diagnostics[0]).Title);
        Assert.Empty(StoryQuickFixes.ForMissingFlag(analysis, diagnostics[1]).Fixes);
        Assert.Null(StoryQuickFixes.ForMissingFlag(analysis, diagnostics[1]).Title);
    }

    [Theory]
    [InlineData("vars: [gold]")]
    [InlineData("vars: not-a-mapping")]
    public void Unsupported_variable_sections_return_a_disabled_reason_without_edits(string vars)
    {
        var manifest = $"game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n{vars}\ncharacters:\n  player: {{first-name: Ada}}\n";
        var analysis = AnalyzeManifest(manifest, "=== start\n{if var:debt > 1}Owed.{/if}\n-> END\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2004");

        var result = StoryQuickFixes.ForMissingVariable(analysis, diagnostic);

        Assert.Empty(result.Fixes);
        Assert.Equal("Declare variable 'debt'", result.Title);
        Assert.NotNull(result.DisabledReason);
    }

    [Fact]
    public void Missing_variable_in_malformed_yaml_is_not_edited()
    {
        var result = ManifestListEditor.AppendMappingEntry(
            "vars: {gold: 1", StorySource.ManifestFileName, ManifestVocabulary.Vars, "debt", "0", "0");

        Assert.Null(result.Edit);
        Assert.Contains("invalid YAML", result.Reason);
    }

    [Theory]
    [InlineData("rpg")]
    [InlineData("none")]
    public void Missing_stat_expands_a_ruleset_shorthand_without_changing_its_base(string baseId)
    {
        var manifest = $"game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset: {baseId}\ncharacters:\n  player: {{first-name: Ada}}\n";
        const string story = "=== start\n~ player:charm += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal("Declare stat 'charm'", fix.Title);
        Assert.Equal(
            $":\n    base: {baseId}\n    stats:\n      - id: charm\n        name: Charm\n        category: custom\n        min: 0\n        max: 2147483647\n        default: 0",
            fix.NewText);
        var updated = ApplyTextEdit(manifest, fix);
        Assert.Contains($"ruleset:\n    base: {baseId}\n    stats:", updated);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Missing_stat_adds_schema_defaults_when_the_ruleset_is_implicit()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:charm += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal(
            "  ruleset:\n    stats:\n      - id: charm\n        name: Charm\n        category: custom\n        min: 0\n        max: 2147483647\n        default: 0\n",
            fix.NewText);
        var edited = AnalyzeManifest(ApplyTextEdit(manifest, fix), story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Missing_stat_preserves_crlf_and_trailing_comment_when_expanding_ruleset_shorthand()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\n  ruleset: none # keep selected base\r\ncharacters:\r\n  player: {first-name: Ada}\r\n";
        const string story = "=== start\r\n~ player:charm += 1\r\n-> END\r\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var edited = ApplyTextEdit(manifest, result.Fixes[0]);

        Assert.Contains("ruleset:\r\n    base: none # keep selected base\r\n    stats:\r\n", edited);
        Assert.DoesNotContain("\r\r\n", edited);
        Assert.DoesNotContain(AnalyzeManifest(edited, story).Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Missing_stat_adds_a_ruleset_with_crlf_without_rewriting_the_manifest()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\ncharacters:\r\n  player: {first-name: Ada}\r\n";
        const string story = "=== start\r\n~ player:charm += 1\r\n-> END\r\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var edited = ApplyTextEdit(manifest, result.Fixes[0]);

        Assert.Contains("  ruleset:\r\n    stats:\r\n      - id: charm\r\n", edited);
        Assert.DoesNotContain("\r\r\n", edited);
        Assert.DoesNotContain(AnalyzeManifest(edited, story).Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Rpg_stat_offers_a_preferred_ruleset_action_and_keeps_declare_stat()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:hp += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");
        Assert.Equal("rpg", analysis.StatProviderName("hp"));
        Assert.DoesNotContain("hp", analysis.BaseRulesetStats);

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);

        var enable = Assert.Single(result.Fixes, fix => fix.Title == "Enable rpg ruleset");
        var declare = Assert.Single(result.Fixes, fix => fix.Title == "Declare stat 'hp'");
        Assert.True(enable.IsPreferred);
        Assert.False(declare.IsPreferred);
        var edited = ApplyTextEdit(manifest, enable);
        Assert.Contains("  ruleset: rpg\n", edited);
        var compiled = AnalyzeManifest(edited, story);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Rpg_stat_adds_missing_base_without_changing_disable_or_stats_entries()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset:\n    disable: [str]\n    stats:\n      - id: grit\n        category: custom\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:hp += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");
        Assert.Equal("rpg", analysis.StatProviderName("hp"));
        Assert.DoesNotContain("hp", analysis.BaseRulesetStats);

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.Empty(result.DisabledActions);
        var enable = Assert.Single(result.Fixes,
            fix => fix.Title == "Enable rpg ruleset");
        var edited = ApplyTextEdit(manifest, enable);

        Assert.Contains("    disable: [str]\n", edited);
        Assert.Contains("    stats:\n      - id: grit\n        category: custom\n", edited);
        Assert.Contains("    base: rpg\n", edited);
        var compiled = AnalyzeManifest(edited, story);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Rpg_stat_replaces_none_base_and_preserves_disable_stats_and_comments()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset:\n    base: none # keep base comment\n    disable: [str] # keep disable comment\n    stats:\n      - id: grit\n        category: custom\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:hp += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");
        Assert.Equal("rpg", analysis.StatProviderName("hp"));
        Assert.DoesNotContain("hp", analysis.BaseRulesetStats);

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.Empty(result.DisabledActions);
        var enable = Assert.Single(result.Fixes,
            fix => fix.Title == "Enable rpg ruleset");
        var edited = ApplyTextEdit(manifest, enable);

        Assert.Contains("base: rpg # keep base comment", edited);
        Assert.Contains("disable: [str] # keep disable comment", edited);
        Assert.Contains("      - id: grit\n        category: custom", edited);
        var compiled = AnalyzeManifest(edited, story);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compiled.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Rpg_ruleset_action_is_disabled_for_unsupported_manifest_shapes()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset: {base: none, stats: {hp: custom}}\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:hp += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);

        var disabled = Assert.Single(result.DisabledActions);
        Assert.Equal("Enable rpg ruleset", disabled.Title);
        Assert.Contains("supported list of stat definitions", disabled.Reason);
        Assert.Contains("not a list", result.DisabledReason);
        Assert.Equal("Declare stat 'hp'", result.Title);
    }

    [Fact]
    public void Rpg_ruleset_action_does_not_override_a_different_registered_stat_provider()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:hp += 1\n-> END\n";
        var rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.Default.Rulesets, StringComparer.Ordinal)
        {
            ["astral"] = () => new RulesetBuilder("astral")
                .AddStat(new StatDefinition(new("hp"), "Health", StatCategory.Custom, 0, 100, 10))
                .Build()
        };
        var analysis = AnalyzeManifest(manifest, story, new CompilerOptions { Rulesets = rulesets });
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);

        var declare = Assert.Single(result.Fixes);
        Assert.Equal("Declare stat 'hp'", declare.Title);
        Assert.False(declare.IsPreferred);
        Assert.Empty(result.DisabledActions);
    }

    [Fact]
    public void Missing_stat_appends_to_existing_block_stats_and_preserves_overrides_comments_and_crlf()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\n  ruleset:\r\n    base: rpg\r\n    disable: [cha] # keep disabled stat\r\n    stats:\r\n      - id: grit\r\n        category: custom # keep category\r\ncharacters:\r\n  player: {first-name: Ada}\r\n";
        const string story = "=== start\r\n~ player:charm += 1\r\n-> END\r\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal(
            "      - id: charm\r\n        name: Charm\r\n        category: custom\r\n        min: 0\r\n        max: 2147483647\r\n        default: 0\r\n",
            fix.NewText);
        var updated = ApplyTextEdit(manifest, fix);
        Assert.Contains("    disable: [cha] # keep disabled stat\r\n", updated);
        Assert.Contains("        category: custom # keep category\r\n      - id: charm\r\n", updated);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("game: {id: quick-fix, title: Quick Fix, start: start, ruleset: {base: none}}", ", stats: [{id: charm, name: Charm, category: custom, min: 0, max: 2147483647, default: 0}]")]
    [InlineData("game: {id: quick-fix, title: Quick Fix, start: start, ruleset: {base: none, stats: []}}", "{id: charm, name: Charm, category: custom, min: 0, max: 2147483647, default: 0}")]
    public void Missing_stat_preserves_flow_ruleset_and_stats_shapes(string game, string insertion)
    {
        var manifest = $"{game}\ncharacters: {{player: {{first-name: Ada}}}}\n";
        const string story = "=== start\n~ player:charm += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal(insertion, fix.NewText);
        var edited = AnalyzeManifest(ApplyTextEdit(manifest, fix), story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("game: {id: quick-fix, title: Quick Fix, start: start}", ", ruleset: {stats: [{id: charm, name: Charm, category: custom, min: 0, max: 2147483647, default: 0}]}")]
    [InlineData("game: {id: quick-fix, title: Quick Fix, start: start, ruleset: none}", "{base: none, stats: [{id: charm, name: Charm, category: custom, min: 0, max: 2147483647, default: 0}]}")]
    public void Missing_stat_adds_or_expands_rulesets_inside_a_flow_game(string game, string insertion)
    {
        var manifest = $"{game}\ncharacters: {{player: {{first-name: Ada}}}}\n";
        const string story = "=== start\n~ player:charm += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingStat(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal(insertion, fix.NewText);
        var edited = AnalyzeManifest(ApplyTextEdit(manifest, fix), story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Disabled_builtin_stats_do_not_get_redeclared_as_custom_stats()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset: {base: rpg, disable: [str]}\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:str += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        Assert.Empty(StoryQuickFixes.ForMissingStat(analysis, diagnostic).Fixes);
    }

    [Fact]
    public void Existing_story_stats_do_not_get_duplicate_declaration_actions()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n  ruleset: {base: none, stats: [{id: charm, category: custom}]}\ncharacters:\n  player: {first-name: Ada}\n";
        const string story = "=== start\n~ player:charm += 1\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);

        Assert.DoesNotContain(analysis.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Unsupported_or_malformed_stat_sections_are_not_edited()
    {
        var unsupported = ManifestListEditor.AppendStoryStat(
            "game: {ruleset: {base: none, stats: {charm: custom}}}\n", StorySource.ManifestFileName, "charm", "Charm");
        var malformed = ManifestListEditor.AppendStoryStat(
            "game: {ruleset: [unfinished\n", StorySource.ManifestFileName, "charm", "Charm");

        Assert.Null(unsupported.Edit);
        Assert.Contains("not a list", unsupported.Reason);
        Assert.Null(malformed.Edit);
        Assert.Contains("invalid YAML", malformed.Reason);
    }

    [Theory]
    [InlineData(
        "=== start\n> old-man: Hello.\n-> END\n",
        "  old-man:\n    first-name: Old Man",
        "Declare character 'old-man'")]
    [InlineData(
        "=== start\n{if player has silver-sword}Seen.{/if}\n-> END\n",
        "items:\n  silver-sword: Silver Sword",
        "Declare item 'silver-sword'")]
    [InlineData(
        "=== start\n{faction:river-guild:name}\n-> END\n",
        "factions:\n  river-guild: {}",
        "Declare faction 'river-guild'")]
    public void Missing_manifest_names_add_schema_valid_declarations(
        string story, string declaration, string title)
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingManifestName(analysis, diagnostic);

        var fix = Assert.Single(result.Fixes);
        Assert.Null(result.DisabledReason);
        Assert.Equal(title, fix.Title);
        Assert.Equal(StoryQuickFixKind.TextEdit, fix.Kind);
        Assert.Equal(StorySource.ManifestFileName, fix.File);
        Assert.Contains(declaration, ApplyInsertion(manifest, fix));
        var updated = ApplyInsertion(manifest, fix);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Missing_manifest_name_appends_to_a_flow_mapping()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters: {player: {first-name: Ada}}\nitems: {rope: Rope}\n";
        const string story = "=== start\n{if player has silver-sword}Seen.{/if}\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingManifestName(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        Assert.Equal(", silver-sword: Silver Sword", fix.NewText);
        Assert.Equal(fix.Range.Start, fix.Range.End);
        var updated = ApplyInsertion(manifest, fix);
        Assert.Contains("items: {rope: Rope, silver-sword: Silver Sword}", updated);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Missing_manifest_name_appends_to_an_empty_flow_mapping()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters: {player: {first-name: Ada}}\nfactions: {}\n";
        const string story = "=== start\n{faction:river-guild:name}\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var fix = Assert.Single(StoryQuickFixes.ForMissingManifestName(analysis, diagnostic).Fixes);

        Assert.Equal("river-guild: {}", fix.NewText);
        var updated = ApplyInsertion(manifest, fix);
        Assert.Contains("factions: {river-guild: {}}", updated);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Fact]
    public void Missing_manifest_name_preserves_comments_and_crlf_in_block_mappings()
    {
        const string manifest = "game:\r\n  id: quick-fix\r\n  title: Quick Fix\r\n  start: start\r\ncharacters:\r\n  player: {first-name: Ada} # keep player\r\n  # keep this comment\r\n  guide: {first-name: Bea}\r\n";
        const string story = "=== start\r\n> old-man: Hello.\r\n-> END\r\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var fix = Assert.Single(StoryQuickFixes.ForMissingManifestName(analysis, diagnostic).Fixes);

        Assert.Equal("  old-man:\r\n    first-name: Old Man\r\n", fix.NewText);
        var updated = ApplyInsertion(manifest, fix);
        Assert.Contains("player: {first-name: Ada} # keep player\r\n  # keep this comment\r\n  guide: {first-name: Bea}\r\n  old-man:", updated);
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Theory]
    [InlineData("characters:\n  player:\n    first-name: Ada\nitems:\n  rope: Rope\n")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n\n# keep this comment\nitems:\n  rope: Rope\n")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    # trailing comment\nitems:\n  rope: Rope\n")]
    public void Missing_manifest_name_appends_after_a_multiline_entry_followed_by_another_section(string sections)
    {
        var manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n" + sections;
        const string story = "=== start\n> old-man: Hello.\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingManifestName(analysis, diagnostic);
        Assert.True(result.Fixes.Count == 1, result.DisabledReason);
        var fix = result.Fixes[0];

        var updated = ApplyInsertion(manifest, fix);
        Assert.Equal(manifest, updated.Replace("  old-man:\n    first-name: Old Man\n", ""));
        Assert.True(updated.IndexOf("  old-man:", StringComparison.Ordinal) < updated.IndexOf("items:", StringComparison.Ordinal));
        var edited = AnalyzeManifest(updated, story);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(edited.Diagnostics, item => item.Code == "TW2001");
    }

    [Theory]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    last-name: |\n      Line one\n      Line two\nitems:\n  rope: Rope\n")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    last-name: >-\n      Folded\n\nitems:\n  rope: Rope\n")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    last-name: \"Quoted\n      across lines\"\nitems:\n  rope: Rope\n")]
    public void Missing_manifest_name_disables_edit_when_multiline_scalar_cannot_be_verified(string value)
    {
        var manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\n" + value;
        const string story = "=== start\n> old-man: Hello.\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingManifestName(analysis, diagnostic);

        Assert.Empty(result.Fixes);
        Assert.Contains("could not be verified to preserve existing manifest data", result.DisabledReason);
        Assert.Equal(manifest, analysis.Source.Files[StorySource.ManifestFileName]);
    }

    [Fact]
    public void Existing_manifest_names_and_unknown_properties_do_not_get_declaration_fixes()
    {
        const string story = "=== start\n> old-man: Hello.\n{old-man:unknown-property}\n-> END\n";
        var analysis = AnalyzeManifest(
            "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n",
            story,
            ("support/extra.talewright.yaml", "characters:\n  old-man: {first-name: Old Man}\n"));
        var location = new SourceLocation("main.tale", 2, 3);
        var diagnostic = new Diagnostic(DiagnosticSeverity.Error, "TW2001", "Unknown character 'old-man'.", location);
        var propertyDiagnostic = new Diagnostic(DiagnosticSeverity.Error, "TW2001", "Unknown property.", new("main.tale", 3, 10));

        Assert.Empty(StoryQuickFixes.ForMissingManifestName(analysis, diagnostic).Fixes);
        Assert.Empty(StoryQuickFixes.ForMissingManifestName(analysis, propertyDiagnostic).Fixes);

        var playerAnalysis = AnalyzeManifest(
            "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n",
            "=== start\n> player: Hello.\n-> END\n");
        var playerDiagnostic = new Diagnostic(
            DiagnosticSeverity.Error, "TW2001", "Unknown character 'player'.", new("main.tale", 2, 3));
        Assert.Empty(StoryQuickFixes.ForMissingManifestName(playerAnalysis, playerDiagnostic).Fixes);
    }

    [Fact]
    public void Unsupported_manifest_mapping_returns_a_disabled_reason()
    {
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters: []\n";
        const string story = "=== start\n> old-man: Hello.\n-> END\n";
        var analysis = AnalyzeManifest(manifest, story);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2001");

        var result = StoryQuickFixes.ForMissingManifestName(analysis, diagnostic);

        Assert.Empty(result.Fixes);
        Assert.Contains("not a YAML mapping", result.DisabledReason);
    }

    [Fact]
    public void Malformed_yaml_and_commented_flow_mappings_are_not_edited()
    {
        var malformed = ManifestListEditor.AppendMappingEntry(
            "characters: [unfinished", StorySource.ManifestFileName, "characters", "old-man",
            "\nfirst-name: Old Man", "{first-name: Old Man}");
        const string commentedText = "items: {rope: Rope # keep this comment\n}\n";
        var commented = ManifestListEditor.AppendMappingEntry(
            commentedText, StorySource.ManifestFileName, "items", "silver-sword", "Silver Sword", "Silver Sword");

        Assert.Null(malformed.Edit);
        Assert.Contains("invalid YAML", malformed.Reason);
        Assert.Null(commented.Edit);
        Assert.Contains("comment inside its flow mapping", commented.Reason);
        Assert.Equal("items: {rope: Rope # keep this comment\n}\n", commentedText);
    }

    [Fact]
    public void Missing_scene_offers_append_and_new_file_actions()
    {
        const string text = "=== start\n* [Go] -> camp.hubb\n";
        var analysis = Analyze("chapters/main.tale", text);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fixes = StoryQuickFixes.ForMissingScene(analysis, diagnostic);

        Assert.Collection(fixes,
            append =>
            {
                Assert.Equal("Create scene 'camp.hubb' in this file", append.Title);
                Assert.Equal(StoryQuickFixKind.TextEdit, append.Kind);
                Assert.Equal("chapters/main.tale", append.File);
                Assert.Equal(new SourceRange(new("chapters/main.tale", 3, 1), new("chapters/main.tale", 3, 1)), append.Range);
                Assert.Equal("\n=== camp.hubb\n", append.NewText);
            },
            create =>
            {
                Assert.Equal("Create scene 'camp.hubb' in a new file", create.Title);
                Assert.Equal(StoryQuickFixKind.CreateFile, create.Kind);
                Assert.Equal("chapters/camp.hubb.tale", create.File);
                Assert.Equal(new SourceRange(new(create.File, 1, 1), new(create.File, 1, 1)), create.Range);
                Assert.Equal("=== camp.hubb\n", create.NewText);
            });
        Assert.Contains(analysis.Diagnostics, item => item.Code == "TW2002");
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r", true)]
    [InlineData("\r", false)]
    public void Fixes_preserve_line_endings_and_final_newline_convention(string newline, bool finalNewline)
    {
        var text = $"=== start{newline}* -> missing{(finalNewline ? newline : "")}";
        var analysis = Analyze("main.tale", text);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fixes = StoryQuickFixes.ForMissingScene(analysis, diagnostic);

        Assert.Equal($"{newline}{(finalNewline ? "" : newline)}=== missing{(finalNewline ? newline : "")}",
            Assert.Single(fixes, fix => fix.Kind == StoryQuickFixKind.TextEdit).NewText);
        Assert.Equal($"=== missing{(finalNewline ? newline : "")}",
            Assert.Single(fixes, fix => fix.Kind == StoryQuickFixKind.CreateFile).NewText);
    }

    [Fact]
    public void Existing_adjacent_scene_file_is_never_offered_for_creation()
    {
        var analysis = Analyze("main.tale", "=== start\n* -> missing\n",
            ("missing.tale", "=== something-else\n-> END\n"));
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fixes = StoryQuickFixes.ForMissingScene(analysis, diagnostic);

        Assert.Single(fixes);
        Assert.Equal(StoryQuickFixKind.TextEdit, fixes[0].Kind);
    }

    [Theory]
    [InlineData("TW2001", "missing")]
    [InlineData("TW2002", "bad!id")]
    [InlineData("TW2002", "END")]
    public void Unsupported_or_invalid_scene_references_have_no_fixes(string code, string target)
    {
        var analysis = Analyze("main.tale", $"=== start\n* -> {target}\n");
        var diagnostic = new Diagnostic(DiagnosticSeverity.Error, code, "test", new("main.tale", 2, 6));

        Assert.Empty(StoryQuickFixes.ForMissingScene(analysis, diagnostic));
    }

    [Theory]
    [InlineData("strt", "start")]
    [InlineData("staart", "start")]
    [InlineData("stort", "start")]
    [InlineData("camp.mpa", "camp.map")]
    public void Close_scene_matches_replace_only_the_name_and_clear_the_diagnostic(string target, string expected)
    {
        var extraScene = expected == "start" ? "" : $"=== {expected}\n-> END\n";
        var text = $"=== start\n* -> {target}\n{extraScene}";
        var analysis = Analyze("main.tale", text);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fix = Assert.Single(StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes);
        var targetLine = $"* -> {target}";
        var startColumn = targetLine.IndexOf(target, StringComparison.Ordinal) + 1;
        Assert.Equal($"Change to `{expected}`", fix.Title);
        Assert.True(fix.IsPreferred);
        Assert.Equal(new SourceLocation("main.tale", 2, startColumn), fix.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, startColumn + target.Length), fix.Range.End);

        var edited = ApplyTextEdit(text, fix);
        Assert.DoesNotContain(Analyze("main.tale", edited).Diagnostics, item => item.Code == "TW2002");
    }

    [Fact]
    public void Close_scene_matches_are_ranked_and_limited_to_three()
    {
        const string text = "=== start\n* -> camp.hubb\n=== camp.hub\n-> END\n=== camp.hubx\n-> END\n=== camp.hu\n-> END\n=== camp.hb\n-> END\n";
        var analysis = Analyze("main.tale", text);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fixes = StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes;

        Assert.Equal(
            ["Change to `camp.hub`", "Change to `camp.hubx`", "Change to `camp.hb`"],
            fixes.Select(fix => fix.Title));
        Assert.All(fixes, fix => Assert.False(fix.IsPreferred));
        Assert.DoesNotContain(fixes, fix => fix.NewText == "camp.hu");
    }

    [Fact]
    public void Close_scene_matches_reject_candidates_beyond_the_length_relative_threshold()
    {
        const string text = "=== start\n* -> strt\n=== stap\n-> END\n";
        var analysis = Analyze("main.tale", text);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

        var fixes = StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes;

        Assert.Equal(["start"], fixes.Select(fix => fix.NewText));
    }

    [Fact]
    public void Close_name_matches_do_not_cross_kinds_or_suggest_distant_names()
    {
        const string manifest = """
            game:
              id: quick-fix
              title: Quick Fix
              start: start
            characters:
              player: {first-name: Ada}
              camp.hub: {first-name: Camp}
            """;

        foreach (var target in new[] { "camp.hubb", "zzzzzzzz" })
        {
            var analysis = AnalyzeManifest(manifest, $"=== start\n* -> {target}\n");
            var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2002");

            Assert.Empty(StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes);
        }
    }

    [Fact]
    public void Close_name_matches_cover_each_diagnostic_name_category()
    {
        const string manifest = """
            game:
              id: quick-fix
              title: Quick Fix
              version: 1
              start: start
              ruleset:
                base: none
                stats:
                  - id: strength
                    name: Strength
                    category: custom
                    min: 0
                    max: 10
                    default: 1
            characters:
              player: {first-name: Ada}
              old-man: {first-name: Old}
            items: {silver-sword: Silver Sword}
            factions: {river-guild: {name: River Guild}}
            flags: [ready]
            vars: {coins: 1}
            """;
        var cases = new (string Story, string Code, string Expected)[]
        {
            ("> old-mna: Hello.\n", "TW2001", "old-man"),
            ("{if player has silver-sowrd}Seen.{/if}\n", "TW2001", "silver-sword"),
            ("{faction:river-gulid:name}\n", "TW2001", "river-guild"),
            ("~ player:strenth += 1\n", "TW2001", "strength"),
            ("{if player:pronoun:subjet == \"they\"}Seen.{/if}\n", "TW2001", "subject"),
            ("{if flag:reayd}Seen.{/if}\n", "TW2004", "ready"),
            ("The total is {var:cois}.\n", "TW2004", "coins")
        };

        foreach (var (story, code, expected) in cases)
        {
            var text = $"=== start\n{story}-> END\n";
            var analysis = AnalyzeManifest(manifest, text);
            var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == code);

            var fix = Assert.Single(StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes);

            Assert.Equal(expected, fix.NewText);
            Assert.True(fix.IsPreferred);
            Assert.DoesNotContain(AnalyzeManifest(manifest, ApplyTextEdit(text, fix)).Diagnostics,
                item => item.Code == code);
        }
    }

    [Fact]
    public void Close_predicate_matches_preserve_the_at_sign_and_replace_only_the_name()
    {
        const string manifest = """
            game:
              id: quick-fix
              title: Quick Fix
              start: start
            characters:
              player: {first-name: Ada}
            """;
        const string text = "=== start\n{if @Raedy}Seen.{/if}\n-> END\n";
        var source = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = text
        });
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Ready", _ => true)
        };
        var analysis = StoryAnalyzer.Analyze(source, options);
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "TW2005");

        var fix = Assert.Single(StoryQuickFixes.ForCloseNameMatches(analysis, diagnostic).Fixes);

        Assert.Equal("Change to `@Ready`", fix.Title);
        Assert.Equal("Ready", fix.NewText);
        Assert.True(fix.IsPreferred);
        Assert.Equal(new SourceLocation("main.tale", 2, 6), fix.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, 11), fix.Range.End);
        var editedSource = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = ApplyTextEdit(text, fix)
        });
        Assert.DoesNotContain(StoryAnalyzer.Analyze(editedSource, options).Diagnostics,
            item => item.Code == "TW2005");
    }

    private static StoryAnalysis Analyze(string file, string text, params (string File, string Text)[] additionalFiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] =
                "game:\n  id: quick-fix\n  title: Quick Fix\n  version: 1\n  start: start\ncharacters:\n  player: {first-name: Ada}\n",
            [file] = text
        };
        foreach (var (additionalFile, additionalText) in additionalFiles) files.Add(additionalFile, additionalText);
        return StoryAnalyzer.Analyze(new StorySource(files));
    }

    private static string ApplyInsertion(string text, StoryQuickFix fix)
    {
        Assert.Equal(fix.Range.Start, fix.Range.End);
        var offset = 0;
        for (var line = 1; line < fix.Range.Start.Line; line++)
        {
            var newline = text.IndexOfAny(['\r', '\n'], offset);
            Assert.NotEqual(-1, newline);
            offset = newline + (text[newline] == '\r' && newline + 1 < text.Length && text[newline + 1] == '\n' ? 2 : 1);
        }
        offset += fix.Range.Start.Column - 1;
        return text.Insert(offset, fix.NewText);
    }

    private static string ApplyTextEdit(string text, StoryQuickFix fix)
    {
        var start = Offset(fix.Range.Start);
        var end = Offset(fix.Range.End);
        Assert.InRange(start, 0, text.Length);
        Assert.InRange(end, start, text.Length);
        return text.Remove(start, end - start).Insert(start, fix.NewText);

        int Offset(SourceLocation location)
        {
            var offset = 0;
            for (var line = 1; line < location.Line; line++)
            {
                var newline = text.IndexOfAny(['\r', '\n'], offset);
                Assert.NotEqual(-1, newline);
                offset = newline + (text[newline] == '\r' && newline + 1 < text.Length && text[newline + 1] == '\n' ? 2 : 1);
            }
            return offset + location.Column - 1;
        }
    }

    private static StoryAnalysis AnalyzeFlag(string manifest, string text) =>
        StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = text
        }));

    private static StoryAnalysis AnalyzeManifest(
        string manifest, string story, params (string File, string Text)[] additionalFiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = story
        };
        foreach (var (file, text) in additionalFiles) files.Add(file, text);
        return StoryAnalyzer.Analyze(new StorySource(files));
    }

    private static StoryAnalysis AnalyzeManifest(string manifest, string story, CompilerOptions options) =>
        StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = story
        }), options);
}
