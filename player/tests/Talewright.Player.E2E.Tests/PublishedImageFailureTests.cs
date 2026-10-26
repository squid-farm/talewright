using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class PublishedImageFailureFactAttribute : FactAttribute
{
    public PublishedImageFailureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1" ||
            Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_TITLE") != "Broken images")
            Skip = "Point the standalone harness at a published Broken images artifact.";
    }
}

[Trait("Artifact", "Standalone")]
public sealed class PublishedImageFailureTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    protected override bool StartsInLibrary => false;

    [PublishedImageFailureFact]
    public async Task Undecodable_published_image_has_accessible_failure_without_blocking_other_images_or_play()
    {
        await AssertState(state => Assert.Contains("Before", state.Passage));
        if (Driver is NativePlayerDriver native)
        {
            await native.ScrollToImageAsync("Broken map");
            var timer = Stopwatch.StartNew();
            while (!(await native.SnapshotAsync()).Content.Contains("This image could not be displayed.", StringComparison.Ordinal))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Published decode failure is accessible.");
                await Task.Delay(100);
            }
            await native.ScrollToImageAsync("Working camp");
            timer.Restart();
            while (!(await native.SnapshotAsync()).Descendants().Any(node =>
                node.Role == "image" && node.Name == "Working camp" && node.Bounds.Height > 100))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Other published images still decode.");
                await Task.Delay(100);
            }
        }
        else
        {
            var broken = Page.GetByRole(AriaRole.Img, new() { Name = "Broken map", Exact = true });
            await broken.ScrollIntoViewIfNeededAsync();
            await Expect(broken).ToBeVisibleAsync();
            await Expect(Latest.GetByRole(AriaRole.Status)).ToContainTextAsync("This image could not be displayed.");
            var working = Page.GetByAltText("Working camp", new() { Exact = true });
            await working.ScrollIntoViewIfNeededAsync();
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                await working.ElementHandleAsync());
        }
        await Choose("Continue");
        await AssertState(state => Assert.Contains("Next passage.", state.Passage));
    }
}
