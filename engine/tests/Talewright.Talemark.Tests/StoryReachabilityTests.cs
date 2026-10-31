using System.Text.RegularExpressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StoryReachabilityTests
{
    private static IEnumerable<string> Unreachable(StoryDefinition story)
    {
        var reachability = StoryReachability.Find(story.Scenes, StoryDiverts.From(story), story.Start);
        return story.Scenes.Where(scene => !reachability.IsReached(scene.Id)).Select(scene => scene.Id.Value);
    }

    private static IEnumerable<string> Tw3009(CompilationResult result) => result.Warnings
        .Where(diagnostic => diagnostic.Code == "TW3009")
        .Select(diagnostic => Regex.Match(diagnostic.Message, "^Scene '(.+)' can never").Groups[1].Value);

    [Fact]
    public void Shared_walk_and_TW3009_agree_on_every_sample_story()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var sources = TestStories.All.Select(fixture => fixture.Source())
            .Prepend(RealisticStory.Create(new RealisticStoryProfile()).ToSource())
            .Prepend(StorySource.FromDirectory(Path.Combine(root.FullName, "samples", "LostRelic")));

        var unreachable = 0;
        foreach (var source in sources)
        {
            var result = StoryCompiler.Compile(source);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(Tw3009(result), Unreachable(result.Story!));
            unreachable += Tw3009(result).Count();
        }

        Assert.True(unreachable > 0, "At least one sample story should have an unreachable scene.");
    }

    [Fact]
    public void Follows_hub_offers_and_diverts_but_not_lines_that_can_never_run()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = TestStories.DefaultYaml + "\nflags: [ready]\n",
            ["main.tale"] = """
                === start
                @hub town
                {if flag:ready}
                -> start
                {/if}
                * [Leave] -> gate
                === inn
                @tags town
                -> END
                === gate
                -> END
                -> lost
                === lost
                -> END
                === orphan
                -> start
                """,
        }));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(["lost", "orphan"], Unreachable(result.Story!));
        Assert.Equal(["lost", "orphan"], Tw3009(result));
    }
}
