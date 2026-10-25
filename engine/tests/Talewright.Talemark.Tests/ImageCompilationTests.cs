using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class ImageCompilationTests
{
    private const string Manifest = """
        id: images
        title: Images
        start: start
        characters: {player: {first-name: Ada}}
        """;

    private static StorySource Source(string call, params string[] paths) =>
        new(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["chapters/main.tale"] = $"=== start\n{call}\n-> END",
        }, paths);

    private static MediaTemplate Media(CompilationResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assert.IsType<MediaTemplate>(Assert.Single(
            Assert.IsType<ParagraphTemplate>(result.Story!.GetScene(new SceneId("start")).Body[0]).Inlines));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".PNG")]
    public void Compiles_explicit_supported_paths_without_reading_bytes(string extension)
    {
        var path = "maps/sector (west), v2" + extension;
        var result = StoryCompiler.Compile(Source($"{{image(\"{path}\", \"  Literal {{player:name}}  \")}}", "images/" + path));
        Assert.Equal(new MediaTemplate("images/" + path, "  Literal {player:name}  "), Media(result));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    public void Compiles_extensionless_paths(string extension)
    {
        var result = StoryCompiler.Compile(Source("""{image("maps/map", "Map")}""", "images/maps/map" + extension));
        Assert.Equal(new MediaTemplate("images/maps/map" + extension, "Map"), Media(result));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(".png", ".jpg", ".jpeg", ".webp")]
    [InlineData(".jpg", ".jpeg", ".webp")]
    [InlineData(".jpeg", ".webp")]
    public void Ambiguity_warns_once_and_compiles_the_preferred_file(params string[] extensions)
    {
        var paths = extensions.Select(extension => "images/map" + extension).ToArray();
        var source = Source("""{image("map", "Map")}""", paths.Reverse().ToArray());
        var result = StoryCompiler.Compile(source);
        Assert.Equal(new MediaTemplate(paths[0], "Map"), Media(result));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("TW2013", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains(string.Join(", ", paths), warning.Message);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("", "old_map-v2", "Old map v2")]
    [InlineData(", \"\"", "old_map-v2", "Old map v2")]
    [InlineData(", \"  \"", "old_map-v2", "Old map v2")]
    [InlineData("", "---", "Image")]
    [InlineData("", "room42", "Room 42")]
    public void Missing_alt_compiles_effective_fallback_and_warns_once(string altArgument, string path, string expected)
    {
        var source = Source($"{{image(\"{path}\"{altArgument})}}", $"images/{path}.png");
        var result = StoryCompiler.Compile(source);
        Assert.Equal(new MediaTemplate($"images/{path}.png", expected), Media(result));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("TW2014", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("../map.png", "TW2010")]
    [InlineData("/map.png", "TW2010")]
    [InlineData("https://example.com/map.png", "TW2010")]
    [InlineData("maps\\\\map.png", "TW2010")]
    [InlineData("map.bmp", "TW2011")]
    [InlineData("missing.png", "TW2012")]
    [InlineData("Map.png", "TW2012")]
    public void Invalid_references_prevent_a_compiled_story(string encodedPath, string code)
    {
        var source = Source($"{{image(\"{encodedPath}\", \"Map\")}}", "images/map.png", "images/map.bmp");
        var result = StoryCompiler.Compile(source);
        Assert.False(result.Success);
        Assert.Null(result.Story);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(new SourceLocation("chapters/main.tale", 2, 8), error.Location);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Theory]
    [InlineData("""🦑 {player:name} {image("missing", "Map")}""")]
    [InlineData("""  > player: 🦑 {image("missing", "Map")}""")]
    [InlineData("""  * [🦑 Pick {image("missing", "Map")}] -> END""")]
    [InlineData("""**{image("missing", "Map")}**""")]
    [InlineData("""{if false}{image("missing", "Map")}{/if}""")]
    [InlineData("""`{image("missing", "Map")}`""")]
    public void Diagnostics_use_mapped_narrative_positions_even_after_substitutions(string line)
    {
        var source = Source(line);
        var files = source.Files.ToDictionary();
        files["chapters/main.tale"] = files["chapters/main.tale"].Replace("\n", "\r\n");
        source = new(files);
        var result = StoryCompiler.Compile(source);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "TW2012");
        Assert.Equal(new SourceLocation("chapters/main.tale", 2,
            line.IndexOf("\"missing", StringComparison.Ordinal) + 1), diagnostic.Location);
        Assert.Equal(result.Diagnostics, StoryAnalyzer.Analyze(source).Diagnostics);
    }

    [Fact]
    public void Multiline_path_location_points_to_the_authored_quote()
    {
        var result = StoryCompiler.Compile(Source("{image(\n  \"missing\", \"Map\")}"));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("TW2012", error.Code);
        Assert.Equal(new SourceLocation("chapters/main.tale", 3, 3), error.Location);
    }

    [Fact]
    public void Escaped_quote_is_decoded_once_before_lookup()
    {
        var result = StoryCompiler.Compile(Source("""{image("a\"b", "He said \"Go\"; \\ remains.")}""", "images/a\"b.png"));
        Assert.Equal(new MediaTemplate("images/a\"b.png", "He said \"Go\"; \\ remains."), Media(result));
        Assert.Empty(result.Diagnostics);
    }
}
