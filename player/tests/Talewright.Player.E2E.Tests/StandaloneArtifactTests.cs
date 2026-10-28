using System.Diagnostics;
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
            await AssertLibraryUnavailable(duringCreation: true);
            await Driver.ClickAsync("Start story");
        }
        await AssertState(state =>
        {
            Assert.True(state.PassageVisible);
            Assert.Equal(title, state.WindowTitle);
            Assert.NotEmpty(state.Choices);
        });
        await AssertLibraryUnavailable();
        var first = await Driver.ObserveAsync();
        if (title == "The Lost Relic")
        {
            Assert.Contains("Tomas", first.Passage);
            const string alt = "A riverside expedition camp with canvas tents and supplies at the edge of dense jungle.";
            if (Driver is NativePlayerDriver imageDriver)
            {
                await imageDriver.ScrollToImageAsync(alt);
                var timer = Stopwatch.StartNew();
                while (!(await imageDriver.SnapshotAsync()).Descendants().Any(node =>
                    node.Role == "image" && node.Name == alt && node.Bounds.Width > 100 && node.Bounds.Height > 100))
                {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Published image decoded in the native host.");
                    await Task.Delay(100);
                }
            }
            else
            {
                var image = Page.GetByAltText(alt, new() { Exact = true });
                await Expect(image).ToHaveCountAsync(1);
                await image.ScrollIntoViewIfNeededAsync();
                await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                    await image.ElementHandleAsync());
                Assert.StartsWith("blob:", await image.GetAttributeAsync("src"));
            }
        }
        if (creation)
        {
            Assert.Contains(Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_CHARACTER") ?? "Tester", first.Passage);
        }
        await Choose(first.Choices[0]);
        await AssertState(state => Assert.NotEqual(first.Passage, state.Passage));
        var checkpoint = await Driver.ObserveAsync();
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await AssertState(state => Assert.Equal(checkpoint.Passage, state.Passage));
        await Driver.ClickAsync("Quick Save");
        if (Driver is NativePlayerDriver native)
        {
            var timer = Stopwatch.StartNew();
            while (!(await native.SnapshotAsync()).Content.Contains("Quick Save checkpoint saved.", StringComparison.Ordinal))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Quick Save completed before restarting the process.");
                await Task.Delay(100);
            }
        }
        else
        {
            await Expect(SaveStatus).ToContainTextAsync("Quick Save checkpoint saved.");
        }
        if (PlayerTarget.IsDesktop)
        {
            await Driver.DisposeAsync();
            await Driver.StartAsync();
        }
        else
        {
            await ReloadAsync();
        }

        if (creation)
        {
            await AssertLibraryUnavailable(duringCreation: true);
            await Driver.ClickAsync("Start story");
        }
        await AssertState(state => Assert.Equal(first.Passage, state.Passage));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Quick Save checkpoint");
        await Driver.ClickAsync("Load save");
        await AssertState(state => Assert.Equal(checkpoint.Passage, state.Passage));
        await Driver.ClickAsync("Back");
        await AssertState(state => Assert.Equal(first.Passage, state.Passage));
        await Restart();
        await AssertState(state => Assert.Equal(first.Passage, state.Passage));
        await AssertLibraryUnavailable();
        if (!PlayerTarget.IsDesktop)
        {
            Assert.Equal("rgb(250, 248, 243)", await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        }
    }
}
