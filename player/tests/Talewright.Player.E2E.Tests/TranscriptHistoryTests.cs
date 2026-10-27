using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class TranscriptHistoryTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private ILocator History => Page.GetByRole(AriaRole.Main, new() { Name = "Recorded history" });

    [Fact]
    public async Task Paging_is_bounded_read_only_and_returns_to_play_with_keyboard_focus()
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        using var files = CreateStory();
        await UploadBytes("history.zip", files.Zip());
        for (var index = 0; index < 45; index++)
        {
            await Expect(Latest).ToContainTextAsync($"Recorded visit {index}.");
            await Choices.First.ClickAsync();
            await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(Math.Min(index + 2, 21));
        }
        await Expect(Latest).ToContainTextAsync("Recorded visit 45.");
        await Driver.ClickAsync("Quick Save");
        await Expect(SaveStatus).ToContainTextAsync("Quick");
        var before = await SavedProgress();
        var sheet = await Sheet.InnerTextAsync();
        var trigger = Page.GetByRole(AriaRole.Button, new() { Name = "History", Exact = true });
        await trigger.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(History.GetByRole(AriaRole.Heading, new() { Name = "History", Exact = true })).ToBeFocusedAsync();
        await Expect(Page.Locator(".tw-columns")).ToBeHiddenAsync();
        await Expect(History.Locator(".tw-history-frame")).ToHaveCountAsync(4);
        Assert.Equal(0, await History.Locator("[aria-live], [role=status]").CountAsync());
        Assert.False(await History.EvaluateAsync<bool>("element => !!element.closest('[aria-live]')"));
        var seen = new HashSet<int>();
        while (true)
        {
            foreach (var passage in await History.Locator(".tw-history-frame").AllInnerTextsAsync())
            {
                var match = System.Text.RegularExpressions.Regex.Match(passage, @"Recorded visit (\d+)\.");
                Assert.True(match.Success);
                seen.Add(int.Parse(match.Groups[1].Value));
            }
            Assert.InRange(await Page.Locator(".tw-frame, .tw-history-frame").CountAsync(), 1, 42);
            var earlier = History.GetByRole(AriaRole.Button, new() { Name = "Earlier passages" });
            if (!await earlier.IsEnabledAsync()) break;
            await earlier.ClickAsync();
            await Expect(History.Locator(".tw-history-heading")).ToBeFocusedAsync();
        }
        Assert.Equal(Enumerable.Range(0, 46), seen.Order());
        await Expect(History).ToContainTextAsync("Recorded visit 0.");
        await Expect(History.Locator(".tw-chosen")).ToHaveCountAsync(21);
        Assert.Empty(await History.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).AllAsync());
        await History.GetByRole(AriaRole.Button, new() { Name = "Later passages" }).ClickAsync();
        await Expect(History.Locator(".tw-history-frame")).ToHaveCountAsync(21);
        await History.GetByRole(AriaRole.Button, new() { Name = "Return to play" }).FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(trigger).ToBeFocusedAsync();
        await Expect(History).ToHaveCountAsync(0);
        await Expect(Latest).ToContainTextAsync("Recorded visit 45.");
        Assert.Equal(sheet, await Sheet.InnerTextAsync());
        await Driver.ClickAsync("Quick Save");
        Assert.Equal(before, await SavedProgress());
        await Driver.ClickAsync("Back");
        await Expect(Latest).ToContainTextAsync("Recorded visit 44.");
        await Driver.ClickAsync("Forward");
        await Expect(Latest).ToContainTextAsync("Recorded visit 45.");
        await Restart();
        await Expect(Latest).ToContainTextAsync("Recorded visit 0.");
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(1);
        await trigger.ClickAsync();
        await Expect(History.Locator(".tw-history-frame")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task Unrevealed_images_do_not_acquire_and_final_history_release_revokes_only_unshared_urls()
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await Page.EvaluateAsync("""
            () => {
                window.historyUrls = { created: [], revoked: [] };
                const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
                URL.createObjectURL = blob => { const url = create(blob); window.historyUrls.created.push(url); return url; };
                URL.revokeObjectURL = url => { window.historyUrls.revoked.push(url); revoke(url); };
            }
            """);
        using var files = CreateStory(images: true);
        await UploadBytes("history-images.zip", files.Zip());
        for (var index = 0; index < 45; index++)
        {
            await Expect(Latest).ToContainTextAsync($"Recorded visit {index}.");
            await Choices.First.ClickAsync();
        }
        var mainImage = Latest.GetByAltText("Camp 45", new() { Exact = true });
        await mainImage.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await mainImage.ElementHandleAsync());
        var shared = await mainImage.GetAttributeAsync("src");
        await Driver.ClickAsync("History");
        var created = await Page.EvaluateAsync<int>("() => window.historyUrls.created.length");
        await Expect(History.GetByRole(AriaRole.Button, new() { Name = "Reveal image: Camp 45", Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await History.Locator("img").CountAsync());
        await History.GetByRole(AriaRole.Button, new() { Name = "Reveal image: Camp 45", Exact = true }).ClickAsync();
        var revealed = History.GetByAltText("Camp 45", new() { Exact = true });
        await revealed.ScrollIntoViewIfNeededAsync();
        await Expect(revealed).ToHaveAttributeAsync("src", shared!);
        Assert.Equal(created, await Page.EvaluateAsync<int>("() => window.historyUrls.created.length"));
        await History.GetByRole(AriaRole.Button, new() { Name = "Earlier passages" }).ClickAsync();
        Assert.False(await IsRevoked(shared!));
        await History.GetByRole(AriaRole.Button, new() { Name = "Earlier passages" }).ClickAsync();
        var beforeReveal = await Page.EvaluateAsync<int>("() => window.historyUrls.created.length");
        Assert.Equal(0, await History.Locator("img").CountAsync());
        var reveal = History.GetByRole(AriaRole.Button, new() { Name = "Reveal image: Camp 0", Exact = true });
        await reveal.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(History.GetByRole(AriaRole.Button, new() { Name = "Hide image: Camp 0", Exact = true })).ToBeFocusedAsync();
        var old = History.GetByAltText("Camp 0", new() { Exact = true });
        await old.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0", await old.ElementHandleAsync());
        var oldUrl = await old.GetAttributeAsync("src");
        Assert.Equal(beforeReveal + 1, await Page.EvaluateAsync<int>("() => window.historyUrls.created.length"));
        await History.GetByRole(AriaRole.Button, new() { Name = "Later passages" }).ClickAsync();
        await Page.WaitForFunctionAsync("url => window.historyUrls.revoked.includes(url)", oldUrl);
        Assert.False(await IsRevoked(shared!));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await Driver.ClickAsync("Return to play");
            await Expect(History).ToHaveCountAsync(0);
            await Driver.ClickAsync("History");
            Assert.Equal(0, await History.Locator("img").CountAsync());
            Assert.InRange(await Page.Locator(".tw-frame, .tw-history-frame").CountAsync(), 1, 42);
        }
        await Driver.ClickAsync("Return to play");
        await Expect(mainImage).ToHaveAttributeAsync("src", shared!);
        await Restart();
        await Page.WaitForFunctionAsync("url => window.historyUrls.revoked.includes(url)", shared);
    }

    private async Task<bool> IsRevoked(string url) =>
        await Page.EvaluateAsync<bool>("url => window.historyUrls.revoked.includes(url)", url);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Departing_history_retires_late_transfer_or_decode_without_leaving_resources(bool decode, bool close)
    {
        await Page.RouteAsync("**/MediaView.razor.js", async route =>
        {
            var response = await route.FetchAsync();
            var script = await response.TextAsync();
            var point = decode ? "const success = await loaded;" : "const bytes = await stream.arrayBuffer();";
            Assert.Contains(point, script);
            script = script.Replace(point, point + """

                if (window.pauseHistoryMedia)
                    await new Promise(resolve => window.resumeHistoryMedia.push(resolve));
                """);
            await route.FulfillAsync(new() { Response = response, Body = script });
        });
        await Page.EvaluateAsync("""
            () => {
                window.pauseHistoryMedia = false;
                window.resumeHistoryMedia = [];
                window.raceUrls = { created: [], revoked: [] };
                const create = URL.createObjectURL.bind(URL), revoke = URL.revokeObjectURL.bind(URL);
                URL.createObjectURL = blob => { const url = create(blob); window.raceUrls.created.push(url); return url; };
                URL.revokeObjectURL = url => { window.raceUrls.revoked.push(url); revoke(url); };
            }
            """);
        using var files = CreateStory(images: true);
        await UploadBytes("late-history.zip", files.Zip());
        for (var index = 0; index < 45; index++)
        {
            await Expect(Latest).ToContainTextAsync($"Recorded visit {index}.");
            await Choices.First.ClickAsync();
        }
        await Driver.ClickAsync("History");
        await History.GetByRole(AriaRole.Button, new() { Name = "Earlier passages" }).ClickAsync();
        await History.GetByRole(AriaRole.Button, new() { Name = "Earlier passages" }).ClickAsync();
        var liveBefore = await Page.EvaluateAsync<int>("() => window.raceUrls.created.length - window.raceUrls.revoked.length");
        await Page.EvaluateAsync("() => { window.pauseHistoryMedia = true; }");
        try
        {
            await History.GetByRole(AriaRole.Button, new() { Name = "Reveal image: Camp 0", Exact = true }).ClickAsync();
            await History.GetByAltText("Camp 0", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
            await Page.WaitForFunctionAsync("() => window.resumeHistoryMedia.length === 1");
            await History.GetByRole(AriaRole.Button, new() { Name = close ? "Return to play" : "Later passages" }).ClickAsync();
            await Expect(Page.GetByAltText("Camp 0", new() { Exact = true })).ToHaveCountAsync(0);
        }
        finally
        {
            await Page.EvaluateAsync("""
                () => {
                    window.pauseHistoryMedia = false;
                    window.resumeHistoryMedia.splice(0).forEach(resume => resume());
                }
                """);
        }
        await Page.WaitForFunctionAsync("count => window.raceUrls.created.length - window.raceUrls.revoked.length === count", liveBefore);
        Assert.Equal(0, await Page.GetByAltText("Camp 0", new() { Exact = true }).CountAsync());
        Assert.InRange(await Page.Locator(".tw-frame, .tw-history-frame").CountAsync(), 1, 42);
        Assert.True(await Page.EvaluateAsync<bool>("() => document.activeElement?.isConnected"));
        if (!close) await Driver.ClickAsync("Return to play");
        await Choices.First.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true })).ToBeVisibleAsync();
    }

    private async Task<string> SavedProgress() => await Page.EvaluateAsync<string>("""
        async () => {
            const store = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
            const data = await store.read('image-test', '\0quick');
            const save = JSON.parse(new TextDecoder().decode(data));
            delete save.summary;
            return JSON.stringify(save);
        }
        """);

    private static ImageStoryFiles CreateStory(bool images = false)
    {
        var files = new ImageStoryFiles();
        var manifest = Path.Combine(files.Root, "story.talewright.yaml");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("start: start", "start: passage0"));
        var tale = new StringBuilder();
        for (var index = 0; index <= 45; index++)
        {
            tale.AppendLine($"=== passage{index}");
            tale.AppendLine($"Recorded visit {index}.");
            if (images)
            {
                File.Copy(Path.Combine(files.Root, "images", "pixel.png"), Path.Combine(files.Root, "images", $"camp-{index}.png"));
                tale.AppendLine($"{{image(\"camp-{index}.png\", \"Camp {index}\")}}");
            }
            tale.AppendLine(index == 45 ? "* [Finish] -> END" : $"* [Next] -> passage{index + 1}");
        }
        File.WriteAllText(files.TalePath, tale.ToString());
        return files;
    }
}
