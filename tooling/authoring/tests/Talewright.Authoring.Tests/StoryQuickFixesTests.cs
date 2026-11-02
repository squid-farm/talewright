using Talewright.Authoring;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryQuickFixesTests
{
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
}
