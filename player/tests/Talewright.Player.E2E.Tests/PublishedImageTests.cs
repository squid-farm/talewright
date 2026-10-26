using System.Diagnostics;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class PublishedImagesFactAttribute : FactAttribute
{
    public PublishedImagesFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1" ||
            Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_TITLE") != "Folder images")
            Skip = "Point the standalone harness at a published ImageStoryFiles artifact.";
    }
}

[Trait("Artifact", "Standalone")]
public sealed class PublishedImageTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    protected override bool StartsInLibrary => false;

    [PublishedImagesFact]
    public async Task Published_image_formats_and_narrative_surfaces_decode_at_the_page_base()
    {
        await AssertState(state => Assert.Contains("Before", state.Passage));
        await AssertState(state => Assert.Contains("Hello from supporting configuration.", state.Passage));
        var decoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alt in new[] { "A camp", "A pixel", "Strong camp", "Emphasized camp", "Code pixel", "Conditional camp" })
        {
            if (Driver is NativePlayerDriver native)
            {
                await native.ScrollToImageAsync(alt);
                var timer = Stopwatch.StartNew();
                while (!(await native.SnapshotAsync()).Descendants().Any(node =>
                    node.Role == "image" && node.Name == alt &&
                    (alt is "A pixel" or "Code pixel"
                        ? node.Bounds.Width is > 0 and <= 2 && node.Bounds.Height is > 0 and <= 2
                        : node.Bounds.Width > 100 && node.Bounds.Height > 100)))
                {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Published {alt} decoded.");
                    await Task.Delay(100);
                }
                continue;
            }
            var image = Page.GetByAltText(alt, new() { Exact = true });
            await image.ScrollIntoViewIfNeededAsync();
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await image.ElementHandleAsync());
            decoded[alt] = (await image.GetAttributeAsync("src"))!;
            Assert.StartsWith("blob:", decoded[alt]);
        }
        if (PlayerTarget.IsDesktop)
        {
            await Choose((await Driver.ObserveAsync()).Choices[0]);
            await AssertState(state => Assert.Contains("Next passage.", state.Passage));
            await Driver.ClickAsync("Back");
            await AssertState(state => Assert.Contains("Before", state.Passage));
            return;
        }
        Assert.Equal(decoded["A camp"], decoded["Strong camp"]);
        Assert.Equal(decoded["A camp"], decoded["Conditional camp"]);
        await Page.SetViewportSizeAsync(390, 844);
        var camp = Page.GetByAltText("A camp", new() { Exact = true });
        await camp.ScrollIntoViewIfNeededAsync();
        Assert.True(await camp.EvaluateAsync<bool>("image => image.getBoundingClientRect().width <= 390"));
        await Choices.First.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("Next passage.");
        var next = Page.GetByAltText("Next camp", new() { Exact = true });
        await next.ScrollIntoViewIfNeededAsync();
        await Expect(next).ToHaveAttributeAsync("src", decoded["A camp"]);
        await Driver.ClickAsync("Back");
        await Expect(camp).ToHaveAttributeAsync("src", decoded["A camp"]);
    }
}
