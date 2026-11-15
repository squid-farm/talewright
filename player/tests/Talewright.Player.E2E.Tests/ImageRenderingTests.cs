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

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task First_scene_images_do_not_move_any_choice_when_their_transfer_finishes(int viewportWidth)
    {
        using var files = new ImageStoryFiles();
        await HoldImageTransfersAsync();
        await Page.SetViewportSizeAsync(viewportWidth, 844);
        var id = $"layout-first-{Guid.NewGuid():N}";
        var passageAlt = "A delayed passage illustration";
        var choiceAlt = "A delayed choice illustration";
        var passageImage = ImageFaker.Call("photo.jpeg", passageAlt);
        var choiceImage = ImageFaker.Call("photo.jpeg", choiceAlt);
        var tale = string.Join('\n',
            "=== start",
            "The first scene has no preceding scene to warm its illustration.",
            passageImage,
            "The image is followed by several choices.",
            $"* [A long route label that wraps at a narrow viewport {choiceImage}] -> END",
            "* [Wait at the camp and watch the river.] -> END",
            "* [Ask the guide to inspect the opposite bank.] -> END");
        await UploadImageStoryAsync(files, id, tale);
        await Expect(Latest).ToContainTextAsync("no preceding scene");
        await Expect(Latest).ToBeFocusedAsync();
        await AssertHeldImagesKeepChoicesStableAsync("first scene", id, viewportWidth,
            new FileInfo(files.ImagePath).Length, passageAlt, choiceAlt);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public async Task Immediate_navigation_images_do_not_move_any_choice_when_their_transfer_finishes(int viewportWidth)
    {
        using var files = new ImageStoryFiles();
        await HoldImageTransfersAsync();
        await Page.SetViewportSizeAsync(viewportWidth, 844);
        var id = $"layout-navigation-{Guid.NewGuid():N}";
        var passageAlt = "A delayed destination illustration";
        var choiceAlt = "A delayed destination choice illustration";
        var passageImage = ImageFaker.Call("photo.jpeg", passageAlt);
        var choiceImage = ImageFaker.Call("photo.jpeg", choiceAlt);
        var tale = string.Join('\n',
            "=== start",
            "Choose before the destination image is ready.",
            "* [Go to the destination.] -> destination",
            "=== destination",
            "The destination appears while its image transfer is held.",
            passageImage,
            $"* [Follow the long destination route label {choiceImage}] -> END",
            "* [Stay beside the river and wait.] -> END",
            "* [Return to the first scene.] -> start");
        await UploadImageStoryAsync(files, id, tale);
        await Expect(Latest).ToContainTextAsync("Choose before the destination");
        await Choose("Go to the destination.");
        await Expect(Latest).ToContainTextAsync("destination appears");
        await Expect(Latest).ToBeFocusedAsync();
        await AssertHeldImagesKeepChoicesStableAsync("immediate navigation", id, viewportWidth,
            new FileInfo(files.ImagePath).Length, passageAlt, choiceAlt);
    }

    private async Task HoldImageTransfersAsync()
    {
        await Page.RouteAsync("**/MediaView.razor.js", async route =>
        {
            var response = await route.FetchAsync();
            var script = await response.TextAsync();
            const string point = "const bytes = await stream.arrayBuffer();";
            Assert.Contains(point, script);
            script = script.Replace(point, """
                const bytes = await stream.arrayBuffer();
                if (window.holdImageTransfers)
                    await new Promise(resolve => window.pendingImageTransfers.push(resolve));
                """, StringComparison.Ordinal);
            await route.FulfillAsync(new() { Response = response, Body = script });
        });
        await Page.EvaluateAsync("""
            () => {
                window.holdImageTransfers = true;
                window.pendingImageTransfers = [];
            }
            """);
    }

    private async Task UploadImageStoryAsync(ImageStoryFiles files, string id, string tale)
    {
        File.WriteAllText(files.TalePath, tale);
        var manifestPath = Path.Combine(files.Root, "story.talewright.yaml");
        File.WriteAllText(manifestPath,
            File.ReadAllText(manifestPath).Replace("id: image-test", $"id: {id}", StringComparison.Ordinal));
        await UploadBytes(id + ".zip", files.Zip());
    }

    private async Task AssertHeldImagesKeepChoicesStableAsync(string scenario, string storyId, int viewportWidth,
        long imageBytes, params string[] alts)
    {
        await Page.WaitForFunctionAsync("window.pendingImageTransfers.length > 0", null,
            new() { Timeout = 30_000 });
        var images = new List<ILocator>();
        foreach (var alt in alts)
        {
            var image = Page.GetByRole(AriaRole.Img, new() { Name = alt, Exact = true });
            await Expect(image).ToBeVisibleAsync();
            Assert.Equal("1376", await image.GetAttributeAsync("width"));
            Assert.Equal("768", await image.GetAttributeAsync("height"));
            Assert.True(await image.EvaluateAsync<bool>("""
                image => {
                    const bounds = image.getBoundingClientRect();
                    return bounds.width > 0 && bounds.height > 0
                        && Math.abs(bounds.width / bounds.height - 1376 / 768) < 0.01;
                }
                """), "The held image has a reserved box with its actual aspect ratio.");
            images.Add(image);
        }

        await Page.EvaluateAsync("""
            () => {
                const buttons = [...document.querySelectorAll(".tw-choices button.tw-choice")];
                if (buttons.length < 3) throw new Error("Expected multiple visible story choices.");
                const observation = {
                    baseline: buttons.map(button => button.getBoundingClientRect().top + scrollY),
                    choiceCount: buttons.length,
                    focus: document.activeElement,
                    maximumDisplacement: 0,
                    scrollCalls: 0,
                    focusChanged: false,
                    choiceCountChanged: false,
                    tracking: true,
                    scrollTo: window.scrollTo,
                    scrollBy: window.scrollBy,
                    scroll: window.scroll,
                    scrollIntoView: Element.prototype.scrollIntoView
                };
                window.imageLayoutObservation = observation;
                window.scrollTo = (...args) => {
                    if (observation.tracking) observation.scrollCalls++;
                    return observation.scrollTo.apply(window, args);
                };
                window.scrollBy = (...args) => {
                    if (observation.tracking) observation.scrollCalls++;
                    return observation.scrollBy.apply(window, args);
                };
                window.scroll = (...args) => {
                    if (observation.tracking) observation.scrollCalls++;
                    return observation.scroll.apply(window, args);
                };
                Element.prototype.scrollIntoView = function (...args) {
                    if (observation.tracking) observation.scrollCalls++;
                    return observation.scrollIntoView.apply(this, args);
                };
                const sample = () => {
                    if (!observation.tracking) return;
                    const current = [...document.querySelectorAll(".tw-choices button.tw-choice")];
                    if (current.length !== observation.choiceCount) observation.choiceCountChanged = true;
                    if (document.activeElement !== observation.focus) observation.focusChanged = true;
                    current.forEach((button, index) => {
                        if (index < observation.baseline.length) {
                            const top = button.getBoundingClientRect().top + scrollY;
                            observation.maximumDisplacement = Math.max(
                                observation.maximumDisplacement, Math.abs(top - observation.baseline[index]));
                        }
                    });
                    requestAnimationFrame(sample);
                };
                requestAnimationFrame(sample);
            }
            """);
        var imageTimer = Stopwatch.StartNew();
        await Page.EvaluateAsync("""
            () => {
                window.holdImageTransfers = false;
                for (const release of window.pendingImageTransfers.splice(0)) release();
            }
            """);

        foreach (var image in images)
        {
            var handle = await image.ElementHandleAsync();
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth === 1376 && image.naturalHeight === 768",
                handle, new() { Timeout = 30_000 });
            var keepsResponsiveAspectRatio = await image.EvaluateAsync<bool>("""
                image => {
                    const bounds = image.getBoundingClientRect();
                    return bounds.width <= innerWidth
                        && Math.abs(bounds.width / bounds.height - image.naturalWidth / image.naturalHeight) < 0.01;
                }
                """);
            Assert.True(keepsResponsiveAspectRatio, "The decoded image remains responsive without distortion.");
        }

        await Page.EvaluateAsync("""
            () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(() => {
                const observation = window.imageLayoutObservation;
                observation.tracking = false;
                window.scrollTo = observation.scrollTo;
                window.scrollBy = observation.scrollBy;
                window.scroll = observation.scroll;
                Element.prototype.scrollIntoView = observation.scrollIntoView;
                resolve();
            }))))
            """);
        imageTimer.Stop();
        Assert.False(await Page.EvaluateAsync<bool>("window.imageLayoutObservation.choiceCountChanged"),
            "The choice set remained unchanged during image arrival.");
        Assert.InRange(await Page.EvaluateAsync<double>("window.imageLayoutObservation.maximumDisplacement"), 0, 1);
        Assert.Equal(0, await Page.EvaluateAsync<int>("window.imageLayoutObservation.scrollCalls"));
        Assert.False(await Page.EvaluateAsync<bool>("window.imageLayoutObservation.focusChanged"),
            "Image arrival did not transiently change focus.");
        Assert.True(await Page.EvaluateAsync<bool>(
            "window.imageLayoutObservation.focus === document.activeElement"),
            "Image arrival did not change focus.");
        foreach (var image in images)
            await Expect(image).ToBeVisibleAsync();

        _output.WriteLine(
            $"Stable-image measurement: scenario={scenario}; story={storyId}; host=published WebAssembly/{_host.BrowserName}; " +
            $"viewport={viewportWidth}x844; image=photo.jpeg ({imageBytes} bytes, 1376x768); " +
            $"cache=fresh uploaded ZIP and Blob URL, no HTTP image cache; warming=enabled, transfer held before Blob URL creation; " +
            $"timing=gate release through successful visible decode and three animation frames; " +
            $"release-to-visible={imageTimer.ElapsedMilliseconds}ms; max choice displacement=" +
            $"{await Page.EvaluateAsync<double>("window.imageLayoutObservation.maximumDisplacement"):F2}px.");
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
        await Choose("Continue");
        await Choose("Haggle for supplies");
        const string alt = "A low fire lights an expedition awning and a brass tripwire bell at the foot of a jungle stair.";
        var images = Page.GetByAltText(alt, new() { Exact = true });
        await Choose("Sign the expedition register");
        await Choose("Continue");
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
        await Choose("Continue");
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
}
