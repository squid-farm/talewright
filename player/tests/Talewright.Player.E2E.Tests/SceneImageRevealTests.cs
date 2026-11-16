using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using Talewright.Talemark.Compilation;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class StandaloneTheoryAttribute : TheoryAttribute
{
    public StandaloneTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1")
            Skip = "Requires a published web host with external story files.";
    }
}

[Trait("Host", "Browser")]
[Trait("Artifact", "Standalone")]
public sealed class SceneImageRevealTests : PlayerTest
{
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public SceneImageRevealTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }

    protected override bool StartsInLibrary => false;

    [StandaloneTheory]
    [InlineData(390, false, "none")]
    [InlineData(1280, false, "none")]
    [InlineData(390, true, "keyboard")]
    [InlineData(1280, true, "keyboard")]
    [InlineData(390, true, "wheel")]
    [InlineData(1280, true, "wheel")]
    [InlineData(390, true, "external")]
    [InlineData(1280, true, "external")]
    [InlineData(390, true, "pointer")]
    [InlineData(1280, true, "pointer")]
    public async Task Image_responses_realign_the_scene_until_the_reader_takes_control(int width, bool longScene, string input)
    {
        using var files = new ImageStoryFiles();
        foreach (var name in new[] { "first.jpeg", "one.jpeg", "two.jpeg", "choice.jpeg" })
            File.Copy(files.ImagePath, Path.Combine(files.Root, "images", name));
        var filler = string.Join("\n\n", Enumerable.Repeat(
            "The path winds beside the river. The traveler studies the distant hills.", longScene ? 35 : 1));
        File.WriteAllText(files.TalePath, $$"""
            === start
            First scene before navigation.
            {image("first.jpeg", "First image")}
            * [Go to destination] -> destination
            === destination
            The destination begins here.
            {image("one.jpeg", "First destination image")}
            {image("two.jpeg", "Second destination image")}
            {{filler}}
            * [A destination choice {image("choice.jpeg", "Choice image")}] -> END
            """);
        var source = StorySource.FromDirectory(files.Root);
        var index = source.Files.Keys.Concat(source.ImagePaths)
            .ToDictionary(path => path, _ => new string('A', 64), StringComparer.Ordinal);
        var gates = new ConcurrentDictionary<string, TaskCompletionSource>();
        var requests = new ConcurrentDictionary<string, TaskCompletionSource>();
        await Page.SetViewportSizeAsync(width, 844);
        await Page.Locator(".tw-creation, .tw-player").First.WaitForAsync();
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.RouteAsync("**/story/**", async route =>
        {
            var path = Uri.UnescapeDataString(new Uri(route.Request.Url).AbsolutePath.Split("/story/")[1]);
            if (path == "index.json")
            {
                await route.FulfillAsync(new() { ContentType = "application/json", Body = JsonSerializer.Serialize(index) });
                return;
            }
            if (path.StartsWith("images/", StringComparison.Ordinal))
            {
                var name = Path.GetFileName(path);
                var gate = gates.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                requests.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                await gate.Task;
                await route.FulfillAsync(new() { ContentType = "image/jpeg", BodyBytes = File.ReadAllBytes(files.ImagePath) });
                return;
            }
            await route.FulfillAsync(new() { ContentType = "text/plain", Body = source.Files[path] });
        });

        async Task WaitForRequest(string name) =>
            await requests.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(30));

        async Task Release(string name, string alt)
        {
            await WaitForRequest(name);
            gates[name].TrySetResult();
            var image = Page.GetByAltText(alt, new() { Exact = true });
            await Page.WaitForFunctionAsync("""
                image => image.complete && image.naturalWidth > 0 && image.naturalHeight > 0
                """, await image.ElementHandleAsync(), new() { Timeout = 30_000 });
            await image.EvaluateAsync("image => image.decode()");
        }

        async Task AssertAligned()
        {
            _output.WriteLine(await Page.EvaluateAsync<string>("""
                () => JSON.stringify({
                    scroll: scrollY, height: document.documentElement.scrollHeight,
                    scene: document.querySelector(".tw-latest").getBoundingClientRect().top,
                    bar: document.querySelector(".tw-bar").getBoundingClientRect().bottom
                })
                """));
            await Page.WaitForFunctionAsync("""
                () => {
                    const scene = document.querySelector(".tw-latest");
                    const bar = document.querySelector(".tw-bar");
                    const target = Math.max(0, Math.min(
                        scrollY + scene.getBoundingClientRect().top - bar.getBoundingClientRect().bottom,
                        document.documentElement.scrollHeight - innerHeight));
                    return Math.abs(scrollY - target) <= 2;
                }
                """, null, new() { Timeout = 10_000 });
        }

        try
        {
            await ReloadAsync();
            await Expect(Latest).ToContainTextAsync("First scene before navigation.");
            await Expect(RevealedPassage).ToBeFocusedAsync();
            await WaitForRequest("first.jpeg");
            Assert.Equal(0, await Page.EvaluateAsync<double>("scrollY"));
            await Release("first.jpeg", "First image");
            Assert.Equal(0, await Page.EvaluateAsync<double>("scrollY"));
            await Expect(RevealedPassage).ToBeFocusedAsync();

            await WaitForRequest("one.jpeg");
            await Choose("Go to destination");
            await Expect(Latest).ToContainTextAsync("The destination begins here.");
            await Expect(RevealedPassage).ToBeFocusedAsync();
            await AssertAligned();
            if (!longScene)
            {
                Assert.True(await Page.EvaluateAsync<bool>("""
                    () => document.querySelector(".tw-latest").getBoundingClientRect().top
                        > document.querySelector(".tw-bar").getBoundingClientRect().bottom + 2
                        && Math.abs(scrollY - (document.documentElement.scrollHeight - innerHeight)) <= 2
                    """), "The initially short page cannot yet align the scene with the toolbar.");
            }
            await WaitForRequest("one.jpeg");
            await WaitForRequest("two.jpeg");
            if (!longScene)
            {
                await Expect(Choices.First).ToBeVisibleAsync();
                await WaitForRequest("choice.jpeg");
            }
            var beforeArrival = await Page.EvaluateAsync<double>("scrollY");
            await Release("one.jpeg", "First destination image");
            await AssertAligned();
            if (!longScene)
                Assert.True(await Page.EvaluateAsync<double>("scrollY") > beforeArrival,
                    "The first image made more of the scene's beginning reachable.");
            await Expect(RevealedPassage).ToBeFocusedAsync();
            await Release("two.jpeg", "Second destination image");
            await AssertAligned();
            await Expect(RevealedPassage).ToBeFocusedAsync();
            if (!longScene)
            {
                await Release("choice.jpeg", "Choice image");
                await AssertAligned();
                await Expect(RevealedPassage).ToBeFocusedAsync();
            }
            else
            {
                Assert.True(await Choices.First.EvaluateAsync<bool>("el => el.getBoundingClientRect().top > innerHeight"));
                var beforeScroll = await Page.EvaluateAsync<double>("scrollY");
                if (input == "keyboard") await Page.Keyboard.PressAsync("PageDown");
                else if (input == "wheel") await Page.Mouse.WheelAsync(0, 300);
                else if (input == "pointer")
                {
                    var position = await Page.EvaluateAsync<float[]>("""
                        () => {
                            const rect = document.querySelector(".tw-latest").getBoundingClientRect();
                            return [rect.left + 5, rect.top + 5];
                        }
                        """);
                    await Page.Mouse.MoveAsync(position[0], position[1]);
                    await Page.Mouse.DownAsync();
                    await Page.Mouse.UpAsync();
                    await Page.EvaluateAsync("() => window.scrollBy(0, 300)");
                }
                else await Page.EvaluateAsync("() => window.scrollBy(0, 300)");
                await Page.WaitForFunctionAsync("before => scrollY > before", beforeScroll);
                var image = Page.GetByAltText("Choice image", new() { Exact = true });
                await image.ScrollIntoViewIfNeededAsync();
                await WaitForRequest("choice.jpeg");
                await Page.EvaluateAsync("""
                    () => {
                        window.revealScrollCalls = 0;
                        window.revealFocus = document.activeElement;
                        for (const name of ["scrollTo", "scrollBy", "scroll"]) {
                            const original = window[name];
                            window[name] = (...args) => {
                                window.revealScrollCalls++;
                                return original.apply(window, args);
                            };
                        }
                        const scrollIntoView = Element.prototype.scrollIntoView;
                        Element.prototype.scrollIntoView = function (...args) {
                            window.revealScrollCalls++;
                            return scrollIntoView.apply(this, args);
                        };
                    }
                    """);
                await Release("choice.jpeg", "Choice image");
                await Page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
                Assert.Equal(0, await Page.EvaluateAsync<int>("window.revealScrollCalls"));
                Assert.True(await Page.EvaluateAsync<bool>("window.revealFocus === document.activeElement"),
                    "Image arrival did not change the reader's focus.");
            }
            _output.WriteLine($"Actual held HTTP image responses; browser={_host.BrowserName}; viewport={width}x844; long={longScene}; input={input}; reduced motion=reduce.");
        }
        finally
        {
            foreach (var gate in gates.Values) gate.TrySetResult();
        }
    }
}
