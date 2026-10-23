using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
public sealed class BrowserObservationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
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
