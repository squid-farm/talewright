using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class PerformanceCorpusHubTests
{
    [Fact]
    public void Transition_from_last_scene_enters_hub_and_generates_twenty_ranked_storylets()
    {
        var tier = PerformanceCorpusTier.Get("Typical");
        var root = Path.Combine(Path.GetTempPath(), $"talewright-hub-test-{Guid.NewGuid():N}");
        using var corpus = PerformanceCorpus.Create(tier, root);
        var files = StorySource.FromDirectory(corpus.Root).Files.ToDictionary(pair => pair.Key, pair => pair.Value);
        files[StorySource.ManifestFileName] = files[StorySource.ManifestFileName]
            .Replace("start: scene.0", $"start: scene.{tier.SceneCount - 1}", StringComparison.Ordinal);
        var result = StoryCompiler.Compile(new StorySource(files));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!);
        Assert.DoesNotContain(history.Current.Choices, choice =>
            choice.Key.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal));

        history.Choose(history.Current.Choices[0].Key);

        Assert.Equal("scene.0", history.Current.Scene?.Value);
        Assert.Equal(1, history.Cursor);
        var generated = history.Current.Choices.Where(choice =>
            choice.Key.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(20, generated.Length);
        var expected = result.Story!.Scenes.Where(scene => scene.Id.Value != "scene.0")
            .OrderByDescending(scene => scene.Priority).Take(20)
            .Select(scene => StorySession.StoryletKeyPrefix + scene.Id.Value);
        Assert.Equal(expected, generated.Select(choice => choice.Key));
    }
}
