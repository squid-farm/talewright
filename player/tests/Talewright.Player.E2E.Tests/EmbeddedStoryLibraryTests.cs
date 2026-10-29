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
        Assert.Equal(new[] { "dream/dream.talewright.yaml", StorySource.ManifestFileName },
            source.Files.Keys.Where(path => path.EndsWith(".yaml")).Order());
        Assert.Equal(new[]
        {
            "chapters/01-camp.tale", "chapters/02-trail.tale",
            "chapters/03-temple.tale", "chapters/04-endings.tale",
            "dream/dream.tale",
        }, source.Files.Keys.Where(path => path.EndsWith(".tale")).Order());
        var expected = new[]
        {
            "images/camp-art.jpeg",
            "images/dream-hall.jpeg",
            "images/expedition-workshop.jpeg",
            "images/jaguar.jpeg",
            "images/night-watch.jpeg",
            "images/relic-altar.jpeg",
            "images/river-crossing.jpg",
            "images/sleeper-high.jpeg",
            "images/sleeper-low.jpeg",
            "images/sleeper-middle.jpeg",
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
        Assert.Equal("Tomas Okafor", result.Story!.World.Characters[new("guide")].FullName);
        Assert.Equal("0.4.0", result.Story.Version);
        Assert.Equal(8, result.Story.World.Characters.Count);
        Assert.Contains(result.Story.Scenes, scene => scene.Id.Value == "dream.sleeper");
        Assert.Contains(await library.ListAsync(), story => story.Id == "LostRelic");
    }
}
