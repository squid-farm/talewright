using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Tale.Compilation;
using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

public sealed class RealCorpusBrowserPerformanceTests : PlayerTest
{
    private readonly WebHostFixture _host;
    private readonly ITestOutputHelper _output;

    public RealCorpusBrowserPerformanceTests(WebHostFixture host, ITestOutputHelper output) : base(host, output)
    {
        _host = host;
        _output = output;
    }
    protected override bool AutoStartDesktop => false;

    [Theory]
    [InlineData("Typical")]
    [InlineData("Medium")]
    [InlineData("Large")]
    [InlineData("Gigantic")]
    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    public async Task Real_file_archive_loads_plays_and_restores_history(string tierName)
    {
        var tier = PerformanceCorpusTier.Get(tierName);
        using var corpus = PerformanceCorpus.Create(tier, NewRoot(tier.Name));
        var story = Compile(StorySource.FromDirectory(corpus.Root));
        var history = CreateHistory(story, tier.HistoryDepth);
        var archive = await File.ReadAllBytesAsync(corpus.ArchivePath);
        Assert.Equal(tier.ExpandedBytes, corpus.Metadata.ExpandedBytes);
        Assert.InRange(corpus.Metadata.CompressedBytes, 1, 20 * 1024 * 1024);

        Context.SetDefaultTimeout(180_000);
        Page.SetDefaultTimeout(180_000);
        await Page.EvaluateAsync("""
            () => {
                window.__talewrightLongTasks = [];
                window.__talewrightLongTasksSupported =
                    typeof PerformanceObserver !== 'undefined' &&
                    PerformanceObserver.supportedEntryTypes?.includes('longtask') === true;
                if (window.__talewrightLongTasksSupported) {
                    new PerformanceObserver(list => window.__talewrightLongTasks.push(
                        ...list.getEntries().map(entry => entry.duration)
                    )).observe({ type: 'longtask', buffered: true });
                }
            }
            """);

        var loadTimer = Stopwatch.StartNew();
        await UploadBytes(tier.Name + ".zip", archive);
        await AssertState(state => Assert.Contains("Passage for Tester;", state.Passage), TimeSpan.FromMinutes(3));
        loadTimer.Stop();

        var choiceTimer = Stopwatch.StartNew();
        await Driver.ChooseAsync("Next");
        await AssertState(state => Assert.Contains("Passage for Tester;", state.Passage), TimeSpan.FromMinutes(3));
        choiceTimer.Stop();

        const string slot = "Real corpus benchmark";
        var save = history.Save();
        var saveTimer = Stopwatch.StartNew();
        await Page.EvaluateAsync(
            """
            async ({ storyId, slot, json }) => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                await module.write(storyId, slot, new TextEncoder().encode(json), false);
            }
            """,
            new { storyId = story.Id, slot, json = System.Text.Encoding.UTF8.GetString(save) });
        saveTimer.Stop();
        await Driver.ClickAsync("Saved games");
        var restoreTimer = Stopwatch.StartNew();
        await Driver.ClickAsync("Load " + slot);
        await AssertState(state => Assert.Contains("Passage for Tester;", state.Passage), TimeSpan.FromMinutes(3));
        restoreTimer.Stop();

        var longTasks = await Page.EvaluateAsync<double[]>("() => window.__talewrightLongTasks ?? []");
        var longTasksSupported = await Page.EvaluateAsync<bool>("() => window.__talewrightLongTasksSupported ?? false");
        var userAgent = await Page.EvaluateAsync<string>("() => navigator.userAgent");
        var browserResources = await Page.EvaluateAsync<JsonElement>(
            "() => ({ hardwareConcurrency: navigator.hardwareConcurrency, deviceMemoryGB: navigator.deviceMemory ?? null })");
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            host = "browser",
            browser = _host.BrowserName,
            userAgent,
            runtime = Environment.Version.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            browserResources,
            runtimeAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local worktree",
            corpus.Metadata,
            uploadCompileFirstPlayableMs = loadTimer.Elapsed.TotalMilliseconds,
            choiceLatencyMs = choiceTimer.Elapsed.TotalMilliseconds,
            indexedDbSaveMs = saveTimer.Elapsed.TotalMilliseconds,
            indexedDbHistoryRestoreMs = restoreTimer.Elapsed.TotalMilliseconds,
            indexedDbSaveBytes = save.Length,
            mainThreadLongTaskCount = longTasks.Length,
            mainThreadLongTaskTotalMs = longTasks.Sum(),
            mainThreadLongTaskMeasurementSupported = longTasksSupported,
            measurement = "single integration observation; not a percentile",
        }));
    }

    [Fact]
    [Trait("Host", "Browser")]
    [Trait("Suite", "RealCorpusPerformance")]
    public async Task Low_compressibility_archive_stays_within_upload_limit_and_is_playable()
    {
        var tier = PerformanceCorpusTier.Get("Typical") with
        {
            Name = "Low-compressibility",
            ExpandedBytes = 10 * 1024 * 1024,
        };
        using var corpus = PerformanceCorpus.Create(tier, NewRoot(tier.Name), lowCompressibility: true);
        var archive = await File.ReadAllBytesAsync(corpus.ArchivePath);
        Assert.InRange(corpus.Metadata.CompressedBytes, 1, 20 * 1024 * 1024);

        Context.SetDefaultTimeout(180_000);
        Page.SetDefaultTimeout(180_000);
        await UploadBytes(tier.Name + ".zip", archive);
        await AssertState(state => Assert.Contains("Passage for Tester;", state.Passage), TimeSpan.FromMinutes(3));
        await Driver.ChooseAsync("Next");
        await AssertState(state => Assert.Contains("Passage for Tester;", state.Passage), TimeSpan.FromMinutes(3));
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            host = "browser",
            browser = _host.BrowserName,
            corpus.Metadata,
            measurement = "single integration observation; not a percentile",
        }));
    }

    private static string NewRoot(string name) =>
        Path.Combine(Path.GetTempPath(), $"talewright-browser-corpus-{name}-{Guid.NewGuid():N}");

    private static StoryHistory CreateHistory(Talewright.Tale.Model.StoryDefinition story, int depth)
    {
        var history = new StoryHistory(story, story.Seed);
        for (var index = 0; index < depth; index++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        return history;
    }

    private static Talewright.Tale.Model.StoryDefinition Compile(StorySource source)
    {
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Story!;
    }
}

