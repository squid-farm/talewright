using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class StandaloneFactAttribute : FactAttribute
{
    public StandaloneFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1")
        {
            Skip = "Set TALEWRIGHT_STANDALONE=1 and point the harness at an extracted all-in-one publish artifact.";
        }
    }
}

[Trait("Artifact", "Standalone")]
public sealed class StandaloneArtifactTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    protected override bool StartsInLibrary => false;

    [StandaloneFact]
    public async Task Selected_story_autostarts_plays_saves_and_reloads_without_other_samples()
    {
        var title = Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_TITLE") ?? "The Lost Relic";
        var creation = Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE_CREATION") == "1";
        if (creation)
        {
            await Driver.ClickAsync("Start story");
        }
        await AssertState(state =>
        {
            Assert.True(state.PassageVisible);
            Assert.Equal(title, state.WindowTitle);
            Assert.NotEmpty(state.Choices);
        });
        var first = await Driver.ObserveAsync();
        if (creation)
        {
            Assert.Contains(Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_CHARACTER") ?? "Tester", first.Passage);
        }
        await Choose(first.Choices[0]);
        await AssertState(state => Assert.NotEqual(first.Passage, state.Passage));
        var checkpoint = await Driver.ObserveAsync();
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await AssertState(state => Assert.Equal(checkpoint.Passage, state.Passage));
        if (PlayerTarget.IsDesktop)
        {
            await Driver.DisposeAsync();
            await Driver.StartAsync();
        }
        else
        {
            await Page.ReloadAsync();
        }

        if (creation)
        {
            await Driver.ClickAsync("Start story");
        }
        await AssertState(state => Assert.Equal(first.Passage, state.Passage));
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Load My save");
        await AssertState(state => Assert.Equal(checkpoint.Passage, state.Passage));
        await Driver.ClickAsync("Back");
        await AssertState(state => Assert.Equal(first.Passage, state.Passage));
        await Library();
        await AssertState(state => Assert.Equal("Talewright", state.WindowTitle));
        if (!PlayerTarget.IsDesktop)
        {
            await Expect(Page.Locator(".tw-story-list li")).ToHaveCountAsync(1);
            await Expect(Page.Locator(".tw-story-list")).ToContainTextAsync(title);
            Assert.Equal("rgb(250, 248, 243)", await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        }
    }
}
