using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
[Collection(SerialCollection.Name)]
public sealed class PassageScrollTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Pressing_a_choice_interrupts_passage_scrolling_without_losing_the_click()
    {
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
        var paragraphs = string.Join("\n\n", Enumerable.Repeat("An earlier passage fills the transcript.", 60));
        await Upload(new StoryFixture("scrolling", TestStories.DefaultManifest($"scrolling-{Guid.NewGuid():N}").Build(), $$"""
            === start
            {{paragraphs}}
            * [Continue] -> second
            === second
            A new passage.
            * [Finish] -> finish
            === finish
            The pressed choice worked.
            -> END
            """));
        await Choose("Continue");
        await Expect(Latest).ToContainTextAsync("A new passage.");
        await Expect(Latest).ToBeFocusedAsync();
        await Page.Locator(".tw-diagnostic-report summary").ClickAsync();
        await Page.EvaluateAsync("""
            () => {
                const passage = document.querySelector('.tw-latest');
                window.scrollTo({ top: window.scrollY + passage.getBoundingClientRect().top - 400, behavior: 'instant' });
            }
            """);
        var bounds = await Choices.First.BoundingBoxAsync();
        Assert.NotNull(bounds);
        await Page.Mouse.MoveAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        await Page.EvaluateAsync("""
            async () => {
                const module = await import('./_content/Talewright.Player/Components/StoryPlayer.razor.js');
                module.revealLatest(document.querySelector('.tw-transcript'), false);
            }
            """);
        await Page.Mouse.DownAsync();
        // Hold the press across the automatic scroll animation's normal duration.
        await Task.Delay(500);
        await Page.Mouse.UpAsync();
        await Expect(Latest).ToContainTextAsync("The pressed choice worked.");
    }
}
