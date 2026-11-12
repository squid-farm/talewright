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
        await Restart();
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

    [Fact]
    public async Task Choice_label_is_separate_from_scene_heading()
    {
        var id = $"choice-label-{Guid.NewGuid():N}";
        await Upload(new StoryFixture(id, TestStories.DefaultManifest(id).Build(), """
            === start
            * -> conversation
            * -> title-only
            * -> END

            === conversation
            @title The conversation
            @choice-label Speak to {player:first-name}
            The guide looks up.
            * [Back] -> start

            === title-only
            @title The old choice text
            * [Back] -> start
            """));

        await Expect(Latest.Locator(".tw-scene-title")).ToHaveCountAsync(0);
        await Expect(Choices).ToHaveCountAsync(3);
        await Expect(Choices.Nth(0)).ToContainTextAsync("Speak to Mara");
        await Expect(Choices.Nth(1)).ToContainTextAsync("Continue");
        await Expect(Choices.Nth(2)).ToContainTextAsync("Continue");

        await Choices.Nth(0).ClickAsync();
        await Expect(Latest.Locator(".tw-scene-title")).ToHaveTextAsync("The conversation");
    }

    [Fact]
    public async Task Shown_unavailable_choices_are_marked_and_available_choices_still_work()
    {
        await Upload(new StoryFixture("shown-choices", TestStories.DefaultYaml, """
            === start
            The first passage remains here.
            * [Restricted] (requires false, shown "Need a key") -> END
            * [Proceed] -> locked

            === locked
            No available actions.
            * [Restricted] (requires false, shown "Still locked") -> END
            """));

        await Expect(Choices).ToHaveCountAsync(2);
        await Expect(Choices.Nth(0)).ToBeDisabledAsync();
        await Expect(Choices.Nth(0)).ToContainTextAsync("1.");
        await Expect(Choices.Nth(0)).ToContainTextAsync("Need a key");
        await Expect(Choices.Nth(0)).ToContainTextAsync("(Unavailable)");
        await Expect(Choices.Nth(0).Locator(".tw-choice-status")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "1. Need a key (Unavailable)", Exact = true })).ToBeDisabledAsync();
        await Expect(Choices.Nth(1)).ToBeEnabledAsync();
        await Expect(Choices.Nth(1)).ToContainTextAsync("2.");

        await Expect(Latest).ToContainTextAsync("The first passage remains here.");
        await Expect(Latest).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Choices.Nth(1)).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("1");
        await Page.WaitForTimeoutAsync(1000);
        await Expect(Latest).ToContainTextAsync("The first passage remains here.");
        await Expect(Choices).ToHaveCountAsync(2);
        await Expect(Choices.Nth(0)).ToContainTextAsync("(Unavailable)");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("No available actions.");
        await Expect(Choices).ToHaveCountAsync(1);
        await Expect(Choices.First).ToBeDisabledAsync();
        await Expect(Choices.First).ToContainTextAsync("Still locked");
        await Expect(Choices.First).ToContainTextAsync("(Unavailable)");
        await Expect(Choices.First.Locator(".tw-choice-status")).ToBeVisibleAsync();

        await Driver.ClickAsync("Back");
        await Expect(Choices.Nth(1)).ToBeEnabledAsync();
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
