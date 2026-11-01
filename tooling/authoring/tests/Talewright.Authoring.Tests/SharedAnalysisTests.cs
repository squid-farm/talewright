using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Authoring.Tests;

public sealed class SharedAnalysisTests
{
    private static StorySource Source() => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] = new ManifestFaker("shared-analysis", "Shared analysis", "start").Build(),
        ["start.tale"] = Tale("Original.")
    });

    private static string Tale(string prose) =>
        string.Join('\n', SceneFaker.Header("start"), prose, DivertFaker.To("END"), "");

    private static Task HoldDelay(TimeSpan _, CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);

    [Fact]
    public async Task Callers_share_one_analysis_and_cancelling_a_waiter_does_not_cancel_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<StoryAnalysis> Analyze(StorySource source, CompilerOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return StoryAnalyzer.Analyze(source, options);
        }

        var workspace = new StoryWorkspace(Source(), null, false, Analyze, HoldDelay);
        var shared = workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150));
        Assert.Same(shared, workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150)));
        using var cancellation = new CancellationTokenSource();
        var cancelled = workspace.AnalyzeAsync(TimeSpan.Zero, cancellation.Token);
        var remaining = workspace.AnalyzeAsync(TimeSpan.Zero);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(remaining.IsCompleted);
            release.TrySetResult();
            var result = await remaining.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(result, await shared.Completion);
            Assert.Same(result.Analysis, workspace.Current);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Rapid_edits_skip_superseded_debounces_and_analyze_the_latest_snapshot_once()
    {
        var calls = 0;
        Task<StoryAnalysis> Analyze(StorySource source, CompilerOptions options, CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(StoryAnalyzer.Analyze(source, options));
        }

        var workspace = new StoryWorkspace(Source(), null, false, Analyze, HoldDelay);
        var original = workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150));
        workspace.Open("start.tale", 1, Tale("First."));
        var first = workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150));
        Assert.True(workspace.Change("start.tale", 2, Tale("Latest.")));
        var latest = workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150));
        latest.StartImmediately();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.Completion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Completion);
        var result = await latest.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Published);
        Assert.Equal(Tale("Latest."), result.Analysis.Source.Files["start.tale"]);
        Assert.Equal(1, calls);
    }
}
