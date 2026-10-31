using Talewright.Core.Identity;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class RealisticStoryTests
{
    private static readonly RealisticStoryProfile Profile = new();

    [Fact]
    public void Same_profile_and_seed_generate_identical_files()
    {
        var first = RealisticStory.Create(Profile);
        var second = RealisticStory.Create(Profile);
        var reseeded = RealisticStory.Create(Profile with { Seed = Profile.Seed + 1 });

        Assert.Equal(first.Files, second.Files);
        Assert.NotEqual(first.Files, reseeded.Files);
    }

    [Theory]
    [InlineData(20, 900, 1_100)]
    [InlineData(200, 9_000, 11_000)]
    public void Story_compiles_and_only_warns_about_its_cut_scenes(int chapters, int minScenes, int maxScenes)
    {
        var generated = RealisticStory.Create(Profile with { Chapters = chapters });
        var result = StoryCompiler.Compile(generated.ToSource());

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Take(5)));
        Assert.InRange(result.Story!.Scenes.Count, minScenes, maxScenes);
        Assert.Equal(generated.SceneCount, result.Story.Scenes.Count);
        Assert.Equal(chapters / Profile.CutSceneEveryChapters, generated.CutSceneCount);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal("TW3009", diagnostic.Code);
            Assert.Contains(".cut'", diagnostic.Message);
        });
        Assert.Equal(generated.CutSceneCount, result.Diagnostics.Count);
    }

    [Fact]
    public void Generated_story_keeps_a_realistic_shape()
    {
        var generated = RealisticStory.Create(Profile);
        var story = StoryCompiler.Compile(generated.ToSource()).Story!;
        var map = StoryDiverts.From(story);
        var divertsFrom = map.Diverts.ToLookup(divert => divert.From);
        bool DivertsTo(SceneId from, SceneId to) => divertsFrom[from].Any(divert => divert.Target.Scene == to);

        var writtenChoices = story.Scenes
            .Select(scene => divertsFrom[scene.Id].Select(divert => divert.ChoiceKey).OfType<string>().Distinct().Count())
            .ToArray();
        Assert.True(writtenChoices.Count(count => count <= 1) >= 0.6 * writtenChoices.Length);
        Assert.InRange(writtenChoices.Max(), 2, 8);

        var hubs = story.Scenes.Where(scene => scene.Hub is not null).Select(scene => scene.Id).ToArray();
        Assert.InRange(hubs.Length, 0.02 * story.Scenes.Count, 0.10 * story.Scenes.Count);
        var offers = map.HubOffers.ToLookup(offer => offer.Hub);
        Assert.All(hubs, hub =>
        {
            Assert.InRange(offers[hub].Count(), 1, 50);
            Assert.Contains(offers[hub], offer => DivertsTo(offer.Scene, hub));
        });

        var retries = map.Diverts.Where(divert =>
            divert.Outcome == SkillCheckOutcome.Failure && divert.Target.Scene is { } retry && DivertsTo(retry, divert.From));
        Assert.NotEmpty(retries);

        var endings = map.Diverts.Where(divert => divert.Target.Scene is null).Select(divert => divert.From).Distinct();
        Assert.True(endings.Count() >= 4);

        Assert.Contains(map.Diverts, divert => divert.ChoiceRequires is not null);
        Assert.Contains(map.Diverts, divert => divert.BlockConditions.Count > 0);
        Assert.Contains(story.Scenes, scene => scene.Once);
        Assert.InRange(
            generated.ProseBytes / (double)generated.SceneCount,
            Profile.WordsPerScene.Min * 4,
            Profile.WordsPerScene.Max * 10);
    }

    [Fact]
    public void Playthrough_from_the_start_reaches_an_ending()
    {
        var story = StoryCompiler.Compile(RealisticStory.Create(Profile).ToSource()).Story!;
        var history = new StoryHistory(story, (ulong)Profile.Seed);

        for (var step = 0; step < 10_000 && !history.Current.IsEnded; step++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        Assert.True(history.Current.IsEnded);
    }
}
