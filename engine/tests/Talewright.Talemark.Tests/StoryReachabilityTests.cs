using System.Text.RegularExpressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StoryReachabilityTests
{
    private static IEnumerable<string> Unreachable(StoryDefinition story)
    {
        return story.Scenes.Where(scene => !story.Reachability.IsReached(scene.Id)).Select(scene => scene.Id.Value);
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
        var manifest = TestStories.DefaultManifest();
        manifest.Flag("ready");
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
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

    [Fact]
    public void Compiled_story_keeps_the_graph_built_for_TW3009()
    {
        var story = TestStories.All[0].Compile();
        var reachability = story.Reachability;

        Assert.Same(reachability, story.Reachability);
        Assert.Same(reachability.Diverts, StoryDiverts.From(story));
        Assert.Same(StoryDiverts.From(story), StoryDiverts.From(story));

        var copy = new StoryDefinition
        {
            Id = story.Id, Title = story.Title, Version = story.Version, Start = story.Start, Randomness = story.Randomness,
            World = story.World, Scenes = story.Scenes, Triggers = story.Triggers, Predicates = story.Predicates,
        };
        Assert.NotSame(reachability, copy.Reachability);
        Assert.Same(copy.Reachability, copy.Reachability);
        Assert.Equal(reachability.Diverts.Diverts.Select(divert => divert.Id), StoryDiverts.From(copy).Diverts.Select(divert => divert.Id));
    }
}
