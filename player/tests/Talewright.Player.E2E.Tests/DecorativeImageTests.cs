using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class DecorativeImageTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Decorative_images_stay_visible_without_becoming_accessible_content()
    {
        using var files = new ImageStoryFiles();
        File.WriteAllText(files.TalePath, """
            === start
            You see a path.
            * [Take the trail {image("pixel.png")#decorative}] -> next
            === next
            You arrive. {image("photo.jpeg")#decorative}
            * [Finish] -> END
            """);
        File.WriteAllBytes(Path.Combine(files.Root, "images", "pixel.png"), [1, 2, 3]);

        await UploadBytes("decorative-images.zip", files.Zip());
        await Observe(new(null, "You see a path.", null));

        if (Driver is NativePlayerDriver native)
        {
            var before = await native.SnapshotAsync();
            var choice = Assert.Single(before.Descendants(),
                node => node.Role == "push button" && node.Name.Contains("Take the trail", StringComparison.Ordinal));
            Assert.DoesNotContain("Pixel", choice.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(before.Descendants(), node => node.Role == "image");
            Assert.DoesNotContain(before.Descendants(), node =>
                node.Role == "status" && node.Name.Contains("could not be displayed", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            await Expect(Page.Locator(".tw-media[aria-hidden='true']")).ToHaveCountAsync(1);
            await Expect(Page.Locator(".tw-media").GetByRole(AriaRole.Img)).ToHaveCountAsync(0);
            await Expect(Page.GetByRole(AriaRole.Button, new()
            {
                Name = "1. Take the trail",
                Exact = true,
            })).ToBeVisibleAsync();

            var error = Page.Locator(".tw-media-error");
            await Expect(error).ToBeVisibleAsync();
            await Expect(error.GetByRole(AriaRole.Status)).ToHaveCountAsync(0);
            await Expect(error).ToContainTextAsync("This image could not be displayed.");
        }

        if (Driver is NativePlayerDriver)
        {
            await Choose("Take the trail");
        }
        else
        {
            await Page.GetByRole(AriaRole.Button, new()
            {
                Name = "1. Take the trail",
                Exact = true,
            }).ClickAsync();
        }
        await Observe(new(null, "You arrive.", ["Finish"]));
        if (Driver is not NativePlayerDriver)
        {
            var photo = Page.Locator(".tw-media img").Last;
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                await photo.ElementHandleAsync());
            Assert.StartsWith("blob:", await photo.GetAttributeAsync("src"));
        }
        await Driver.ClickAsync("History");
        if (Driver is NativePlayerDriver nativeHistory)
        {
            var history = await nativeHistory.SnapshotAsync();
            Assert.Equal(2, history.Descendants().Count(node =>
                node.Role == "push button" && node.Name == "Reveal decorative image"));
            await nativeHistory.ClickAsync("Reveal decorative image");
            var afterReveal = await nativeHistory.SnapshotAsync();
            Assert.Contains("You arrive.", afterReveal.Content);
            Assert.Contains(afterReveal.Descendants(), node =>
                node.Role == "push button" && node.Name == "Hide decorative image");
            Assert.DoesNotContain(afterReveal.Descendants(), node => node.Role == "image");
        }
        else
        {
            var reveal = Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Reveal decorative image",
                Exact = true,
            });
            await Expect(reveal).ToHaveCountAsync(2);
            await reveal.First.ClickAsync();
            await Expect(Page.Locator(".tw-history-frame").Last).ToContainTextAsync("You arrive.");
            await Expect(Page.GetByRole(AriaRole.Button, new()
            {
                Name = "Hide decorative image",
                Exact = true,
            })).ToBeVisibleAsync();
            await Expect(Page.Locator(".tw-media").GetByRole(AriaRole.Img)).ToHaveCountAsync(0);
        }
    }
}
