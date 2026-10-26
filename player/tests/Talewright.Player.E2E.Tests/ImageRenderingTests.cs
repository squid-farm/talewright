using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class ImageRenderingTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Embedded_images_decode_remain_in_history_and_release_only_after_successful_replacement()
    {
        await Page.EvaluateAsync("""
            () => {
                window.imageRevocations = [];
                const revoke = URL.revokeObjectURL.bind(URL);
                URL.revokeObjectURL = url => { window.imageRevocations.push(url); revoke(url); };
            }
            """);
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        var image = Page.GetByRole(AriaRole.Img, new() { Name = "A riverside expedition camp with canvas tents and supplies at the edge of dense jungle.", Exact = true });
        await image.ScrollIntoViewIfNeededAsync();
        await Expect(image).ToHaveJSPropertyAsync("complete", true);
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await image.ElementHandleAsync());
        await Page.SetViewportSizeAsync(390, 844);
        await image.ScrollIntoViewIfNeededAsync();
        var fits = await image.EvaluateAsync<bool>("""
            image => {
                const bounds = image.getBoundingClientRect();
                return bounds.width <= 390
                    && Math.abs(bounds.width / bounds.height - image.naturalWidth / image.naturalHeight) < 0.01;
            }
            """);
        Assert.True(fits, "The actual decoded image fits a narrow viewport without distorting its aspect ratio.");
        var url = await image.GetAttributeAsync("src");
        Assert.StartsWith("blob:", url);
        await Choices.First.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator(".tw-past img")).ToHaveAttributeAsync("src", url!);
        await Driver.ClickAsync("Back");
        await Expect(image).ToHaveAttributeAsync("src", url!);
        await Library();
        Assert.False(await Page.EvaluateAsync<bool>("url => window.imageRevocations.includes(url)", url));
        var invalid = new StoryFixture("bad-images", "game:\n  id: bad\n  title: Bad\n  start: missing\n", "=== start\n-> END");
        await Upload(invalid);
        await Expect(Page.Locator(".tw-library .tw-diagnostics")).ToBeVisibleAsync();
        await Page.Locator(".tw-resume").ClickAsync();
        await Expect(image).ToHaveAttributeAsync("src", url!);
        await Library();
        var text = new StoryFixture("replacement",
            "game:\n  id: replacement\n  title: Replacement\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            "=== start\nReplacement text.\n-> END");
        await Upload(text);
        await Expect(Latest).ToContainTextAsync("Replacement text.");
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.imageRevocations.filter(value => value === url).length", url));
    }

    [Fact]
    public async Task Repeated_embedded_history_occurrences_share_url_until_last_holder_is_removed()
    {
        await Page.EvaluateAsync("""
            () => {
                window.createdImages = [];
                window.revokedImages = [];
                const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
                URL.createObjectURL = blob => { const url = create(blob); window.createdImages.push(url); return url; };
                URL.revokeObjectURL = url => { window.revokedImages.push(url); revoke(url); };
            }
            """);
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await Choose("Continue");
        await Choose("Visit the expedition workshop");
        const string alt = "A sheltered camp worktable holds a rucksack, rations, rope and expedition records beside a hanging watch bell.";
        var images = Page.GetByAltText(alt, new() { Exact = true });
        await images.First.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await images.First.ElementHandleAsync());
        var url = await images.First.GetAttributeAsync("src");
        await Choose("Pack a trail lunch");
        await Choose("Eat one ration");
        await Choose("Continue");
        await Expect(images).ToHaveCountAsync(2);
        await images.Last.ScrollIntoViewIfNeededAsync();
        await Expect(images.Last).ToHaveAttributeAsync("src", url!);
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.createdImages.filter(value => value === url).length", url));
        await Driver.ClickAsync("Back");
        await Expect(images).ToHaveCountAsync(1);
        Assert.False(await Page.EvaluateAsync<bool>("url => window.revokedImages.includes(url)", url));
        await Driver.ClickAsync("Forward");
        await Expect(images).ToHaveCountAsync(2);
        await images.Last.ScrollIntoViewIfNeededAsync();
        await Expect(images.Last).ToHaveAttributeAsync("src", url!);
        await Library();
        var replacement = new StoryFixture("image-replacement",
            "game:\n  id: image-replacement\n  title: Replacement\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            "=== start\nReplacement.\n-> END");
        await Upload(replacement);
        await Expect(Latest).ToContainTextAsync("Replacement.");
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.revokedImages.filter(value => value === url).length", url));
    }

    [Fact]
    public async Task Undecodable_archive_images_keep_effective_alt_and_visible_accessible_failure()
    {
        var fixture = new StoryFixture("archive-images",
            "game:\n  id: archive-images\n  title: Archive images\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            """
            === start
            Before **{image("map", "A useful map")}** after.
            > player: *{image("map", "Dialogue map")}*
            * [Continue `{image("map", "Choice map")}`] -> END
            """, ["images/map.png"]);
        await Upload(fixture);
        foreach (var alt in new[] { "A useful map", "Dialogue map", "Choice map" })
            await Expect(Page.GetByRole(AriaRole.Img, new() { Name = alt, Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator(".tw-media-strong")).ToHaveCSSAsync("font-weight", "700");
        await Expect(Page.Locator(".tw-media-emphasis")).ToHaveCSSAsync("font-style", "italic");
        await Expect(Page.Locator(".tw-media-code")).ToHaveCSSAsync("font-family", "monospace");
        await Expect(Latest.GetByRole(AriaRole.Status)).ToContainTextAsync(
            ["This image could not be displayed.", "This image could not be displayed."]);
        await Choices.First.FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrapped/")]
    public async Task Split_configuration_archive_images_decode_share_history_and_release_on_replacement(string prefix)
    {
        using var files = new ImageStoryFiles();
        await Page.EvaluateAsync("""
            () => {
                window.archiveRevoked = [];
                const revoke = URL.revokeObjectURL.bind(URL);
                URL.revokeObjectURL = url => { window.archiveRevoked.push(url); revoke(url); };
            }
            """);
        await UploadBytes("illustrated-split.zip", files.Zip(prefix));
        await Expect(Latest).ToContainTextAsync("Before");
        var image = Page.GetByAltText("A camp", new() { Exact = true });
        await image.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await image.ElementHandleAsync());
        var url = await image.GetAttributeAsync("src");
        await Choices.First.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("Next passage.");
        var next = Page.GetByAltText("Next camp", new() { Exact = true });
        await next.ScrollIntoViewIfNeededAsync();
        await Expect(next).ToHaveAttributeAsync("src", url!);
        await Driver.ClickAsync("Back");
        await Expect(image).ToHaveAttributeAsync("src", url!);
        await Library();
        await UploadBytes("bad.zip", [1, 2, 3]);
        await Expect(Page.Locator(".tw-library")).ToContainTextAsync("Couldn't read");
        await Page.Locator(".tw-resume").ClickAsync();
        await Expect(image).ToHaveAttributeAsync("src", url!);
        Assert.False(await Page.EvaluateAsync<bool>("url => window.archiveRevoked.includes(url)", url));
        await Library();
        await Upload(new StoryFixture("text-replacement",
            "game:\n  id: replacement\n  title: Replacement\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            "=== start\nReplacement text.\n-> END"));
        await Expect(Latest).ToContainTextAsync("Replacement text.");
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.archiveRevoked.filter(value => value === url).length", url));
    }

    [Fact]
    public async Task Browser_transport_is_viewport_lazy_and_decodes_png_jpeg_and_webp()
    {
        using var files = new ImageStoryFiles();
        await Page.EvaluateAsync("""
            async () => {
                const module = await import("./_content/Talewright.Player/Components/MediaView.razor.js");
                const holder = document.createElement("div");
                holder.style.height = "5000px";
                document.body.append(holder);
                const image = document.createElement("img");
                image.alt = "Lazy transport test";
                image.style.minWidth = image.style.minHeight = "1px";
                document.body.append(image);
                window.imageRequests = 0;
                window.lazyImage = { image, holder, id: module.observe(image,
                    { invokeMethodAsync: async () => { window.imageRequests++; } }) };
            }
            """);
        await Page.WaitForTimeoutAsync(200);
        Assert.Equal(0, await Page.EvaluateAsync<int>("window.imageRequests"));
        await Page.GetByAltText("Lazy transport test").ScrollIntoViewIfNeededAsync();
        await Expect(Page.GetByAltText("Lazy transport test")).ToBeInViewportAsync();
        await Page.WaitForFunctionAsync("window.imageRequests === 1");
        await Page.EvaluateAsync("""
            async () => {
                const module = await import("./_content/Talewright.Player/Components/MediaView.razor.js");
                module.release(window.lazyImage.id);
                window.lazyImage.image.remove();
                window.lazyImage.holder.remove();
            }
            """);
        foreach (var (name, mime) in new[] { ("pixel.png", "image/png"), ("photo.jpeg", "image/jpeg"), ("pixel.webp", "image/webp") })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(files.Root, "images", name));
            var decoded = await Page.EvaluateAsync<bool>("""
                async ({base64, mime}) => {
                    const module = await import("./_content/Talewright.Player/Components/MediaView.razor.js");
                    const image = document.createElement("img");
                    document.body.append(image);
                    const id = module.observe(image, { invokeMethodAsync: async () => {} });
                    try {
                        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
                        return await module.load(id, { arrayBuffer: async () => bytes.buffer }, mime)
                            && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0;
                    } finally {
                        module.release(id);
                        image.remove();
                    }
                }
                """, new { base64 = Convert.ToBase64String(bytes), mime });
            Assert.True(decoded, name + " decoded in the actual browser.");
        }
    }
}