public sealed class RealCorpusNativePerformanceTests : PlayerTest
{
    private readonly ITestOutputHelper _output;

    public RealCorpusNativePerformanceTests(WebHostFixture host, ITestOutputHelper output) : base(host, output) =>
        _output = output;
    protected override bool AutoStartDesktop => false;

    [Theory]
    [InlineData("Typical")]
    [InlineData("Medium")]
    [InlineData("Large")]
    [InlineData("Gigantic")]
    [Trait("Host", "Linux")]
    [Trait("Suite", "RealCorpusPerformance")]
    public async Task Photino_opens_real_file_corpus_and_accepts_a_choice(string tierName)
    {
        var tier = PerformanceCorpusTier.Get(tierName);
        using var corpus = PerformanceCorpus.Create(
            tier,
            Path.Combine(Path.GetTempPath(), $"talewright-native-corpus-{tier.Name}-{Guid.NewGuid():N}"));

        var loadTimer = Stopwatch.StartNew();
        await Driver.StartAsync(corpus.Root);
        await AssertState(
            state => Assert.Contains("Passage for Tester;", state.Passage),
            TimeSpan.FromMinutes(3));
        loadTimer.Stop();

        var choiceTimer = Stopwatch.StartNew();
        await Driver.ChooseAsync("Next");
        await AssertState(
            state => Assert.Contains("Passage for Tester;", state.Passage),
            TimeSpan.FromMinutes(3));
        choiceTimer.Stop();

        _output.WriteLine(JsonSerializer.Serialize(new
        {
            host = "Linux Photino",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            physicalMemoryBytes = ReadPhysicalMemoryBytes(),
            commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local worktree",
            corpus.Metadata,
            coldApplicationOpenAndFirstPlayableMs = loadTimer.Elapsed.TotalMilliseconds,
            choiceLatencyMs = choiceTimer.Elapsed.TotalMilliseconds,
            measurement = "single integration observation; first process/cache state documented by runner",
        }));
    }

    private static long? ReadPhysicalMemoryBytes()
    {
        const string memoryInfo = "/proc/meminfo";
        if (!File.Exists(memoryInfo))
        {
            return null;
        }

        var line = File.ReadLines(memoryInfo)
            .FirstOrDefault(value => value.StartsWith("MemTotal:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kibibytes)
            ? kibibytes * 1024
            : null;
    }
}
