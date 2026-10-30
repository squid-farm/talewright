using System.Globalization;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class StoryImageResolverTests
{
    private static readonly SourceLocation Location = new("chapters/camp.tale", 12, 7);

    private static StorySource Source(params string[] images) => new(new Dictionary<string, string>(), images);

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    public void Supported_formats_resolve_with_or_without_extension(string extension)
    {
        var source = Source("images/maps/old-map" + extension);
        foreach (var path in new[] { "maps/old-map", "maps/old-map" + extension })
        {
            var diagnostics = new DiagnosticBag();
            var result = StoryImageResolver.Resolve(source, path, "The old map.", Location, diagnostics);
            Assert.Equal(new ResolvedStoryImage("images/maps/old-map" + extension, "The old map."), result);
            Assert.Empty(diagnostics.Items);
        }
    }

    [Theory]
    [InlineData(".png", ".jpg", ".jpeg", ".webp")]
    [InlineData(".jpg", ".jpeg", ".webp")]
    [InlineData(".jpeg", ".webp")]
    public void Ambiguous_extensionless_paths_warn_and_use_priority_order(params string[] extensions)
    {
        var paths = extensions.Select(ext => "images/map" + ext).ToArray();
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(Source(Enumerable.Reverse(paths).ToArray()), "map", "A map.", Location, diagnostics);

        Assert.Equal(paths[0], result?.Path);
        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal("TW2013", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Location, warning.Location);
        Assert.Contains(string.Join(", ", paths), warning.Message);
        Assert.Contains($"Using '{paths[0]}'", warning.Message);
        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void Explicit_extension_does_not_warn_or_choose_a_different_format()
    {
        var diagnostics = new DiagnosticBag();
        var source = Source("images/map.png", "images/map.jpg", "images/map.jpeg", "images/map.webp");
        var result = StoryImageResolver.Resolve(source, "map.webp", "A map.", Location, diagnostics);
        Assert.Equal("images/map.webp", result?.Path);
        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public void Explicit_extension_recognizes_the_format_but_matches_the_filename_case_exactly()
    {
        var source = Source("images/map.PNG");
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(source, "map.PNG", "Map.", Location, diagnostics);
        Assert.Equal("images/map.PNG", result?.Path);
        Assert.Empty(diagnostics.Items);
        AssertError(source, "map.png", "TW2012", "case-sensitive");
        AssertError(source, "map", "TW2012", "case-sensitive");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing.png")]
    [InlineData("map.jpg")]
    [InlineData("Map.png")]
    [InlineData("MAP")]
    [InlineData("Maps/map.png")]
    [InlineData("outside")]
    public void Missing_files_and_case_mismatches_are_errors(string path)
    {
        var source = Source("images/map.png", "images/maps/map.png", "outside.png", "Images/outside.png");
        AssertError(source, path, "TW2012", "case-sensitive");
    }

    [Theory]
    [InlineData("map.gif")]
    [InlineData("map.svg")]
    [InlineData("map.bmp")]
    [InlineData("map.txt")]
    public void Unsupported_formats_are_errors_even_if_present(string path) =>
        AssertError(Source("images/" + path), path, "TW2011", ".png, .jpg, .jpeg, or .webp");

    [Fact]
    public void Extensionless_lookup_does_not_select_unsupported_files() =>
        AssertError(Source("images/map.gif"), "map", "TW2012", "not found");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../map.png")]
    [InlineData("maps/../map.png")]
    [InlineData("maps/../../map.png")]
    [InlineData("/map.png")]
    [InlineData("C:/map.png")]
    [InlineData("C:map.png")]
    [InlineData(@"C:\map.png")]
    [InlineData(@"\\server\map.png")]
    [InlineData(@"maps\map.png")]
    [InlineData("https://example.com/map.png")]
    [InlineData("http://example.com/map.png")]
    [InlineData("//example.com/map.png")]
    [InlineData("file:///map.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("./map.png")]
    [InlineData("maps//map.png")]
    [InlineData("maps/")]
    [InlineData("map.png?size=1")]
    [InlineData("map.png#fragment")]
    [InlineData("map\0.png")]
    [InlineData("map\n.png")]
    public void Unsafe_paths_never_resolve_even_if_catalogued(string path) =>
        AssertError(Source("images/" + path), path, "TW2010", "relative path inside images/");

    [Theory]
    [InlineData("old_map-v2.png", "Old map v2")]
    [InlineData("map123.png", "Map 123")]
    [InlineData("123.png", "123")]
    [InlineData("123_map.png", "123 map")]
    [InlineData("NASA_map.png", "Nasa map")]
    [InlineData("HTTPServerMap.png", "Http server map")]
    [InlineData("NASA.png", "Nasa")]
    [InlineData("old___map---v2.png", "Old map v2")]
    [InlineData("old...map.png", "Old map")]
    [InlineData("nested/oldMap.png", "Old map")]
    [InlineData("!!!.png", "Image")]
    [InlineData("__--.png", "Image")]
    public void Missing_alt_text_warns_and_generates_exact_sentence_case(string path, string expected)
    {
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(Source("images/" + path), path, null, Location, diagnostics);
        Assert.Equal(expected, result?.AltText);
        AssertMissingAltWarning(diagnostics, expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_or_whitespace_alt_text_is_missing(string altText)
    {
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(Source("images/map.png"), "map", altText, Location, diagnostics);
        Assert.Equal("Map", result?.AltText);
        AssertMissingAltWarning(diagnostics, "Map");
    }

    [Theory]
    [InlineData("  The OLD map!  ")]
    [InlineData("NASA's map, v2.")]
    [InlineData("Map\nwith a second line.")]
    public void Author_written_alt_text_is_preserved_exactly(string altText)
    {
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(Source("images/map.png"), "map", altText, Location, diagnostics);
        Assert.Equal(altText, result?.AltText);
        Assert.Empty(diagnostics.Items);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Fallback_is_independent_of_computer_language(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach (var (filename, expected) in new[]
            {
                ("indigo_ISLAND-v2.png", "Indigo island v2"),
                ("indigoMap.png", "Indigo map"),
                ("island.png", "Island"),
                ("NASA_map123.png", "Nasa map123"),
                ("old___map---v2.png", "Old map v2"),
                ("!!!.png", "Image"),
            })
            {
                var diagnostics = new DiagnosticBag();
                var result = StoryImageResolver.Resolve(Source("images/" + filename), filename, null, Location, diagnostics);
                Assert.Equal(expected, result?.AltText);
                AssertMissingAltWarning(diagnostics, expected);
                Assert.Equal(culture, CultureInfo.CurrentCulture.Name);
                Assert.Equal(culture, CultureInfo.CurrentUICulture.Name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Ambiguity_and_missing_alt_text_both_warn_at_the_reference()
    {
        var diagnostics = new DiagnosticBag();
        var result = StoryImageResolver.Resolve(Source("images/map.webp", "images/map.png"), "map", null, Location, diagnostics);
        Assert.Equal(new ResolvedStoryImage("images/map.png", "Map"), result);
        Assert.Equal(["TW2013", "TW2014"], diagnostics.Items.Select(d => d.Code));
        Assert.All(diagnostics.Items, d =>
        {
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
            Assert.Equal(Location, d.Location);
        });
    }

    private static void AssertError(StorySource source, string path, string code, string message)
    {
        var diagnostics = new DiagnosticBag();
        Assert.Null(StoryImageResolver.Resolve(source, path, null, Location, diagnostics));
        var error = Assert.Single(diagnostics.Items);
        Assert.Equal(code, error.Code);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(Location, error.Location);
        Assert.Contains(message, error.Message);
        Assert.True(diagnostics.HasErrors);
    }

    private static void AssertMissingAltWarning(DiagnosticBag diagnostics, string fallback)
    {
        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal("TW2014", warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Location, warning.Location);
        Assert.Contains($"Using '{fallback}'", warning.Message);
        Assert.False(diagnostics.HasErrors);
    }
}
