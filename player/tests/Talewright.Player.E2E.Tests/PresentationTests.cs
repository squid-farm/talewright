using System.Globalization;
using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class PresentationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Extra_vertical_spacing_is_visible_but_absent_from_the_accessibility_tree()
    {
        await Upload(new StoryFixture("vertical-spacing-browser", TestStories.DefaultYaml,
            "=== start\nFirst.\n\nSecond.\n\nThird.\n\n\n\nFourth.\n" +
            "> guide: Hello.\n>\n>\n>\n> Goodbye.\n* [Finish] -> END"));
        var spacers = Latest.Locator("p.tw-blank-line");
        await Expect(spacers).ToHaveCountAsync(4);
        for (var index = 0; index < 4; index++)
        {
            await Expect(spacers.Nth(index)).ToHaveAttributeAsync("aria-hidden", "true");
            Assert.True((await spacers.Nth(index).BoundingBoxAsync())!.Height > 0);
        }
        var paragraphs = Latest.Locator(".tw-paragraph");
        var first = (await paragraphs.Nth(0).BoundingBoxAsync())!;
        var second = (await paragraphs.Nth(1).BoundingBoxAsync())!;
        var third = (await paragraphs.Nth(2).BoundingBoxAsync())!;
        var fourth = (await paragraphs.Nth(3).BoundingBoxAsync())!;
        Assert.True(fourth.Y - (third.Y + third.Height) > second.Y - (first.Y + first.Height));
        var speech = Latest.Locator(".tw-speech-body .tw-line");
        var hello = (await speech.Nth(0).BoundingBoxAsync())!;
        var goodbye = (await speech.Nth(1).BoundingBoxAsync())!;
        Assert.True(goodbye.Y - (hello.Y + hello.Height) >= hello.Height * 2);
        var accessible = await Latest.AriaSnapshotAsync();
        Assert.Contains("First.", accessible);
        Assert.Contains("Goodbye.", accessible);
        Assert.DoesNotContain('\u00a0', accessible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_passage_restart_and_resume_restore_focus_and_scroll(bool reduced)
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = reduced ? ReducedMotion.Reduce : ReducedMotion.NoPreference });
        await Upload(TestStories.Long);
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Choose("Next passage");
        await Expect(Latest).ToContainTextAsync("The new passage is visible.");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(Latest).ToBeInViewportAsync();
        await Page.WaitForFunctionAsync("() => { const r = document.querySelector('.tw-latest').getBoundingClientRect(); return r.top >= 0 && r.bottom <= innerHeight; }");
        await Library();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Back to Player Test Continue where you left off" }).ClickAsync();
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(Latest).ToBeInViewportAsync();
        await Restart();
        await Expect(Latest).ToContainTextAsync("Long beginning");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Page.WaitForFunctionAsync("() => scrollY < 2");
    }

    [Fact]
    public async Task Automatic_scene_diverts_reveal_the_first_new_passage_without_continue_annotations()
    {
        var id = $"automatic-diverts-{Guid.NewGuid():N}";
        var longPassage = string.Join('\n', Enumerable.Repeat(
            "The path winds through the forest, past trees older than the road.", 16));
        await Upload(new StoryFixture(id, TestStories.DefaultManifest(id).Build(), $"""
            === start
            A marker from the earlier passage.
            {longPassage}
            * [Follow the trail] -> first
            === first
            The first newly presented passage.
            {longPassage}
            -> second
            === second
            The next passage follows automatically.
            {longPassage}
            -> final
            === final
            The final passage waits for the reader.
            * [Finish] -> END
            """));

        await Choose("Follow the trail");

        var firstNewPassage = Page.Locator(".tw-passage-anchor[data-tw-passage-index='1']");
        await Expect(firstNewPassage).ToHaveAttributeAsync("role", "group");
        await Expect(firstNewPassage).ToHaveAttributeAsync("aria-label", "New story passage");
        await Expect(firstNewPassage).ToContainTextAsync("The first newly presented passage.");
        await Expect(firstNewPassage).ToBeFocusedAsync();
        await Expect(Latest).ToContainTextAsync("The final passage waits for the reader.");
        await AssertState(state =>
        {
            Assert.DoesNotContain("A marker from the earlier passage.", state.NewPassages);
            Assert.Contains("The first newly presented passage.", state.NewPassages);
            Assert.Contains("The next passage follows automatically.", state.NewPassages);
            Assert.Contains("The final passage waits for the reader.", state.NewPassages);
        });
        await Expect(Page.Locator(".tw-chosen")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".tw-chosen").First).ToContainTextAsync("Follow the trail");
        await Page.WaitForFunctionAsync("""
            () => {
                const passage = document.querySelector('.tw-passage-anchor[data-tw-passage-index="1"]');
                const toolbar = document.querySelector('.tw-bar');
                return passage && toolbar
                    && Math.abs(passage.getBoundingClientRect().top - toolbar.getBoundingClientRect().bottom) <= 2;
            }
            """);

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Close saves");
        await Driver.ClickAsync("Back");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(RevealedPassage).ToContainTextAsync("The path winds through the forest");
        await Driver.ClickAsync("Forward");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(RevealedPassage).ToContainTextAsync("The first newly presented passage.");
        await Choose("Finish");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(RevealedPassage).ToContainTextAsync("The first newly presented passage.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Automatic_diverts_focus_the_first_passage_remaining_after_a_clear(bool onStart)
    {
        var id = $"automatic-clear-{Guid.NewGuid():N}";
        var startExit = onStart ? "-> first" : "* [Follow] -> first";
        await Upload(new StoryFixture(id, TestStories.DefaultManifest(id).Build(), $"""
            === start
            Before the journey.
            {startExit}
            === first
            Before the clear.
            -> cleared
            === cleared
            @clear
            The first visible passage.
            -> final
            === final
            The final passage.
            * [Finish] -> END
            """));

        if (!onStart) await Choose("Follow");

        await Expect(RevealedPassage).ToContainTextAsync("The first visible passage.");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Expect(Latest).ToContainTextAsync("The final passage.");
        await Expect(Page.GetByRole(AriaRole.Main, new() { Name = "Story transcript" }))
            .Not.ToContainTextAsync("Before the clear.");
    }

    [Fact]
    public async Task Runaway_automatic_diverts_show_an_error_without_a_continue_or_success()
    {
        var id = $"runaway-diverts-{Guid.NewGuid():N}";
        await Upload(new StoryFixture(id, TestStories.DefaultManifest(id).Build(), """
            === start
            -> start
            """));

        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("1,000 automatic scene transitions");
        await Expect(Choices).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Keyboard_activates_choices_and_navigation_with_semantic_focus()
    {
        await Upload(TestStories.Linear);
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Choices.First).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("Second passage text.");
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Space");
        await Expect(Latest).ToContainTextAsync("Finished safely.");
        await Expect(RevealedPassage).ToBeFocusedAsync();
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
        await Expect(RevealedPassage).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Choices.Nth(1)).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("1");
        await Page.WaitForTimeoutAsync(1000);
        await Expect(Latest).ToContainTextAsync("The first passage remains here.");
        await Expect(Choices).ToHaveCountAsync(2);
        await Expect(Choices.Nth(0)).ToContainTextAsync("(Unavailable)");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Latest).ToContainTextAsync("No available actions.");
        await Expect(RevealedPassage).ToBeFocusedAsync();
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

    [Fact]
    public async Task Authored_whitespace_survives_and_text_wraps_at_narrow_widths()
    {
        await Page.SetViewportSizeAsync(320, 900);
        await Upload(new StoryFixture("spacing-browser", TestStories.DefaultYaml,
            "=== start\n  Prose  \twith several words that need to wrap at this narrow viewport width.\n" +
            "> guide:  Speech  \twith several words that need to wrap at this narrow viewport width.\n-> END"));

        var prose = Latest.Locator(".tw-paragraph");
        var speech = Latest.Locator(".tw-speech-body .tw-line");
        Assert.Equal("  Prose  \twith several words that need to wrap at this narrow viewport width.",
            await prose.TextContentAsync());
        Assert.Equal(" Speech  \twith several words that need to wrap at this narrow viewport width.",
            await speech.TextContentAsync());
        foreach (var line in new[] { prose, speech })
        {
            await Expect(line).ToHaveCSSAsync("white-space", "pre-wrap");
            await Expect(line).ToHaveCSSAsync("tab-size", "4");
            Assert.True(await line.EvaluateAsync<bool>(
                "e => e.getBoundingClientRect().height > parseFloat(getComputedStyle(e).lineHeight) * 1.5"));
            Assert.True(await line.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"));
        }
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speech_turns_render_as_distinct_theme_aware_panels_at_narrow_width(bool dark)
    {
        await Page.SetViewportSizeAsync(320, 900);
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(new StoryFixture("speech-panels-browser", TestStories.DefaultYaml, """
            === start
            Narration before the speech.
            > guide: First  paragraph.
            >
            >
            >
            > Second paragraph.

            > player: A verylongunbrokenwordthatmustwrapwithoutcausinghorizontalscrolling followed by more dialogue.
            Narration after the speech.
            -> END
            """));

        var panels = Latest.Locator(".tw-speech");
        await Expect(panels).ToHaveCountAsync(2);
        await Expect(panels.Nth(0).Locator(".tw-speaker")).ToHaveTextAsync("Tomas");
        await Expect(panels.Nth(0).Locator(".tw-line")).ToHaveCountAsync(2);
        await Expect(panels.Nth(0).Locator(".tw-blank-line")).ToHaveCountAsync(2);
        Assert.Equal("First  paragraph.", await panels.Nth(0).Locator(".tw-line").Nth(0).TextContentAsync());
        await Expect(panels.Nth(1).Locator(".tw-speaker")).ToHaveTextAsync("Mara");
        await Expect(panels.Nth(1).Locator(".tw-line")).ToHaveCountAsync(1);
        await Expect(Latest.Locator(".tw-paragraph")).ToHaveCountAsync(2);
        Assert.Equal("Narration before the speech.",
            await Latest.Locator(".tw-paragraph").Nth(0).TextContentAsync());
        Assert.Equal("Narration after the speech.",
            await Latest.Locator(".tw-paragraph").Nth(1).TextContentAsync());

        var panelStyle = await panels.First.EvaluateAsync<string[]>("""
            e => {
                const style = getComputedStyle(e);
                return [
                    style.boxSizing,
                    style.padding,
                    style.marginTop,
                    style.marginBottom,
                    style.borderLeftWidth,
                    style.borderTopWidth,
                    style.borderRightWidth,
                    style.borderBottomWidth,
                    style.borderRadius,
                    style.backgroundColor,
                    style.borderLeftColor,
                    String(e.scrollWidth),
                    String(e.clientWidth),
                    String(e.getBoundingClientRect().width),
                    String(e.parentElement.clientWidth),
                    style.boxShadow,
                ];
            }
            """);
        Assert.Equal("border-box", panelStyle[0]);
        Assert.Equal("18px", panelStyle[1]);
        Assert.Equal("18px", panelStyle[2]);
        Assert.Equal("18px", panelStyle[3]);
        Assert.Equal("3px", panelStyle[4]);
        Assert.Equal("0px", panelStyle[5]);
        Assert.Equal("0px", panelStyle[6]);
        Assert.Equal("0px", panelStyle[7]);
        Assert.Equal("0px", panelStyle[8]);
        Assert.NotEqual(panelStyle[9],
            await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        Assert.NotEqual(panelStyle[9], panelStyle[10]);
        Assert.Equal(panelStyle[12], panelStyle[11]);
        Assert.InRange(
            Math.Abs(double.Parse(panelStyle[13], CultureInfo.InvariantCulture) -
                double.Parse(panelStyle[14], CultureInfo.InvariantCulture)),
            0,
            1);
        Assert.Equal("none", panelStyle[15]);
        var foreground = await Page.Locator("body").EvaluateAsync<string>("e => getComputedStyle(e).color");
        Assert.Equal(foreground,
            await panels.First.Locator(".tw-line").First.EvaluateAsync<string>("e => getComputedStyle(e).color"));
        Assert.NotEqual(panelStyle[9], foreground);

        var labelStyle = await panels.First.Locator(".tw-speaker").EvaluateAsync<string[]>("""
            e => {
                const style = getComputedStyle(e);
                return [
                    style.display,
                    style.fontSize,
                    getComputedStyle(document.documentElement).fontSize,
                    style.fontFamily,
                    style.fontVariant,
                    style.fontWeight,
                    style.letterSpacing,
                    style.color,
                    style.marginTop,
                    style.marginBottom,
                ];
            }
            """);
        Assert.Equal("block", labelStyle[0]);
        Assert.Contains("system-ui", labelStyle[3]);
        Assert.Equal("small-caps", labelStyle[4]);
        Assert.Equal("700", labelStyle[5]);
        Assert.Equal("0px", labelStyle[8]);
        Assert.Equal("9px", labelStyle[9]);
        Assert.Equal(panelStyle[10], labelStyle[7]);
        Assert.InRange(
            Math.Abs(double.Parse(labelStyle[1].Replace("px", ""), CultureInfo.InvariantCulture) /
                double.Parse(labelStyle[2].Replace("px", ""), CultureInfo.InvariantCulture) - 0.7),
            0,
            0.001);
        Assert.InRange(
            Math.Abs(double.Parse(labelStyle[6].Replace("px", ""), CultureInfo.InvariantCulture) /
                double.Parse(labelStyle[1].Replace("px", ""), CultureInfo.InvariantCulture) - 0.08),
            0,
            0.001);

        foreach (var panel in await panels.AllAsync())
        {
            Assert.True(await panel.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"));
            foreach (var line in await panel.Locator(".tw-line").AllAsync())
            {
                Assert.True(await line.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"));
            }
        }
        var firstPanel = await panels.Nth(0).BoundingBoxAsync();
        var secondPanel = await panels.Nth(1).BoundingBoxAsync();
        Assert.NotNull(firstPanel);
        Assert.NotNull(secondPanel);
        Assert.True(secondPanel.Y - (firstPanel.Y + firstPanel.Height) >= 16);
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
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
