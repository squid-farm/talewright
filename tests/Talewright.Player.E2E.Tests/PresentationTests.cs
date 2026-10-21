using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class PresentationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_passage_restart_and_resume_restore_focus_and_scroll(bool reduced)
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = reduced ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
        await Upload(TestStories.Long);
        await Expect(Latest).ToBeFocusedAsync();
        await Choose("Next passage");
        await Expect(Latest).ToContainTextAsync("The new passage is visible.");
        await Expect(Latest).ToBeFocusedAsync();
        await Expect(Latest).ToBeInViewportAsync();
        await Page.WaitForFunctionAsync("() => { const r = document.querySelector('.tw-latest').getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; }");
        await Library();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Back to Player Test Continue where you left off" }).ClickAsync();
        await Expect(Latest).ToBeFocusedAsync();
        await Expect(Latest).ToBeInViewportAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true }).ClickAsync();
        await Expect(Latest).ToContainTextAsync("Long beginning");
        await Expect(Latest).ToBeFocusedAsync();
        await Page.WaitForFunctionAsync("() => scrollY < 2");
    }

    [Fact]
    public async Task Keyboard_activates_choices_and_navigation_with_semantic_focus()
    {
        await Upload(TestStories.Linear);
        await Expect(Latest).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Choices.First).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("Second passage text.");
        await Expect(Latest).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Space");
        await Expect(Latest).ToContainTextAsync("Finished safely.");
        await Expect(Page.GetByRole(AriaRole.Main, new() { Name = "Story transcript" })).ToHaveAttributeAsync("aria-live", "polite");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Play again" })).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData(390, true)]
    [InlineData(831, true)]
    [InlineData(833, false)]
    [InlineData(1280, false)]
    public async Task Layout_reflows_without_horizontal_overflow(int width, bool stacked)
    {
        // Media-query rem units use the browser's initial 16px font, not the styled root font.
        await Page.SetViewportSizeAsync(width, 900);
        await Upload(TestStories.Linear);
        await Expect(Sheet).ToBeVisibleAsync();
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
        var transcript = await Page.GetByRole(AriaRole.Main).BoundingBoxAsync();
        var sheet = await Sheet.BoundingBoxAsync();
        Assert.NotNull(transcript);
        Assert.NotNull(sheet);
        Assert.Equal(stacked, sheet.Y >= transcript.Y + transcript.Height - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Color_scheme_uses_the_declared_readable_palette(bool dark)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(TestStories.Linear);
        var background = await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");
        var foreground = await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).color");
        Assert.Equal(dark ? "rgb(28, 26, 23)" : "rgb(250, 248, 243)", background);
        Assert.Equal(dark ? "rgb(232, 226, 214)" : "rgb(34, 34, 34)", foreground);
    }
}
