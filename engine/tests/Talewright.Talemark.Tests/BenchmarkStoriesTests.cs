using System.Security.Cryptography;
using System.Text;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class BenchmarkStoriesTests
{
    [Theory]
    [InlineData(100, 11_881, "01E70260A966976812789CEE03AB42AC872919D547A11518D17126AB46F8B1F2")]
    [InlineData(1_000, 120_781, "BC78FD4DCBC2E8A88CDC64C063A25CE03455A8745CC8BB64EB4161B1245E068A")]
    [InlineData(10_000, 1_236_781, "6F9A7F38050BD5CD2E42006069E5BDE8972F2719460B383F1F9050F08B3B5BD5")]
    public void Reference_scene_loops_preserve_their_source_bytes(int scenes, int expectedBytes, string expectedHash)
    {
        var source = BenchmarkStories.CreateSceneLoop(scenes);

        Assert.Equal("""
            game:
              id: benchmark
              title: Benchmark
              start: scene.0
              seed: 42
            characters:
              player: {first-name: Tester}
            """, source.Files[StorySource.ManifestFileName]);
        Assert.Equal(expectedBytes, PerformanceSource.Utf8ByteCount(source));
        var tale = source.Files[BenchmarkStories.TaleFileName];
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tale))));
    }

    [Fact]
    public void Scene_loop_compiles_with_one_choice_per_scene()
    {
        var result = StoryCompiler.Compile(BenchmarkStories.CreateSceneLoop(5));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(5, result.Story!.Scenes.Count);
        Assert.All(result.Story.Scenes, scene => Assert.Single(scene.Choices));
    }

    [Fact]
    public void Scene_loop_requires_a_positive_scene_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStories.CreateSceneLoop(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkStories.CreateSceneLoop(-1));
    }
}
