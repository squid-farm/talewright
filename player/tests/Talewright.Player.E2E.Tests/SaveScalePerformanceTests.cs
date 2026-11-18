using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

[Collection(SerialCollection.Name)]
public sealed class SaveScalePerformanceTests : PlayerTest
{
    private const string PreparedSlot = "Prepared scale save";
    private const string AutosaveSlot = "Autosave scale save";
    private const string AutosaveStorageKey = "\0autosave";
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public SaveScalePerformanceTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }

    protected override bool AutoStartDesktop => false;

    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    [Fact]
    public Task Save_scale_reports_replay_continuation_and_autosave_costs_at_1_000_choices() =>
        MeasureSaveScale(1_000);

    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    [Fact]
    public Task Save_scale_reports_replay_continuation_and_autosave_costs_at_5_000_choices() =>
        MeasureSaveScale(5_000);

    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    [Fact]
    public Task Save_scale_reports_replay_continuation_and_autosave_costs_at_10_000_choices() =>
        MeasureSaveScale(10_000);

    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    [Fact]
    public async Task Quota_failure_for_a_largest_measured_save_preserves_existing_slots()
    {
        var source = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource();
        var story = Compile(source);
        var history = CreateRealisticHistory(story);
        var save = history.Save();
        const string slot = "Largest measured save";
        await WriteBrowserSaveAsync(story.Id, slot, save);
        await UploadArchive(CreateArchive(source));
        await LoadSlot(slot);

        var existingBytes = await ReadBrowserSaveAsync(story.Id, slot);
        Assert.Equal(save, existingBytes);
        await Page.EvaluateAsync("""
            () => {
                window.originalTransaction = IDBDatabase.prototype.transaction;
                IDBDatabase.prototype.transaction = function(...args) {
                    if (args[1] === "readwrite") throw new DOMException("test failure", "QuotaExceededError");
                    return window.originalTransaction.apply(this, args);
                };
            }
            """);

        try
        {
            await Driver.ClickAsync("Saves");
            await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Quota failure");
            await Driver.ClickAsync("Save game");
            await Expect(Page.GetByText("Couldn't complete save operation"))
                .ToContainTextAsync("not enough browser storage");
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load Quota failure" })).ToHaveCountAsync(0);
        }
        finally
        {
            await Page.EvaluateAsync("() => IDBDatabase.prototype.transaction = window.originalTransaction");
        }

        await Driver.ClickAsync("Retry save list");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load " + slot })).ToBeVisibleAsync();
        Assert.Equal(existingBytes, await ReadBrowserSaveAsync(story.Id, slot));
    }

    private async Task MeasureSaveScale(int choices)
    {
        var tier = PerformanceCorpusTier.Get("Typical");
        using var corpus = PerformanceCorpus.Create(
            tier, Path.Combine(Path.GetTempPath(), $"talewright-save-scale-{choices}-{Guid.NewGuid():N}"));
        var source = StorySource.FromDirectory(corpus.Root);
        var changedSource = AddStartEffect(source);
        var story = Compile(source);
        Assert.Equal(story.Id, Compile(changedSource).Id);
        var history = CreateHistory(story, choices);
        var save = SaveFormat.Write(SaveFormat.Read(history.Save()));
        var autosaveDepth = Math.Min(choices, StoryHistory.MaxChoices - 50);
        var autosaveSave = autosaveDepth == choices
            ? save
            : SaveFormat.Write(SaveFormat.Read(CreateHistory(story, autosaveDepth).Save()));
        var archive = CreateArchive(source);
        var changedArchive = CreateArchive(changedSource);

        await WriteBrowserSaveAsync(story.Id, PreparedSlot, save);
        await WriteBrowserSaveAsync(story.Id, AutosaveSlot, autosaveSave);
        await UploadArchive(archive);
        await LoadSlot(PreparedSlot);

        var replaySaveMs = await SaveNamed("Replay measurement");
        var replayBytes = await ReadBrowserSaveAsync(story.Id, "Replay measurement");
        var replayLoadMs = await LoadSlot("Replay measurement");
        await LoadSlot(AutosaveSlot);
        var replayAutosave = await MeasureAutosaveWrites();
        var replayAutosaveBytes = (await ReadBrowserSaveAsync(story.Id, AutosaveStorageKey)).Length;

        await Library();
        await UploadArchive(changedArchive);
        await LoadSlot(PreparedSlot);
        var continuationSaveMs = await SaveNamed("Continuation measurement");
        var continuationBytes = await ReadBrowserSaveAsync(story.Id, "Continuation measurement");
        var continuationLoadMs = await LoadSlot("Continuation measurement");
        await LoadSlot(AutosaveSlot);
        var continuationAutosave = await MeasureAutosaveWrites();
        var continuationAutosaveBytes = (await ReadBrowserSaveAsync(story.Id, AutosaveStorageKey)).Length;

        var jsHeap = await Page.EvaluateAsync<long[]?>("""
            () => performance.memory
                ? [
                    performance.memory.usedJSHeapSize,
                    performance.memory.totalJSHeapSize,
                    performance.memory.jsHeapSizeLimit
                ]
                : null
            """);
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            host = "browser",
            browser = _host.BrowserName,
            userAgent = await Page.EvaluateAsync<string>("() => navigator.userAgent"),
            runtime = Environment.Version.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            requestedChoices = choices,
            autosaveStartChoices = autosaveDepth,
            replay = new
            {
                indexedDbSaveMs = replaySaveMs,
                indexedDbSaveBytes = replayBytes.Length,
                indexedDbLoadMs = replayLoadMs,
                autosaveP50Ms = Percentile(replayAutosave, 0.50),
                autosaveP95Ms = Percentile(replayAutosave, 0.95),
                autosaveSaveBytes = replayAutosaveBytes,
            },
            continuation = new
            {
                indexedDbSaveMs = continuationSaveMs,
                indexedDbSaveBytes = continuationBytes.Length,
                indexedDbLoadMs = continuationLoadMs,
                autosaveP50Ms = Percentile(continuationAutosave, 0.50),
                autosaveP95Ms = Percentile(continuationAutosave, 0.95),
                autosaveSaveBytes = continuationAutosaveBytes,
            },
            jsHeapUsedBytes = jsHeap?[0],
            jsHeapTotalBytes = jsHeap?[1],
            jsHeapLimitBytes = jsHeap?[2],
            jsHeapMeasurement = jsHeap is null
                ? "not measured"
                : "performance.memory; Chromium-only JavaScript-heap approximation, not WebAssembly heap",
            autosaveSamplesPerPath = 50,
        }));
    }

    private async Task<double> SaveNamed(string name)
    {
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true });
        if (!await dialog.IsVisibleAsync())
        {
            await Driver.ClickAsync("Saves");
        }

        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync(name);
        var timer = Stopwatch.StartNew();
        await Driver.ClickAsync("Save game");
        await Expect(dialog.Locator(".tw-save-status"))
            .ToContainTextAsync($"Saved '{name}'.", new() { Timeout = 180_000 });
        timer.Stop();
        return timer.Elapsed.TotalMilliseconds;
    }

    private async Task<double> LoadSlot(string slot)
    {
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true });
        if (!await dialog.IsVisibleAsync())
        {
            await Driver.ClickAsync("Saves");
        }

        var timer = Stopwatch.StartNew();
        await Driver.ClickAsync("Load " + slot);
        var confirm = Page.GetByRole(AriaRole.Button, new() { Name = "Load save", Exact = true });
        await Page.WaitForFunctionAsync("""
            slot => {
                const confirmation = Array.from(document.querySelectorAll('button'))
                    .some(button => button.textContent.trim() === 'Load save');
                const loaded = Array.from(document.querySelectorAll('.tw-save-status'))
                    .some(status => status.textContent.includes(`Loaded '${slot}'.`));
                return confirmation || loaded;
            }
            """, slot);
        if (await confirm.IsVisibleAsync())
        {
            await confirm.ClickAsync();
        }
        await Expect(SaveStatus.First)
            .ToContainTextAsync($"Loaded '{slot}'.", new() { Timeout = 180_000 });
        timer.Stop();
        if (await dialog.IsVisibleAsync())
        {
            await Driver.ClickAsync("Close saves");
        }
        return timer.Elapsed.TotalMilliseconds;
    }

    private async Task<double[]> MeasureAutosaveWrites()
    {
        await Page.EvaluateAsync("""
            () => {
                if (window.__talewrightSaveScale) {
                    window.__talewrightSaveScale.clickStart = null;
                    window.__talewrightSaveScale.samples = [];
                    return;
                }
                const state = window.__talewrightSaveScale = { clickStart: null, samples: [] };
                document.addEventListener('click', event => {
                    if (event.target instanceof Element &&
                        event.target.closest('[aria-label="Story choices"] button')) {
                        state.clickStart = performance.now();
                    }
                }, true);
                const original = IDBDatabase.prototype.transaction;
                IDBDatabase.prototype.transaction = function(...args) {
                    const transaction = original.apply(this, args);
                    if (args[1] === "readwrite" && state.clickStart !== null) {
                        const started = state.clickStart;
                        transaction.addEventListener('complete', () => {
                            state.samples.push(performance.now() - started);
                            state.clickStart = null;
                        });
                    }
                    return transaction;
                };
            }
            """);

        for (var index = 1; index <= 50; index++)
        {
            await Choices.First.ClickAsync();
            await Page.WaitForFunctionAsync(
                "count => window.__talewrightSaveScale.samples.length >= count",
                index,
                new() { Timeout = 180_000 });
        }

        return await Page.EvaluateAsync<double[]>("() => window.__talewrightSaveScale.samples");
    }

    private async Task UploadArchive(byte[] archive)
    {
        await UploadBytes("save-scale.zip", archive);
        await ResolveAutosavePromptAsync(resumeAutosave: false);
    }

    private static StoryHistory CreateHistory(Talewright.Talemark.Model.StoryDefinition story, int choices)
    {
        var history = new StoryHistory(story, story.Seed);
        for (var index = 0; index < choices; index++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        return history;
    }

    private static StoryHistory CreateRealisticHistory(Talewright.Talemark.Model.StoryDefinition story)
    {
        var history = new StoryHistory(story, story.Seed);
        for (var action = 0; action < StoryHistory.MaxChoices; action++)
        {
            if (history.IsAtChoiceLimit)
            {
                break;
            }

            if (history.Current.IsEnded)
            {
                history = new StoryHistory(story, story.Seed);
            }

            var firstChoice = history.Current.Choices.FirstOrDefault()
                ?? throw new InvalidOperationException("The realistic route has no first choice to take.");
            history.Choose(firstChoice.Key);
        }

        Assert.Equal(StoryHistory.MaxChoices, history.PlaythroughLength);
        return history;
    }

    private static StorySource AddStartEffect(StorySource source)
    {
        var files = source.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string header = "=== scene.0";
        var path = files.First(pair => pair.Value.Contains(header, StringComparison.Ordinal)).Key;
        var effect = EffectFaker.Line(EffectFaker.Increase(new TaleVariableRef("visits"), 1));
        files[path] = InsertAfterMetadata(files[path], header, effect);
        return new StorySource(files);
    }

    private static string InsertAfterMetadata(string text, string header, string effect)
    {
        var index = text.IndexOf(header, StringComparison.Ordinal);
        var insertion = text.IndexOf('\n', index + header.Length);
        if (insertion < 0)
        {
            throw new InvalidOperationException("The performance corpus scene header is incomplete.");
        }

        insertion++;
        while (insertion < text.Length && text[insertion] == '@')
        {
            var lineEnd = text.IndexOf('\n', insertion);
            if (lineEnd < 0)
            {
                insertion = text.Length;
                break;
            }
            insertion = lineEnd + 1;
        }

        return text.Insert(insertion, effect + "\n");
    }

    private static byte[] CreateArchive(StorySource source)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in source.Files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    private static double Percentile(IReadOnlyCollection<double> samples, double percentile)
    {
        if (samples.Count != 50)
        {
            throw new InvalidOperationException($"Expected 50 autosave samples, received {samples.Count}.");
        }

        var sorted = samples.Order().ToArray();
        return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    private static Talewright.Talemark.Model.StoryDefinition Compile(StorySource source)
    {
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Story!;
    }
}
