using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class ToolbarTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    [Trait("Host", "Browser")]
    public async Task Overflow_is_disabled_until_its_module_is_initialized()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/PlayerToolbar.razor.js", async route =>
        {
            requested.SetResult();
            await release.Task;
            await route.ContinueAsync();
        });
        try
        {
            await Upload(TestStories.Linear);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await AssertState(state => Assert.True(state.PassageFocused));
            var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "More actions" });
            await Expect(toggle).ToBeVisibleAsync();
            await Expect(toggle).ToBeDisabledAsync();
            await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
            release.SetResult();
            await Expect(toggle).ToBeEnabledAsync();
            await toggle.ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true })).ToBeFocusedAsync();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Save_refresh_does_not_pull_focus_back_into_open_disclosure()
    {
        await Upload(TestStories.Linear);
        await AssertState(state => Assert.True(state.PassageFocused));
        await Driver.ClickAsync("More actions");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Page.Keyboard.PressAsync("Shift+Tab");
        var saves = Page.GetByRole(AriaRole.Button, new() { Name = "Saved games", Exact = true });
        await Expect(saves).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Save game", Exact = true })).ToBeEnabledAsync();
        await Expect(saves).ToBeEnabledAsync();
        Assert.False(await Page.Locator(".tw-overflow-actions").EvaluateAsync<bool>(
            "menu => menu.contains(document.activeElement)"), "Save refresh must not pull focus into the disclosure.");
        await saves.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(saves).ToBeFocusedAsync();
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Open_overflow_stays_inside_viewport_when_primary_controls_wrap()
    {
        await Page.SetViewportSizeAsync(240, 900);
        await Upload(TestStories.Linear);
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "More actions" });
        var back = Page.GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true });
        await toggle.ClickAsync();
        var toggleBounds = (await toggle.BoundingBoxAsync())!;
        var backBounds = (await back.BoundingBoxAsync())!;
        Assert.True(toggleBounds.Y >= backBounds.Y + backBounds.Height, "The controls wrap onto a second row.");
        foreach (var name in new[] { "Restart", "Library" })
        {
            var action = Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
            await Expect(action).ToBeVisibleAsync();
            var bounds = (await action.BoundingBoxAsync())!;
            Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= 240, $"{name} is fully inside the viewport.");
        }
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1100)]
    public async Task Toolbar_stays_visible_when_scrolling_both_directions(int width)
    {
        await Upload(TestStories.Long);
        await AssertState(state => Assert.True(state.PassageFocused));
        await Driver.ResizeAsync(width, 900);
        var first = await PassageTop();
        await ScrollToEnd();
        await WaitForPassageTop(top => top < first - 100);
        await AssertToolbarVisible();
        await ScrollToStart();
        await WaitForPassageTop(top => Math.Abs(top - first) < 3);
        await AssertToolbarVisible();
        await Choose("Next passage");
        await Observe(new(null, "The new passage is visible."));
        await AssertToolbarVisible();
        await AssertState(state => Assert.True(state.PassageInWindow));
    }

    [Theory]
    [InlineData(390)]
    [InlineData(1100)]
    [Trait("Host", "Browser")]
    public async Task Focused_choices_and_new_passages_are_below_the_toolbar(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await Upload(TestStories.Long);
        // The first passage scrolls to the top and takes focus once revealed; wait so it cannot undo the focus below.
        await AssertState(state => Assert.True(state.PassageFocused));
        await Choices.First.FocusAsync();
        await Expect(Choices.First).ToBeInViewportAsync();
        var bar = (await Page.Locator(".tw-bar").BoundingBoxAsync())!;
        var choice = (await Choices.First.BoundingBoxAsync())!;
        Assert.True(choice.Y >= bar.Y + bar.Height);
        await Page.Keyboard.PressAsync("Enter");
        await Observe(new(null, "The new passage is visible."));
        bar = (await Page.Locator(".tw-bar").BoundingBoxAsync())!;
        var passage = (await Latest.BoundingBoxAsync())!;
        Assert.True(passage.Y >= bar.Y + bar.Height);
        Assert.InRange(bar.Height, 44, 80);
    }

    [Fact]
    public async Task Restart_can_be_cancelled_without_losing_progress()
    {
        await Upload(TestStories.Linear);
        await Choose("Walk onward");
        await Driver.ClickAsync("More actions");
        await Driver.ClickAsync("Restart");
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Second passage text."));
        await Restart();
        await Observe(new(null, "First passage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Host", "Browser")]
    public async Task Overflow_keyboard_modal_and_themed_background_work(bool dark)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(TestStories.Linear);
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "More actions" });
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true })).ToBeHiddenAsync();
        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(toggle).ToBeFocusedAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await toggle.ClickAsync();
        await Driver.ClickAsync("Restart");
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Restart story?" });
        await Expect(dialog).ToBeVisibleAsync();
        Assert.True(await dialog.EvaluateAsync<bool>("e => e.matches(':modal')"));
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Keep playing" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(toggle).ToBeFocusedAsync();
        Assert.Equal(dark ? "rgb(28, 26, 23)" : "rgb(250, 248, 243)",
            await Page.Locator(".tw-bar").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true })).ToBeDisabledAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Forward", Exact = true })).ToBeDisabledAsync();
        await Choose("Walk onward");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true })).ToBeEnabledAsync();
        await Driver.ClickAsync("Back");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Forward", Exact = true })).ToBeEnabledAsync();
    }

    private async Task<double> PassageTop()
    {
        if (Driver is NativePlayerDriver native)
        {
            return (await native.SnapshotAsync()).Descendants().First(node => node.Name == "Current passage").Bounds.Y;
        }
        return (await Latest.BoundingBoxAsync())!.Y;
    }

    private async Task ScrollToEnd()
    {
        if (PlayerTarget.IsDesktop)
        {
            await Driver.PressAsync("End");
        }
        else
        {
            await Page.EvaluateAsync("() => window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' })");
        }
    }

    private async Task ScrollToStart()
    {
        if (PlayerTarget.IsDesktop)
        {
            await Driver.PressAsync("Home");
        }
        else
        {
            await Page.EvaluateAsync("() => window.scrollTo({ top: 0, behavior: 'instant' })");
        }
    }

    private async Task WaitForPassageTop(Func<double, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition(await PassageTop()))
        {
            Assert.True(DateTime.UtcNow < deadline, "The real scroll changed the passage position.");
            await Task.Delay(100);
        }
    }

    private async Task AssertToolbarVisible()
    {
        if (Driver is NativePlayerDriver native)
        {
            var nodes = (await native.SnapshotAsync()).Descendants().ToArray();
            var window = nodes.First(node => node.Role == "frame").Bounds;
            foreach (var name in new[] { "Back", "Forward", "Saved games", "More actions" })
            {
                var bounds = nodes.First(node => node.Role == "push button" && node.Name == name).Bounds;
                Assert.True(bounds.Y >= window.Y && bounds.Y + bounds.Height <= window.Y + window.Height, $"{name} remains visible.");
                Assert.True(bounds.X >= window.X && bounds.X + bounds.Width <= window.X + window.Width, $"{name} is not clipped.");
            }
        }
        else
        {
            foreach (var name in new[] { "Back", "Forward", "Saved games", "More actions" })
            {
                await Expect(Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true })).ToBeInViewportAsync();
            }
            Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));
            var bar = (await Page.Locator(".tw-bar").BoundingBoxAsync())!;
            var sheet = (await Sheet.BoundingBoxAsync())!;
            Assert.True(sheet.Y >= bar.Y + bar.Height, "Character sheet does not overlap the toolbar.");
        }
    }
}
