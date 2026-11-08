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
        await Expect(Page.GetByLabel("Hide mechanical notes", new() { Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByLabel("Reading width", new() { Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByLabel("Line spacing", new() { Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.GetByLabel("Reduced motion", new() { Exact = true })).ToBeFocusedAsync();
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
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reading-width", "normal");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-line-spacing", "normal");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reduced-motion", "system");
        Assert.Equal("18px", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
        Assert.Equal("rgb(28, 26, 23)", await Page.Locator("body").EvaluateAsync<string>(
            "element => getComputedStyle(element).backgroundColor"));

        await Upload(TestStories.Progression);
        await Choose("Collect relic");
        await Expect(Page.Locator(".tw-note").First).ToBeVisibleAsync();
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        await Expect(Page.GetByLabel("Hide mechanical notes", new() { Exact = true })).Not.ToBeCheckedAsync();
        await Page.Keyboard.PressAsync("Escape");
    }

    [Theory]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"readingWidth\":\"invalid\",\"lineSpacing\":\"relaxed\"}", "normal", "relaxed")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"readingWidth\":\"wide\",\"lineSpacing\":\"invalid\"}", "wide", "normal")]
    [Trait("Host", "Browser")]
    public async Task Unsupported_reading_preferences_fall_back_independently(string stored, string width, string spacing)
    {
        await Page.EvaluateAsync(
            "stored => localStorage.setItem('talewright.player.preferences', stored)",
            stored);
        await ReloadAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "dark");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reading-width", width);
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-line-spacing", spacing);
    }

    [Theory]
    [InlineData("{\"version\":1,\"theme\":\"dark\"}")]
    [InlineData("{\"version\":1,\"theme\":\"dark\",\"reducedMotion\":\"invalid\"}")]
    [Trait("Host", "Browser")]
    public async Task Missing_or_invalid_reduced_motion_uses_the_system_preference(string stored)
    {
        await Page.EvaluateAsync(
            "stored => localStorage.setItem('talewright.player.preferences', stored)",
            stored);
        await ReloadAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-theme", "dark");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reduced-motion", "system");
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Reduced_motion_preference_disables_motion_and_reveals_passages_instantly()
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var reducedMotion = Page.GetByLabel("Reduced motion", new() { Exact = true });
        await Expect(reducedMotion.Locator("option")).ToHaveTextAsync(["System", "Reduce"]);
        await reducedMotion.SelectOptionAsync("reduce");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reduced-motion", "reduce");

        await Page.AddStyleTagAsync(new()
        {
            Content = ".motion-probe { animation: motion-probe 10s infinite; transition: opacity 10s; scroll-behavior: smooth; }",
        });
        var styles = await Page.EvaluateAsync<string[]>("""
            () => {
                const probe = document.createElement('div');
                probe.className = 'motion-probe';
                document.body.append(probe);
                const style = getComputedStyle(probe);
                const result = [style.animationName, style.transitionDuration, style.scrollBehavior];
                probe.remove();
                return result;
            }
            """);
        Assert.Equal(["none", "0s", "auto"], styles);

        await Page.Keyboard.PressAsync("Escape");
        await Page.EvaluateAsync("""
            () => {
                window.__twRevealBehavior = null;
                const scrollIntoView = Element.prototype.scrollIntoView;
                Element.prototype.scrollIntoView = function (options) {
                    if (this.classList.contains('tw-latest')) {
                        window.__twRevealBehavior = options?.behavior;
                    }
                    return scrollIntoView.call(this, options);
                };
            }
            """);
        await Choose("Walk onward");
        await Expect(Latest).ToContainTextAsync("Second passage text.");
        Assert.Equal("auto", await Page.EvaluateAsync<string>("() => window.__twRevealBehavior"));

        var stored = await Page.EvaluateAsync<string>(
            "() => localStorage.getItem('talewright.player.preferences')");
        using (var preferences = System.Text.Json.JsonDocument.Parse(stored))
        {
            Assert.Equal("reduce", preferences.RootElement.GetProperty("reducedMotion").GetString());
        }

        await ReloadAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reduced-motion", "reduce");
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Hiding_mechanical_notes_is_persistent_and_only_changes_presentation()
    {
        await Upload(TestStories.Progression);
        await Choose("Collect relic");
        await Expect(Latest.GetByRole(AriaRole.Status).Filter(new() { HasTextString = "Level 2!" }))
            .ToBeVisibleAsync();

        var notes = Page.Locator(".tw-note");
        var initialNoteCount = await notes.CountAsync();
        Assert.True(initialNoteCount > 0);
        var choicesBefore = (await Driver.ObserveAsync()).Choices;

        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        var hide = Page.GetByLabel("Hide mechanical notes", new() { Exact = true });
        await Expect(hide).Not.ToBeCheckedAsync();
        await hide.CheckAsync();
        await AssertAllHiddenAsync(notes);
        Assert.Equal(initialNoteCount, await notes.CountAsync());
        Assert.Equal(choicesBefore, (await Driver.ObserveAsync()).Choices);
        Assert.True(await Page.EvaluateAsync<bool>(
            "() => JSON.parse(localStorage.getItem('talewright.player.preferences')).hideMechanicalNotes"));
        await Page.Keyboard.PressAsync("Escape");

        await Choose("Donate relic");
        await Expect(Latest).ToContainTextAsync("The donation is complete.");
        var futureNotes = Page.Locator(".tw-latest .tw-note");
        Assert.True(await futureNotes.CountAsync() > 0, "The next passage should contain a new mechanical note.");
        await AssertAllHiddenAsync(futureNotes);
        var pastNotes = Page.Locator(".tw-past .tw-note");
        Assert.True(await pastNotes.CountAsync() > 0);
        await AssertAllHiddenAsync(pastNotes);

        var frameCount = await Page.Locator(".tw-frame").CountAsync();
        await Driver.ClickAsync("History");
        await AssertAllHiddenAsync(Page.Locator(".tw-history .tw-note"));
        Assert.Equal(frameCount, await Page.Locator(".tw-history-frame").CountAsync());
        await Driver.ClickAsync("Return to play");
        await Driver.ClickAsync("Back");
        await Expect(Latest).ToContainTextAsync("Level 2.");
        await Driver.ClickAsync("Forward");
        await Expect(Latest).ToContainTextAsync("The donation is complete.");

        await ReloadAsync();
        await Upload(TestStories.Progression);
        await Choose("Collect relic");
        await AssertAllHiddenAsync(Page.Locator(".tw-note"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        await Expect(hide).ToBeCheckedAsync();
        await Page.Keyboard.PressAsync("Escape");
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
    public async Task Mechanical_notes_checkbox_scales_and_remains_usable_in_a_narrow_window()
    {
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Page.SetViewportSizeAsync(320, 720);
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var checkbox = Page.GetByLabel("Hide mechanical notes", new() { Exact = true });
        var label = Page.Locator(".tw-settings-toggle");
        var textSize = Page.GetByLabel("Text size", new() { Exact = true });
        var initial = await checkbox.BoundingBoxAsync();
        Assert.NotNull(initial);
        Assert.InRange(initial.Width, 26.9, 27.1);
        Assert.InRange(initial.Height, 26.9, 27.1);
        Assert.True(await label.EvaluateAsync<bool>("element => element.getBoundingClientRect().height >= 44"));

        await textSize.SelectOptionAsync("200");
        var enlarged = await checkbox.BoundingBoxAsync();
        Assert.NotNull(enlarged);
        Assert.InRange(enlarged.Width, initial.Width * 2 - 0.1, initial.Width * 2 + 0.1);
        Assert.InRange(enlarged.Height, initial.Height * 2 - 0.1, initial.Height * 2 + 0.1);
        Assert.True(await label.EvaluateAsync<bool>("""
            element => {
                const dialog = element.closest('dialog');
                const label = element.getBoundingClientRect();
                const checkbox = element.querySelector('input').getBoundingClientRect();
                const bounds = dialog.getBoundingClientRect();
                return element.scrollWidth <= element.clientWidth + 1
                    && label.left >= bounds.left && label.right <= bounds.right
                    && checkbox.left >= label.left && checkbox.right <= label.right;
            }
            """), "The checkbox and label should fit in the settings dialog at 200%.");

        var labelBounds = await label.BoundingBoxAsync();
        Assert.NotNull(labelBounds);
        await label.ClickAsync(new() { Position = new() { X = labelBounds.Width - 2, Y = labelBounds.Height / 2 } });
        await Expect(checkbox).ToBeCheckedAsync();
        await checkbox.FocusAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(checkbox).Not.ToBeCheckedAsync();
        await Page.Keyboard.PressAsync("Space");
        await Expect(checkbox).ToBeCheckedAsync();
        await Expect(checkbox).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Escape");
        await ReloadAsync();
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");
        await Expect(checkbox).ToBeCheckedAsync();
        Assert.Equal("36px", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).fontSize"));
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

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Reading_width_and_line_spacing_persist_and_keep_choices_usable_at_every_size()
    {
        await Upload(TestStories.Linear);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Settings");

        var readingWidth = Page.GetByLabel("Reading width", new() { Exact = true });
        var lineSpacing = Page.GetByLabel("Line spacing", new() { Exact = true });
        await Expect(readingWidth.Locator("option")).ToHaveTextAsync(["Narrow", "Normal", "Wide"]);
        await Expect(lineSpacing.Locator("option")).ToHaveTextAsync(["Compact", "Normal", "Relaxed"]);

        var widthTokens = new Dictionary<string, string>
        {
            ["narrow"] = "38rem",
            ["normal"] = "52rem",
            ["wide"] = "68rem",
        };
        var spacingTokens = new Dictionary<string, string>
        {
            ["compact"] = "1.35",
            ["normal"] = "1.6",
            ["relaxed"] = "1.9",
        };

        await Page.SetViewportSizeAsync(1600, 900);
        var renderedWidths = new Dictionary<string, double>();
        foreach (var (width, widthToken) in widthTokens)
        foreach (var (spacing, spacingToken) in spacingTokens)
        {
            await readingWidth.SelectOptionAsync(width);
            await lineSpacing.SelectOptionAsync(spacing);
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reading-width", width);
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-line-spacing", spacing);
            Assert.Equal(widthToken, await Page.Locator("html").EvaluateAsync<string>(
                "element => getComputedStyle(element).getPropertyValue('--tw-transcript-max-width').trim()"));
            Assert.Equal(spacingToken, await Page.Locator("html").EvaluateAsync<string>(
                "element => getComputedStyle(element).getPropertyValue('--tw-transcript-line-height').trim()"));
            Assert.True(await Page.Locator(".tw-transcript").EvaluateAsync<bool>(
                "element => parseFloat(getComputedStyle(element).lineHeight) > 0"));
            Assert.True(await Page.Locator(".tw-transcript").EvaluateAsync<bool>(
                "element => element.scrollWidth <= element.clientWidth + 1"),
                $"{width} reading width and {spacing} spacing should fit the transcript.");
            if (spacing == "normal")
            {
                renderedWidths[width] = await Page.Locator(".tw-transcript").EvaluateAsync<double>(
                    "element => element.getBoundingClientRect().width");
            }

            await Page.SetViewportSizeAsync(320, 720);
            Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"),
                $"{width} reading width and {spacing} spacing should not overflow a narrow window.");
            foreach (var choice in await Page.Locator(".tw-choices button").AllAsync())
            {
                Assert.True(await choice.EvaluateAsync<bool>(
                    "element => element.getBoundingClientRect().right <= innerWidth + 1"),
                    $"{width} reading width and {spacing} spacing should keep choices in the viewport.");
            }
            Assert.True((await Driver.ObserveAsync()).SheetStacked,
                $"{width} reading width and {spacing} spacing should retain the stacked sheet layout.");
            await Page.SetViewportSizeAsync(1600, 900);
        }
        Assert.True(renderedWidths["narrow"] < renderedWidths["normal"]
            && renderedWidths["normal"] < renderedWidths["wide"],
            $"Each reading width should change the desktop transcript width: {string.Join(", ", renderedWidths.Select(pair => $"{pair.Key}={pair.Value}px"))}.");

        await Page.GetByLabel("Text size", new() { Exact = true }).SelectOptionAsync("200");
        await Page.SetViewportSizeAsync(320, 720);
        foreach (var (width, widthToken) in widthTokens)
        foreach (var (spacing, spacingToken) in spacingTokens)
        {
            await readingWidth.SelectOptionAsync(width);
            await lineSpacing.SelectOptionAsync(spacing);
            Assert.Equal(widthToken, await Page.Locator("html").EvaluateAsync<string>(
                "element => getComputedStyle(element).getPropertyValue('--tw-transcript-max-width').trim()"));
            Assert.Equal(spacingToken, await Page.Locator("html").EvaluateAsync<string>(
                "element => getComputedStyle(element).getPropertyValue('--tw-transcript-line-height').trim()"));
            Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"),
                $"{width} reading width and {spacing} spacing at 200% text should not overflow a narrow window.");
            foreach (var choice in await Page.Locator(".tw-choices button").AllAsync())
            {
                Assert.True(await choice.EvaluateAsync<bool>(
                    "element => element.getBoundingClientRect().right <= innerWidth + 1"),
                    $"{width} reading width and {spacing} spacing at 200% text should keep choices in the viewport.");
            }
        }

        await Page.Keyboard.PressAsync("Escape");
        var stored = await Page.EvaluateAsync<string>(
            "() => localStorage.getItem('talewright.player.preferences')");
        using (var preferences = System.Text.Json.JsonDocument.Parse(stored))
        {
            Assert.Equal("wide", preferences.RootElement.GetProperty("readingWidth").GetString());
            Assert.Equal("relaxed", preferences.RootElement.GetProperty("lineSpacing").GetString());
        }

        await ReloadAsync();
        await Upload(TestStories.Linear);
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-reading-width", "wide");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-tw-line-spacing", "relaxed");
        Assert.Equal("68rem", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).getPropertyValue('--tw-transcript-max-width').trim()"));
        Assert.Equal("1.9", await Page.Locator("html").EvaluateAsync<string>(
            "element => getComputedStyle(element).getPropertyValue('--tw-transcript-line-height').trim()"));

        await Driver.ClickAsync("History");
        Assert.Equal("68rem", await Page.Locator(".tw-history").EvaluateAsync<string>(
            "element => getComputedStyle(element).getPropertyValue('--tw-transcript-max-width').trim()"));
        Assert.Equal("1.9", await Page.Locator(".tw-history").EvaluateAsync<string>(
            "element => getComputedStyle(element).getPropertyValue('--tw-transcript-line-height').trim()"));
        await Driver.ClickAsync("Return to play");
    }

    private static async Task AssertAllHiddenAsync(ILocator elements)
    {
        var matches = await elements.AllAsync();
        Assert.NotEmpty(matches);
        foreach (var element in matches)
        {
            await Expect(element).ToBeHiddenAsync();
        }
    }
}
