using Talewright.Authoring;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

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

    [Fact]
    public void Malformed_yaml_is_not_edited_and_returns_a_reason()
    {
        var result = ManifestListEditor.Append("flags: [unfinished", StorySource.ManifestFileName, "flags", "missing");

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

    private static StoryAnalysis AnalyzeFlag(string manifest, string text) =>
        StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = manifest,
            ["main.tale"] = text
        }));
}
