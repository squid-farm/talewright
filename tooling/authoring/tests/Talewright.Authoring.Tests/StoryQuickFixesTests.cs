using Talewright.Authoring;
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
}
