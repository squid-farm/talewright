using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class BrowserObservationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Functional_tests_use_reduced_motion_without_changing_player_preferences()
    {
        Assert.True(await Page.EvaluateAsync<bool>("() => matchMedia('(prefers-reduced-motion: reduce)').matches"));
        Assert.Equal("system", await Page.Locator("html").GetAttributeAsync("data-tw-reduced-motion"));
    }

    [Fact]
    public async Task Choosing_waits_for_the_passage_to_change_when_choice_labels_are_reused()
    {
        await Page.SetContentAsync("""
            <!DOCTYPE html>
            <main aria-label="Story transcript">
                <section aria-label="Current passage">First</section>
                <ol aria-label="Story choices"><li><button>1. Continue</button></li></ol>
            </main>
            """);
        await Page.EvaluateAsync("""
            () => {
                let step = 0;
                document.querySelector('button').onclick = () => {
                    setTimeout(() => {
                        const previous = document.querySelector('[aria-label="Current passage"]');
                        const current = document.createElement('section');
                        current.setAttribute('aria-label', 'Current passage');
                        current.textContent = ++step === 1 ? 'Second' : 'Third';
                        previous.removeAttribute('aria-label');
                        previous.after(current);
                    }, 250);
                };
            }
            """);

        await Choose("Continue");
        Assert.Equal("Second", (await Driver.ObserveAsync()).Passage);
        await Choose("Continue");
        Assert.Equal("Third", (await Driver.ObserveAsync()).Passage);
    }

    [Fact]
    public async Task Choosing_fails_if_the_click_does_not_advance_the_passage()
    {
        await Page.SetContentAsync("""
            <!DOCTYPE html>
            <main aria-label="Story transcript">
                <section aria-label="Current passage">Unchanged</section>
                <ol aria-label="Story choices"><li><button onclick="this.dataset.clicked = 'true'">1. Continue</button></li></ol>
            </main>
            """);
        Context.SetDefaultTimeout(500);

        await Assert.ThrowsAsync<TimeoutException>(() => Choose("Continue"));
        Assert.Equal("true", await Page.Locator("button").GetAttributeAsync("data-clicked"));
        Assert.Equal("Unchanged", (await Driver.ObserveAsync()).Passage);
    }

    [Fact]
    public async Task Observations_remain_coherent_while_library_is_replaced()
    {
        await Page.SetContentAsync("<div id='view'></div>");
        await Page.EvaluateAsync("""
            () => {
                const view = document.querySelector('#view');
                const library = '<div class="tw-library">Library ready</div>';
                const player = '<main aria-label="Story transcript"><section aria-label="Current passage" tabindex="-1">Ready</section><ol aria-label="Story choices"><li><button>1. Continue</button></li></ol></main><aside aria-label="Character sheet">Sheet ready</aside>';
                let playing = false;
                view.innerHTML = library;
                window.observationTimer = setInterval(() => {
                    playing = !playing;
                    view.innerHTML = playing ? player : library;
                }, 0);
            }
            """);
        try
        {
            var observedStates = new HashSet<bool>();
            for (var i = 0; i < 100; i++)
            {
                var state = await Driver.ObserveAsync();
                observedStates.Add(state.PassageVisible);
                if (state.PassageVisible)
                {
                    Assert.Equal("Ready", state.Passage);
                    Assert.Contains("Ready", state.Transcript);
                    Assert.Equal("Sheet ready", state.Sheet);
                    Assert.Equal(["Continue"], state.Choices);
                    Assert.Empty(state.Library);
                }
                else
                {
                    Assert.Equal("Library ready", state.Library);
                    Assert.Empty(state.Passage);
                    Assert.Empty(state.Transcript);
                    Assert.Empty(state.Sheet);
                    Assert.Empty(state.Choices);
                }
            }

            Assert.Equal(2, observedStates.Count);
        }
        finally
        {
            await Page.EvaluateAsync("() => clearInterval(window.observationTimer)");
        }
    }
}
