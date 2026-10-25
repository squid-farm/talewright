using Talewright.Player.Library;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.E2E.Tests;

public sealed class EmbeddedStoryLibraryTests
{
    [Fact]
    public async Task Bundled_images_map_to_real_resources_and_compile_without_becoming_text_files()
    {
        var assembly = typeof(EmbeddedStoryLibraryTests).Assembly;
        var library = new EmbeddedStoryLibrary(assembly);
        var source = await library.LoadAsync("LostRelic");
        var expected = new[]
        {
            "images/camp-art.jpeg",
            "images/expedition-workshop.jpeg",
            "images/relic-altar.jpeg",
            "images/temple-door.jpeg",
        };
        Assert.Equal(expected, source.ImagePaths.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(source.Files.Keys, path => path.StartsWith("images/", StringComparison.Ordinal));
        foreach (var path in source.ImagePaths)
        {
            var resource = assembly.GetManifestResourceInfo("stories/LostRelic/" + path);
            Assert.NotNull(resource);
        }
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Contains(await library.ListAsync(), story => story.Id == "LostRelic");
    }
}
