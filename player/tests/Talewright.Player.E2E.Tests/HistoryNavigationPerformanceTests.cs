using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Browser")]
[Trait("Suite", "HistoryNavigationPerformance")]
public sealed class HistoryNavigationPerformanceTests : PlayerTest
{
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public HistoryNavigationPerformanceTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task Back_and_forward_report_replay_and_browser_navigation_costs(int depth)
    {
        const int samples = 5;
        var fixture = new StoryFixture("history-navigation", """
            id: history-navigation
            title: History navigation
            start: loop
            seed: 42
            characters:
              player: {first-name: Tester}
            """, """
            === loop
            ~ var:visits += 1
            Visit {var:visits}.
            * [Next] -> loop
            """);
        var history = new StoryHistory(fixture.Compile(), 42);
        for (var index = 0; index < depth; index++)
        {
            history.Choose("c0");
        }
        var save = history.Save();
        var events = history.Events.ToArray();
        var native = new List<object>();
        for (var iteration = -1; iteration < samples; iteration++)
        {
            MeasureReplay("Back", history.Back, depth - 1, iteration);
            MeasureReplay("Forward", history.Forward, depth, iteration);
            Assert.Equal(events, history.Events);
            Assert.Equal(save, history.Save());
        }

        Context.SetDefaultTimeout(180_000);
        Page.SetDefaultTimeout(180_000);
        await Upload(fixture);
        await Expect(Latest).ToContainTextAsync("Visit 1.");
        await Page.EvaluateAsync("""
            async json => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                await module.write('history-navigation', 'Navigation', new TextEncoder().encode(json), false);
            }
            """, Encoding.UTF8.GetString(save));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Navigation");
        await Driver.ClickAsync("Load save");
        await Expect(SaveStatus).ToHaveTextAsync("Loaded 'Navigation'.");
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(depth + 1);
        // Normalize focus after the setup dialog closes, before measuring navigation.
        await Latest.FocusAsync();
        await Page.EvaluateAsync("""
            () => {
                const paragraph = document.querySelector('.tw-past .tw-paragraph');
                const range = document.createRange();
                range.selectNodeContents(paragraph);
                const selection = getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
            }
            """);
        var browser = new List<JsonElement>();
        for (var iteration = -1; iteration < samples; iteration++)
        {
            await MeasureBrowser("Back", depth, iteration);
            await MeasureBrowser("Forward", depth + 1, iteration);
        }
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Forward", Exact = true })).ToBeDisabledAsync();

        _output.WriteLine(JsonSerializer.Serialize(new
        {
            depth,
            samples,
            browser = _host.BrowserName,
            browserVersion = _host.Browser.Version,
            userAgent = await Page.EvaluateAsync<string>("() => navigator.userAgent"),
            nativeRuntime = Environment.Version.ToString(),
            nativeReplay = native,
            browserNavigation = browser,
            measurement = "one warmup pair, five measured pairs; native replay is not WASM replay; " +
                "two animation frames after DOM and focus readiness indicate a paint opportunity, not proof of pixel presentation; " +
                "no percentile or retained-memory claim",
        }));

        void MeasureReplay(string direction, Action action, int cursor, int iteration)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(cursor, history.Cursor);
            Assert.Equal(cursor + 1, history.Transcript.Count);
            if (iteration >= 0)
            {
                native.Add(new { iteration, direction, elapsedMs = elapsed, allocatedBytes = bytes });
            }
        }

        async Task MeasureBrowser(string direction, int frames, int iteration)
        {
            await Page.EvaluateAsync("""
                ({ direction, frames, iteration }) => {
                    const transcript = document.querySelector('.tw-transcript');
                    const button = [...document.querySelectorAll('.tw-bar button')]
                        .find(element => element.textContent.trim() === direction);
                    if (!transcript || !button || button.disabled)
                        throw new Error('Navigation target is unavailable');
                    window.__historyNavigation = null;
                    button.addEventListener('click', () => {
                        const start = performance.now();
                        let domObservedMs = null;
                        let focusObservedMs = null;
                        let previousFrame = start;
                        let maxAnimationFrameGapMs = 0;
                        let readyFrames = 0;
                        const observe = () => {
                            const latest = transcript.querySelector('.tw-latest');
                            const ready = transcript.querySelectorAll('.tw-frame').length === frames &&
                                latest?.textContent.includes(`Visit ${frames}.`);
                            if (ready && domObservedMs === null)
                                domObservedMs = performance.now() - start;
                            if (ready && document.activeElement === latest && focusObservedMs === null)
                                focusObservedMs = performance.now() - start;
                        };
                        const observer = new MutationObserver(observe);
                        observer.observe(transcript, { childList: true, subtree: true, characterData: true, attributes: true });
                        const tick = () => {
                            const now = performance.now();
                            maxAnimationFrameGapMs = Math.max(maxAnimationFrameGapMs, now - previousFrame);
                            previousFrame = now;
                            observe();
                            readyFrames = domObservedMs !== null && focusObservedMs !== null ? readyFrames + 1 : 0;
                            if (readyFrames >= 2) {
                                observer.disconnect();
                                window.__historyNavigation = {
                                    direction, iteration, domObservedMs, focusObservedMs,
                                    nextPaintOpportunityMs: performance.now() - start,
                                    maxAnimationFrameGapMs
                                };
                            } else {
                                requestAnimationFrame(tick);
                            }
                        };
                        requestAnimationFrame(tick);
                    }, { capture: true, once: true });
                }
                """, new { direction, frames, iteration });
            await Page.GetByRole(AriaRole.Button, new() { Name = direction, Exact = true }).ClickAsync();
            await using var ready = await Page.WaitForFunctionAsync("() => window.__historyNavigation !== null");
            var result = await Page.EvaluateAsync<JsonElement>("() => window.__historyNavigation");
            await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(frames);
            await Expect(Latest).ToContainTextAsync($"Visit {frames}.");
            await Expect(Latest).ToBeFocusedAsync();
            await Expect(Page.Locator(".tw-chosen")).ToHaveCountAsync(frames - 1);
            Assert.Equal("Visit 1.", await Page.EvaluateAsync<string>("() => getSelection().toString()"));
            if (iteration >= 0)
            {
                browser.Add(result);
            }
        }
    }
}
