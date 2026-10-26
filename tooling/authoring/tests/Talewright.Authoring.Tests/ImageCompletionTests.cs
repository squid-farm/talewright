using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class ImageCompletionTests
{
    private const string Manifest = """
        game:
          id: images
          title: Images
          start: start
        characters: { player: { first-name: Ada }, image: { first-name: Iris } }
        """;

    private static readonly string[] Paths =
    [
        "images/maps/camp.png", "images/maps/camp.jpg", "images/maps/Coast.webp",
        "images/maps/a\"b.jpeg", "images/camp.jpeg", "images/notes.txt",
        "images/../outside.png", "outside.png", "images/unsafe\\path.png",
    ];

    private static StorySource Source(string text, IEnumerable<string>? paths = null) =>
        new(new Dictionary<string, string> { [StorySource.ManifestFileName] = Manifest, ["main.tale"] = text }, paths ?? Paths);

    private static (StoryAnalysis Analysis, SourceLocation Position) Analyze(string marked)
    {
        var caret = marked.IndexOf('|');
        var lines = marked[..caret].ReplaceLineEndings("\n").Split('\n');
        return (StoryAnalyzer.Analyze(Source(marked.Remove(caret, 1))),
            new("main.tale", lines.Length, lines[^1].Length + 1));
    }

    [Theory]
    [InlineData("""{image("maps/ca|mp.png", "Map")}""")]
    [InlineData("""{image("maps/ca|""")]
    [InlineData("""> player: {image("maps/ca|")}""")]
    [InlineData("""* [Pick {image("maps/ca|")}] -> END""")]
    [InlineData("""**{image("maps/ca|")}**""")]
    public void Completes_only_case_sensitive_supported_paths_in_narrative_contexts(string line)
    {
        var (analysis, position) = Analyze("=== start\n" + line + "\n-> END");
        var items = StoryCompletion.Complete(analysis, position);
        Assert.Equal(new[] { "maps/camp.jpg", "maps/camp.png" }, items.Select(item => item.Label));
        Assert.All(items, item => Assert.Equal(item.Label, item.NewText));
    }

    [Fact]
    public void Replaces_the_entire_path_content_preserving_quotes_alt_and_utf16_positions()
    {
        const string marked = "=== start\r\n🦑 {image(\"maps/ca|mp.png\", \"Alt\")}";
        var (analysis, position) = Analyze(marked);
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == "maps/camp.jpg");
        var line = marked.Replace("|", "").Split('\n')[1];
        Assert.Equal("maps/camp.png", line[(item.Range.Start.Column - 1)..(item.Range.End.Column - 1)]);
        var updated = line[..(item.Range.Start.Column - 1)] + item.NewText + line[(item.Range.End.Column - 1)..];
        Assert.Equal("🦑 {image(\"maps/camp.jpg\", \"Alt\")}", updated);
    }

    [Fact]
    public void Escaped_quotes_use_decoded_prefix_and_source_boundaries()
    {
        var (analysis, position) = Analyze("""
            === start
            {image("maps/a\"|b.jpeg", "Alt")}
            -> END
            """);
        var item = Assert.Single(StoryCompletion.Complete(analysis, position));
        Assert.Equal("maps/a\"b.jpeg", item.Label);
        Assert.Equal("maps/a\\\"b.jpeg", item.NewText);
        Assert.Equal(item.NewText.Length, item.Range.End.Column - item.Range.Start.Column);
    }

    [Theory]
    [InlineData("""{image("maps/co|")}""")]
    [InlineData("""{image("camp", "maps/|")}""")]
    [InlineData("""\{image("maps/|")\}""")]
    [InlineData("""// {image("maps/|")}""")]
    [InlineData("""@title {image("maps/|")}""")]
    [InlineData("""Prose maps/|""")]
    public void Avoids_incorrect_case_alt_prose_comments_and_plain_string_fields(string line)
    {
        var (analysis, position) = Analyze("=== start\n" + line + "\n-> END");
        Assert.Empty(StoryCompletion.Complete(analysis, position));
    }

    [Theory]
    [InlineData("missing", "Map", "TW2012")]
    [InlineData("../camp", "Map", "TW2010")]
    [InlineData("camp.bmp", "Map", "TW2011")]
    [InlineData("maps/camp", "Map", "TW2013")]
    [InlineData("camp.jpeg", "", "TW2014")]
    public void Editor_diagnostics_match_compiler_without_duplicates(string path, string alt, string code)
    {
        var source = Source($"=== start\n{{image(\"{path}\", \"{alt}\")}}\n-> END");
        var analysis = StoryAnalyzer.Analyze(source);
        var diagnostic = Assert.Single(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(new SourceLocation("main.tale", 2, 8), diagnostic.Location);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
    }

    [Fact]
    public async Task Workspace_retains_catalogue_and_invalidates_on_image_events_without_losing_unsaved_text()
    {
        var workspace = new StoryWorkspace(Source("=== start\nOld text.\n-> END", []));
        workspace.Open("main.tale", 1, "=== start\n{image(\"camp.jpeg\", \"Map\")}\n-> END");
        var missing = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(missing.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2012");
        workspace.UpdateImage("images/camp.jpeg", true);
        Assert.Null(workspace.Current);
        var added = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.DoesNotContain(added.Analysis.Diagnostics, diagnostic => diagnostic.Code.StartsWith("TW201", StringComparison.Ordinal));
        Assert.Contains("camp.jpeg", added.Analysis.Source.Files["main.tale"]);
        workspace.UpdateImage("images/camp.jpeg", false);
        workspace.UpdateImage("images/new.jpeg", true);
        var renamed = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(renamed.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2012");
        Assert.Equal(new[] { "images/new.jpeg" }, renamed.Analysis.Source.ImagePaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("id: [")]
    public void Manifest_recovery_keeps_image_diagnostics_and_completion_without_duplicates(string? manifest)
    {
        var files = new Dictionary<string, string>
        {
            ["main.tale"] = "=== start\n{image(\"camp.jpeg\", \"Map\")}\n-> END",
        };
        if (manifest is not null) files[StorySource.ManifestFileName] = manifest;
        var analysis = StoryAnalyzer.Analyze(new(files, []));
        Assert.Single(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2012");
        analysis = StoryAnalyzer.Analyze(new(files, ["images/camp.jpeg"]));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code.StartsWith("TW201", StringComparison.Ordinal));
        Assert.Single(StoryCompletion.Complete(analysis, new("main.tale", 2, 9)));
    }
}
