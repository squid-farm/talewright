using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class PlayerPreferencesTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    [Trait("Host", "Browser")]
    public async Task Theme_setting_is_modal_persistent_and_overrides_the_system()
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Settings", Exact = true });
        var theme = Page.GetByLabel("Theme", new() { Exact = true });
        var close = Page.GetByRole(AriaRole.Button, new() { Name = "Close settings", Exact = true });
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "More actions", Exact = true });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(theme).ToBeFocusedAsync();
        await Expect(theme.Locator("option")).ToHaveTextAsync(["System", "Light", "Dark"]);
        Assert.True(await dialog.EvaluateAsync<bool>("element => element.matches(':modal')"));

        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByLabel("Text size", new() { Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(close).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(theme).ToBeFocusedAsync();

        await theme.SelectOptionAsync("Light");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "light");
        Assert.Equal("light", await Page.Locator("html").EvaluateAsync<string>("element => getComputedStyle(element).colorScheme"));
        Assert.Equal("rgb(250, 248, 243)", await Page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));

        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(toggle).ToBeFocusedAsync();
        await ReloadAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "light");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"version\":1,\"theme\":\"ultraviolet\"}")]
    [InlineData("{\"version\":99,\"theme\":\"dark\"}")]
    [Trait("Host", "Browser")]
    public async Task Corrupt_or_unknown_theme_falls_back_to_system_and_system_tracks_os_changes(string stored)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Page.EvaluateAsync(
            "stored => localStorage.setItem('talewright.player.preferences', stored)",
            stored);
        await ReloadAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "system");
        Assert.Equal("rgb(28, 26, 23)", await Page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));

        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        Assert.Equal("rgb(250, 248, 243)", await Page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
    }

    [Theory]
    [InlineData("{\"version\":1,\"theme\":\"dark\"}")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"textSizePercent\":250}")]
    [Trait("Host", "Browser")]
    public async Task Missing_or_invalid_text_size_uses_the_default_without_losing_theme(string stored)
    {
        await Page.EvaluateAsync(
            "stored => localStorage.setItem('talewright.player.preferences', stored)",
            stored);
        await ReloadAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "dark");
        Assert.Equal("18px", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
        Assert.Equal("rgb(28, 26, 23)", await Page.Locator("body").EvaluateAsync<string>(
            "element => getComputedStyle(element).backgroundColor"));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Forced_dark_theme_covers_save_dialog_and_character_creation()
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        await Page.GetByLabel("Theme", new() { Exact = true }).SelectOptionAsync("Dark");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Close settings", Exact = true }).ClickAsync();

        Assert.Equal("rgb(28, 26, 23)", await Page.Locator("body").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        await Driver.ClickAsync("Saves");
        var saves = Page.Locator(".tw-saves");
        await Expect(saves).ToBeVisibleAsync();
        Assert.Equal("rgb(38, 35, 31)", await saves.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        Assert.Equal("rgb(232, 226, 214)", await saves.EvaluateAsync<string>("element => getComputedStyle(element).color"));
        await Page.Keyboard.PressAsync("Escape");

        await Library();
        var fixture = TestStories.Linear with
        {
            Yaml = TestStories.DefaultManifest("theme-creation", creationName: true).Build(),
        };
        await Upload(fixture);
        var defaultName = Page.GetByRole(AriaRole.Button, new() { Name = "Use default name", Exact = true });
        var contrast = await defaultName.EvaluateAsync<double>("""
            element => {
                const channels = color => color.match(/\d+/g).map(Number).map(value => {
                    value /= 255;
                    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
                });
                const luminance = color => {
                    const [red, green, blue] = channels(color);
                    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                };
                const style = getComputedStyle(element);
                const text = luminance(style.color);
                const background = luminance(style.backgroundColor);
                return (Math.max(text, background) + 0.05) / (Math.min(text, background) + 0.05);
            }
            """);

        Assert.True(contrast >= 4.5, $"Default-name button text contrast was {contrast:F2}:1 in forced Dark mode.");
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Maximum_text_size_persists_and_reflows_player_content_in_a_narrow_window()
    {
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var textSize = Page.GetByLabel("Text size", new() { Exact = true });
        await Expect(textSize.Locator("option")).ToHaveTextAsync(["100%", "125%", "150%", "175%", "200%"]);
        await textSize.SelectOptionAsync("200");
        Assert.Equal("36px", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
        Assert.Equal("36px", await Page.Locator(".tw-choices button").First.EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
        Assert.InRange(await Sheet.EvaluateAsync<double>(
            "element => parseFloat(getComputedStyle(element).fontSize)"), 30.5, 30.7);
        Assert.Equal(200, await Page.EvaluateAsync<int>(
            "() => JSON.parse(localStorage.getItem('talewright.player.preferences')).textSizePercent"));

        await Page.Keyboard.PressAsync("Escape");
        await ReloadAsync();
        Assert.Equal("36px", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Page.SetViewportSizeAsync(320, 720);

        var layout = await Page.EvaluateAsync<string>("""
            () => {
                const bounds = element => {
                    const rect = element.getBoundingClientRect();
                    return {
                        name: element.className || element.tagName,
                        left: rect.left,
                        right: rect.right,
                        width: rect.width,
                        scrollWidth: element.scrollWidth,
                        clientWidth: element.clientWidth,
                        columns: getComputedStyle(element).gridTemplateColumns
                    };
                };
                const visibleToolbar = [...document.querySelectorAll(".tw-bar-actions button")]
                    .filter(element => element.checkVisibility());
                return JSON.stringify({
                    innerWidth,
                    rootWidth: document.documentElement.scrollWidth,
                    elements: [".tw-app", ".tw-player", ".tw-columns", ".tw-transcript", ".tw-sheet", ".tw-choices"]
                        .map(selector => bounds(document.querySelector(selector))),
                    toolbar: visibleToolbar.map(bounds),
                    choices: [...document.querySelectorAll(".tw-choices button")].map(bounds)
                });
            }
            """);
        using var layoutDetails = System.Text.Json.JsonDocument.Parse(layout);
        var rootWidth = layoutDetails.RootElement.GetProperty("rootWidth").GetDouble();
        var viewportWidth = layoutDetails.RootElement.GetProperty("innerWidth").GetDouble();
        Assert.True(rootWidth <= viewportWidth + 1,
            $"Text at 200% should reflow without horizontal clipping in a narrow window: {layout}");
        foreach (var group in new[] { "toolbar", "choices" })
        {
            foreach (var element in layoutDetails.RootElement.GetProperty(group).EnumerateArray())
            {
                Assert.True(element.GetProperty("left").GetDouble() >= -1
                    && element.GetProperty("right").GetDouble() <= viewportWidth + 1,
                    $"Text at 200% should keep {element.GetProperty("name").GetString()} in the viewport: {layout}");
            }
        }
        Assert.True((await Driver.ObserveAsync()).SheetStacked);

        Assert.True(await Page.Locator(".tw-sheet").EvaluateAsync<bool>(
            "element => element.scrollWidth <= element.clientWidth + 1"),
            "Character sheet should reflow at the largest text size.");
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        var settings = Page.GetByRole(AriaRole.Dialog, new() { Name = "Settings", Exact = true });
        await Expect(settings).ToBeVisibleAsync();
        Assert.True(await settings.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth + 1"),
            "Settings dialog should fit in a narrow window.");
        Assert.True(await textSize.EvaluateAsync<bool>("element => element.getBoundingClientRect().right <= innerWidth"));
        await Page.Keyboard.PressAsync("Escape");
    }
}
