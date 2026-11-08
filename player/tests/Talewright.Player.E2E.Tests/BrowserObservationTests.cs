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

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task Choosing_waits_for_lazy_media_that_can_move_the_choice(int spacerHeight)
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteAsync("**/choice-readiness.svg", async route =>
        {
            requested.TrySetResult();
            await release.Task;
            await route.FulfillAsync(new()
            {
                ContentType = "image/svg+xml",
                Body = """<svg xmlns="http://www.w3.org/2000/svg" width="600" height="900"><rect width="600" height="900" fill="green"/></svg>""",
            });
        });
        await SetMediaChoiceAsync(spacerHeight);
        await Page.EvaluateAsync("""
            () => {
                const image = document.querySelector('.tw-media img');
                const observer = new IntersectionObserver(entries => {
                    if (entries.some(entry => entry.isIntersecting)) {
                        observer.disconnect();
                        image.src = '/choice-readiness.svg';
                    }
                });
                observer.observe(image);
            }
            """);

        Context.SetDefaultTimeout(500);
        var choose = Choose("Continue");
        try
        {
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => choose);
            Assert.Null(await Page.Locator("button").GetAttributeAsync("data-clicked"));
        }
        finally
        {
            release.TrySetResult();
            Context.SetDefaultTimeout(10_000);
        }

        // The first attempt must leave the choice untouched; once media settles it can advance.
        await Choose("Continue");
        Assert.Equal("Second", (await Driver.ObserveAsync()).Passage);
    }

    [Fact]
    public async Task Choosing_reports_unsettled_media_without_clicking()
    {
        await SetMediaChoiceAsync(0);
        Context.SetDefaultTimeout(500);

        await Assert.ThrowsAsync<TimeoutException>(() => Choose("Continue"));
        Assert.Null(await Page.Locator("button").GetAttributeAsync("data-clicked"));
        Assert.Equal("First", (await Driver.ObserveAsync()).Passage);
    }

    [Fact]
    public async Task Choosing_accepts_reported_media_failure_and_ignores_hidden_history()
    {
        await SetMediaChoiceAsync(0);
        await Page.EvaluateAsync("""
            () => {
                const image = document.querySelector('.tw-media img');
                image.hidden = true;
                const failure = document.createElement('span');
                failure.className = 'tw-media-error';
                failure.innerHTML = '<span role="status">This image could not be displayed.</span>';
                image.after(failure);
                const history = document.createElement('div');
                history.hidden = true;
                history.innerHTML = '<main aria-label="Story transcript"><span class="tw-media"><img alt="Hidden history"></span></main>';
                document.body.append(history);
            }
            """);

        await Choose("Continue");
        Assert.Equal("Second", (await Driver.ObserveAsync()).Passage);
    }

    private async Task SetMediaChoiceAsync(int spacerHeight)
    {
        await Page.SetContentAsync($$"""
            <!DOCTYPE html>
            <style>.tw-media img { display: block; max-width: 100%; height: auto; } img[hidden] { display: none; }</style>
            <main aria-label="Story transcript">
                <section aria-label="Current passage">First</section>
                <div style="height: {{spacerHeight}}px"></div>
                <span class="tw-media"><img alt="Delayed image"></span>
                <ol aria-label="Story choices"><li><button>1. Continue</button></li></ol>
            </main>
            """);
        await Page.EvaluateAsync("""
            () => document.querySelector('button').onclick = event => {
                event.currentTarget.dataset.clicked = 'true';
                const previous = document.querySelector('[aria-label="Current passage"]');
                const current = document.createElement('section');
                current.setAttribute('aria-label', 'Current passage');
                current.textContent = 'Second';
                previous.removeAttribute('aria-label');
                previous.after(current);
            }
            """);
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
