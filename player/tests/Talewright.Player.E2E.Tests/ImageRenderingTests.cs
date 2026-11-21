using System.Diagnostics;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class ImageRenderingTests : PlayerTest
{
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public ImageRenderingTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }

    [Fact]
    public async Task Destination_image_warming_is_reused_and_offscreen_images_remain_lazy()
    {
        using var files = new ImageStoryFiles();
        files.EnlargeIllustrationAboveFormerArchiveLimit();
        var paragraphs = string.Join("\n\n", Enumerable.Repeat(
            "A reader studies the marked route and considers what might wait beyond the next ridge. " +
            "The path winds through old growth, following the river until the stones turn east.", 60));
        var destinationTale = $$"""
            === start
            Read this passage before choosing where to go.
            * [Continue to the destination] -> destination
            === destination
            {{paragraphs}}

            {image("photo.jpeg", "Destination map")}
            * [Finish] -> END
            """;
        var coldTale = $$"""
            === start
            Read this passage before loading the image.
            {{paragraphs}}

            {image("photo.jpeg", "Cold control map")}
            * [Finish] -> END
            """;
        var baseManifest = File.ReadAllText(Path.Combine(files.Root, "story.talewright.yaml"));

        await Page.RouteAsync("**/MediaView.razor.js", async route =>
        {
            var response = await route.FetchAsync();
            var script = await response.TextAsync();
            const string point = "const bytes = await stream.arrayBuffer();";
            Assert.Contains(point, script);
            script = script.Replace(point, """
                if (window.pauseWarmingTransport)
                    await new Promise(resolve => window.resumeWarmingTransport.push(resolve));
                const bytes = await stream.arrayBuffer();
                """, StringComparison.Ordinal);
            await route.FulfillAsync(new() { Response = response, Body = script });
        });

        await Page.EvaluateAsync("""
            () => {
                window.imageUrls = [];
                window.pauseWarmingTransport = false;
                window.resumeWarmingTransport = [];
                const create = URL.createObjectURL.bind(URL);
                URL.createObjectURL = blob => {
                    const url = create(blob);
                    if (blob.type.startsWith("image/")) window.imageUrls.push(url);
                    return url;
                };
            }
            """);

        File.WriteAllText(files.TalePath, coldTale);
        var coldStoryId = "warm-cold-baseline";
        File.WriteAllText(Path.Combine(files.Root, "story.talewright.yaml"),
            baseManifest.Replace("id: image-test", $"id: {coldStoryId}", StringComparison.Ordinal));
        var coldCompilation = StoryCompiler.Compile(StorySource.FromDirectory(files.Root));
        Assert.True(coldCompilation.Success, string.Join("\n", coldCompilation.Diagnostics));
        var coldHistory = new StoryHistory(coldCompilation.Story!, 42);
        await WriteBrowserSaveAsync(coldStoryId, "Cold baseline", coldHistory.Save());
        await UploadBytes(coldStoryId + ".zip", files.Zip());
        await Expect(Latest).ToContainTextAsync("Read this passage before loading the image.");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Cold baseline");
        var coldTimer = Stopwatch.StartNew();
        await Driver.ClickAsync("Load save");
        await Expect(SaveStatus).ToHaveTextAsync("Loaded 'Cold baseline'.");
        var coldImage = Page.GetByRole(AriaRole.Img, new() { Name = "Cold control map", Exact = true });
        var coldChoice = Choices.Last;
        Assert.Equal(0, await Page.EvaluateAsync<int>("window.imageUrls.length"));
        var coldChoiceTopBefore = await Page.EvaluateAsync<double>(
            "element => element.getBoundingClientRect().top + window.scrollY", await coldChoice.ElementHandleAsync());
        await coldImage.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
            await coldImage.ElementHandleAsync(), new() { Timeout = 30_000 });
        coldTimer.Stop();
        var coldChoiceTopAfter = await Page.EvaluateAsync<double>(
            "element => element.getBoundingClientRect().top + window.scrollY", await coldChoice.ElementHandleAsync());
        Assert.Equal(1, await Page.EvaluateAsync<int>("window.imageUrls.length"));
        var coldChoiceDisplacement = coldChoiceTopAfter - coldChoiceTopBefore;

        await Library();
        await Page.EvaluateAsync("() => { window.imageUrls = []; }");

        async Task<(long NavigationMilliseconds, long PassageMilliseconds, int WarmUrlsAtNavigation, double ChoiceDisplacement)> PlayAsync(
            string storyId, bool waitForWarm)
        {
            try
            {
                if (!waitForWarm)
                {
                    await Page.EvaluateAsync("""
                        () => {
                            window.pauseWarmingTransport = true;
                            window.resumeWarmingTransport = [];
                        }
                        """);
                }
                File.WriteAllText(files.TalePath, destinationTale);
                var manifestPath = Path.Combine(files.Root, "story.talewright.yaml");
                File.WriteAllText(manifestPath,
                    baseManifest.Replace("id: image-test", $"id: {storyId}", StringComparison.Ordinal));
                await UploadBytes(storyId + ".zip", files.Zip());
                await Expect(Latest).ToContainTextAsync("Read this passage before choosing where to go.");

                var warmTimer = Stopwatch.StartNew();
                if (waitForWarm)
                {
                    await Page.WaitForFunctionAsync("window.imageUrls.length === 1", null,
                        new() { Timeout = 30_000 });
                    await Task.Delay(500);
                }
                else
                {
                    await Page.WaitForFunctionAsync("window.resumeWarmingTransport.length === 1", null,
                        new() { Timeout = 30_000 });
                }
                var warmMilliseconds = warmTimer.ElapsedMilliseconds;
                var warmUrlsAtNavigation = await Page.EvaluateAsync<int>("window.imageUrls.length");
                if (!waitForWarm)
                    Assert.Equal(0, warmUrlsAtNavigation);

                var navigationTimer = Stopwatch.StartNew();
                await Choose("Continue to the destination");
                var image = Page.GetByRole(AriaRole.Img, new() { Name = "Destination map", Exact = true });
                await image.WaitForAsync(new() { State = WaitForSelectorState.Attached });
                Assert.True(await image.EvaluateAsync<bool>("element => element.getBoundingClientRect().top > innerHeight"));
                Assert.Null(await image.GetAttributeAsync("src"));
                var finish = Choices.Last;
                var finishTopBefore = await Page.EvaluateAsync<double>(
                    "element => element.getBoundingClientRect().top + window.scrollY", await finish.ElementHandleAsync());
                await image.ScrollIntoViewIfNeededAsync();
                if (!waitForWarm)
                {
                    await Expect(image).ToBeInViewportAsync();
                    await Task.Delay(100);
                    Assert.Equal(0, await Page.EvaluateAsync<int>("window.imageUrls.length"));
                    await Page.EvaluateAsync("""
                        () => {
                            const resume = window.resumeWarmingTransport.shift();
                            if (!resume) throw new Error("No pending image transfer to resume.");
                            resume();
                            window.pauseWarmingTransport = false;
                        }
                        """);
                }
                await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                    await image.ElementHandleAsync(), new() { Timeout = 30_000 });
                navigationTimer.Stop();
                var finishTopAfter = await Page.EvaluateAsync<double>(
                    "element => element.getBoundingClientRect().top + window.scrollY", await finish.ElementHandleAsync());
                Assert.Equal(1, await Page.EvaluateAsync<int>("window.imageUrls.length"));
                Assert.Equal(await Page.EvaluateAsync<string>("window.imageUrls[0]"), await image.GetAttributeAsync("src"));
                return (navigationTimer.ElapsedMilliseconds, warmMilliseconds, warmUrlsAtNavigation, finishTopAfter - finishTopBefore);
            }
            finally
            {
                if (!waitForWarm)
                {
                    await Page.EvaluateAsync("""
                        () => {
                            window.pauseWarmingTransport = false;
                            for (const resume of window.resumeWarmingTransport.splice(0)) resume();
                        }
                        """);
                }
            }
        }

        var immediate = await PlayAsync("warm-immediate", waitForWarm: false);
        await Library();
        await Page.EvaluateAsync("() => { window.imageUrls = []; }");
        var settled = await PlayAsync("warm-settled", waitForWarm: true);

        _output.WriteLine(
            $"Image warming measurement: story=local uploaded ZIP; host=published WebAssembly/{_host.BrowserName}; " +
            $"imageBytes={new FileInfo(files.ImagePath).Length}; HTTP image cache=not used; each trial used a new story owner/Blob URL; " +
            $"image transport=local archive uploads to loopback host; readingInterval=0ms immediate and 500ms settled; " +
            $"timing=cold save-load-to-decoded versus navigation-to-decoded; " +
            $"choice displacement=Finish button document-Y change before versus after image decode.");
        _output.WriteLine(
            $"Cold save restore: load-to-image-visible={coldTimer.ElapsedMilliseconds}ms, choice displacement={coldChoiceDisplacement:F1}px.");
        _output.WriteLine(
            $"Immediate: warmed URLs at navigation={immediate.WarmUrlsAtNavigation}, passage-visible-to-choice={immediate.PassageMilliseconds}ms, " +
            $"navigation-to-image-visible={immediate.NavigationMilliseconds}ms, choice displacement={immediate.ChoiceDisplacement:F1}px.");
        _output.WriteLine(
            $"Settled: warmed URLs at navigation={settled.WarmUrlsAtNavigation}, passage-visible-to-choice={settled.PassageMilliseconds}ms, " +
            $"navigation-to-image-visible={settled.NavigationMilliseconds}ms, choice displacement={settled.ChoiceDisplacement:F1}px.");
    }

    [Fact]
    public async Task Distinct_embedded_images_stay_mounted_on_departure_and_survive_rejected_replacement()
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
        await Expect(image).ToHaveAttributeAsync("src", url!);
        Assert.False(await Page.EvaluateAsync<bool>("url => window.imageRevocations.includes(url)", url));
        await Driver.ClickAsync("Back");
        await image.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await image.ElementHandleAsync());
        var restoredUrl = await image.GetAttributeAsync("src");
        Assert.Equal(url, restoredUrl);
        await Library();
        Assert.False(await Page.EvaluateAsync<bool>("url => window.imageRevocations.includes(url)", restoredUrl));
        var invalid = new StoryFixture("bad-images", "game:\n  id: bad\n  title: Bad\n  start: missing\n", "=== start\n-> END");
        await UploadBytes(invalid.Id + ".zip", invalid.Zip());
        await Expect(Page.Locator(".tw-library .tw-diagnostics")).ToBeVisibleAsync();
        await Page.Locator(".tw-resume").ClickAsync();
        await Expect(image).ToHaveAttributeAsync("src", restoredUrl!);
        await Library();
        var text = new StoryFixture("replacement",
            "game:\n  id: replacement\n  title: Replacement\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            "=== start\nReplacement text.\n-> END");
        await Upload(text);
        await Expect(Latest).ToContainTextAsync("Replacement text.");
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.imageRevocations.filter(value => value === url).length", restoredUrl));
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
        await Choose("Haggle for supplies");
        const string alt = "A low fire lights an expedition awning and a brass tripwire bell at the foot of a jungle stair.";
        var images = Page.GetByAltText(alt, new() { Exact = true });
        await Choose("Sign the expedition register");
        await Choose("Set out for the temple");
        await Choose("Tie off the rope and cross carefully");
        await Choose("Make camp before the climb");
        await images.First.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await images.First.ElementHandleAsync());
        await Choose("Eat a ration");
        await images.First.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await images.First.ElementHandleAsync());
        var url = await images.First.GetAttributeAsync("src");
        await Choose("Share supper by the fire");
        await Expect(images).ToHaveCountAsync(1);
        await images.Last.ScrollIntoViewIfNeededAsync();
        await Expect(images.Last).ToHaveAttributeAsync("src", url!);
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.createdImages.filter(value => value === url).length", url));
        await Driver.ClickAsync("Back");
        await Expect(images).ToHaveCountAsync(1);
        Assert.False(await Page.EvaluateAsync<bool>("url => window.revokedImages.includes(url)", url));
        await Driver.ClickAsync("Forward");
        await Expect(images).ToHaveCountAsync(1);
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
        await Expect(Page.Locator(".tw-media-code")).ToHaveCountAsync(0);
        Assert.Equal(2, (await Choices.First.InnerTextAsync()).Count(character => character == '`'));
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
        Assert.False(await Page.EvaluateAsync<bool>("url => window.archiveRevoked.includes(url)", url));
        var next = Page.GetByAltText("Next camp", new() { Exact = true });
        await next.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await next.ElementHandleAsync());
        var nextUrl = await next.GetAttributeAsync("src");
        Assert.Equal(url, nextUrl);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Reveal image: A camp", Exact = true }).ClickAsync();
        await image.ScrollIntoViewIfNeededAsync();
        await Expect(image).ToHaveAttributeAsync("src", nextUrl!);
        await Driver.ClickAsync("Back");
        await image.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await image.ElementHandleAsync());
        var restoredUrl = await image.GetAttributeAsync("src");
        Assert.StartsWith("blob:", restoredUrl);
        await Library();
        await UploadBytes("bad.zip", [1, 2, 3]);
        await Expect(Page.Locator(".tw-library")).ToContainTextAsync("Couldn't read");
        await Page.Locator(".tw-resume").ClickAsync();
        await Expect(image).ToHaveAttributeAsync("src", restoredUrl!);
        Assert.False(await Page.EvaluateAsync<bool>("url => window.archiveRevoked.includes(url)", restoredUrl));
        await Library();
        await Upload(new StoryFixture("text-replacement",
            "game:\n  id: replacement\n  title: Replacement\n  start: start\ncharacters:\n  player: {first-name: Tester}\n",
            "=== start\nReplacement text.\n-> END"));
        await Expect(Latest).ToContainTextAsync("Replacement text.");
        Assert.Equal(1, await Page.EvaluateAsync<int>("url => window.archiveRevoked.filter(value => value === url).length", restoredUrl));
    }

    [Fact]
    public async Task Illustrated_zip_above_former_expanded_limit_decodes_and_remains_playable()
    {
        using var files = new ImageStoryFiles();
        files.EnlargeIllustrationAboveFormerArchiveLimit();
        Assert.True(new FileInfo(files.ImagePath).Length > 50L * 1024 * 1024);
        var archive = files.Zip("wrapped/");
        Assert.True(archive.Length < 20 * 1024 * 1024, "Compressed upload and expanded image size are separate.");
        await UploadBytes("above-former-limit.zip", archive);
        await Expect(Latest).ToContainTextAsync("Hello from supporting configuration.");
        var image = Page.GetByAltText("A camp", new() { Exact = true });
        await image.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth === 1376 && image.naturalHeight === 768",
            await image.ElementHandleAsync());
        Assert.StartsWith("blob:", await image.GetAttributeAsync("src"));
        await Choose((await Driver.ObserveAsync()).Choices[0]);
        await Expect(Latest).ToContainTextAsync("Next passage.");
        var next = Page.GetByAltText("Next camp", new() { Exact = true });
        await next.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth === 1376 && image.naturalHeight === 768",
            await next.ElementHandleAsync());
        var nextUrl = await next.GetAttributeAsync("src");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Reveal image: A camp", Exact = true }).ClickAsync();
        await image.ScrollIntoViewIfNeededAsync();
        await Expect(image).ToHaveAttributeAsync("src", nextUrl!);
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

    [Fact]
    public async Task Small_images_stay_inline_and_narrative_illustrations_open_a_full_resolution_lightbox()
    {
        using var files = new ImageStoryFiles();
        await File.WriteAllTextAsync(files.TalePath, """
            === start
            Before {image("pixel.png", "Tiny credit")} after {image("photo.jpeg", "A camp")}.
            * [Continue {image("pixel.png", "Choice token")}] -> next
            === next
            Finished.
            * [Finish] -> END
            """);
        await UploadBytes("image-presentation.zip", files.Zip());

        var tiny = Page.GetByAltText("Tiny credit", new() { Exact = true });
        var illustration = Page.Locator(".tw-frame .tw-media > img[alt='A camp']");
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await tiny.ElementHandleAsync());
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
            await illustration.ElementHandleAsync());
        Assert.Null(await tiny.GetAttributeAsync("role"));
        Assert.Null(await tiny.GetAttributeAsync("tabindex"));
        Assert.Empty(await Page.GetByRole(AriaRole.Button, new() { Name = "Expand image: Tiny credit", Exact = true }).AllAsync());
        Assert.Equal("inline", await tiny.EvaluateAsync<string>("image => getComputedStyle(image).display"));
        var choiceImage = Page.GetByAltText("Choice token", new() { Exact = true });
        await Expect(choiceImage).ToBeVisibleAsync();
        Assert.Empty(await Page.GetByRole(AriaRole.Button, new() { Name = "Expand image: Choice token", Exact = true }).AllAsync());
        Assert.Equal("inline", await choiceImage.EvaluateAsync<string>("image => getComputedStyle(image).display"));

        var trigger = Page.GetByRole(AriaRole.Button, new() { Name = "Expand image: A camp", Exact = true });
        await Expect(trigger).ToBeVisibleAsync();
        await Expect(trigger).ToHaveCSSAsync("cursor", "zoom-in");
        var dialog = Page.Locator("dialog.tw-image-lightbox");
        string? lightBackground = null;
        foreach (var theme in new[] { "light", "dark" })
        {
            await Page.EvaluateAsync("theme => document.documentElement.dataset.twTheme = theme", theme);
            if (theme == "light")
            {
                await trigger.ClickAsync();
            }
            else
            {
                await trigger.FocusAsync();
                await Page.Keyboard.PressAsync("Enter");
            }
            await Expect(trigger).ToHaveAttributeAsync("aria-expanded", "true");
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Dialog, new() { Name = "A camp", Exact = true }))
                .ToBeVisibleAsync();
            var expanded = dialog.GetByAltText("A camp", new() { Exact = true });
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                await expanded.ElementHandleAsync());
            Assert.Equal(await illustration.EvaluateAsync<int>("image => image.naturalWidth"),
                await expanded.EvaluateAsync<int>("image => image.naturalWidth"));
            var background = await dialog.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
            Assert.NotEqual("rgba(0, 0, 0, 0)", background);
            if (theme == "light") lightBackground = background;
            else Assert.NotEqual(lightBackground, background);

            if (theme == "light")
            {
                await Page.Keyboard.PressAsync("Escape");
                await Expect(dialog).ToBeHiddenAsync();
                await Expect(trigger).ToBeFocusedAsync();
            }
            else
            {
                await dialog.EvaluateAsync("element => element.click()");
                await Expect(dialog).ToBeHiddenAsync();
                await Expect(trigger).ToBeFocusedAsync();
            }
        }

        await trigger.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Close image", Exact = true })).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close image", Exact = true }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();
    }
}
