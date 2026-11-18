using System.Diagnostics;
using Talewright.Core.Identity;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Runtime.Tests;

public sealed class ReplayAllocationTests(ITestOutputHelper output)
{
    private const int ChoiceCount = 10_000;
    private const int RedoCursor = 5_000;
    private const long MiB = 1024 * 1024;
    private const long IndependentRestoreCeiling = 64 * MiB;
    private const long IndependentSaveCeiling = 12 * MiB;
    private const long RealisticSaveCeiling = 32 * MiB;
    private const long RealisticReplayCeiling = 896 * MiB;
    private const long RealisticContinuationCeiling = 768 * MiB;

    [Fact]
    public void Release_historical_independent_corpus_restores_within_the_64_mib_ceiling()
    {
#if DEBUG
        return;
#endif
        var root = Path.Combine(Path.GetTempPath(), $"talewright-replay-allocation-{Guid.NewGuid():N}");
        using var corpus = PerformanceCorpus.Create(
            new PerformanceCorpusTier("history-10000", 10, 100, 512 * 1024, ChoiceCount), root);
        var source = StorySource.FromDirectory(corpus.Root);
        var story = Compile(source);
        var history = new StoryHistory(story, (ulong)corpus.Metadata.Seed);
        for (var index = 0; index < ChoiceCount; index++)
            history.Choose(history.Current.Choices[0].Key);
        var save = history.Save();
        var redoHistory = StoryHistory.Restore(story, MakeRedoSave(save, RedoCursor));
        var redoSave = redoHistory.Save();
        var visits = new VarId("visits");
        var endVisits = history.State.GetVariable(visits);
        var redoVisits = redoHistory.State.GetVariable(visits);
        var endContinuationVisits = endVisits + (history.Current.Scene == new SceneId("scene.0") ? 1 : 0);
        var redoContinuationVisits = redoVisits + (redoHistory.Current.Scene == new SceneId("scene.0") ? 1 : 0);

        var files = source.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string header = "=== scene.0";
        var path = files.First(pair => pair.Key.EndsWith(".tale", StringComparison.Ordinal)
            && pair.Value.Contains(header, StringComparison.Ordinal)).Key;
        files[path] = InsertAfterMetadata(files[path], header,
            EffectFaker.Line(EffectFaker.Increase(new TaleVariableRef("visits"), 1)));
        var changed = Compile(new StorySource(files));

        Measure("historical independent restore end", () => StoryHistory.Restore(story, save),
            IndependentRestoreCeiling, ChoiceCount, ChoiceCount,
            validate: restored => Assert.Equal(endVisits, restored.State.GetVariable(visits)));
        Measure("historical independent restore redo", () => StoryHistory.Restore(story, redoSave),
            IndependentRestoreCeiling, RedoCursor, RedoCursor,
            validate: restored =>
            {
                Assert.True(restored.CanForward);
                Assert.Equal(redoVisits, restored.State.GetVariable(visits));
            });
        Measure("historical independent continuation", () => StoryHistory.Restore(changed, save),
            IndependentRestoreCeiling, 0, ChoiceCount, expectedContinuation: true,
            validate: restored => Assert.Equal(endContinuationVisits, restored.State.GetVariable(visits)));
        Measure("historical independent continuation redo", () => StoryHistory.Restore(changed, redoSave),
            IndependentRestoreCeiling, 0, RedoCursor, expectedContinuation: true,
            validate: restored =>
            {
                Assert.True(restored.Continuation!.RedoDiscarded);
                Assert.Equal(redoContinuationVisits, restored.State.GetVariable(visits));
            });
    }

    [Fact]
    public void Release_save_and_restore_allocations_stay_within_measured_ceilings()
    {
#if DEBUG
        return;
#endif
        var independent = CompileIndependentLoop();
        var independentHistory = PlayIndependentLoop(independent, ChoiceCount);
        var independentEndSave = independentHistory.Save();
        var independentRedoSave = MakeRedoSave(independentEndSave, RedoCursor);
        var independentLegacySave = MakeRedoSave(independentEndSave, ChoiceCount);
        var independentRedoHistory = StoryHistory.Restore(independent, independentRedoSave);
        var independentChanged = CompileIndependentLoop(changed: true);

        var warmedChoiceHistory = PlayIndependentLoop(independent, 100);
        warmedChoiceHistory.Choose(warmedChoiceHistory.Current.Choices[0].Key);
        var nextChoiceHistory = PlayIndependentLoop(independent, ChoiceCount - 1);
        Collect();
        var choiceTimer = Stopwatch.StartNew();
        var choiceBefore = GC.GetAllocatedBytesForCurrentThread();
        nextChoiceHistory.Choose(nextChoiceHistory.Current.Choices[0].Key);
        var choiceAllocated = GC.GetAllocatedBytesForCurrentThread() - choiceBefore;
        choiceTimer.Stop();
        Assert.Equal(ChoiceCount, nextChoiceHistory.Cursor);
        output.WriteLine($"independent next choice at depth {ChoiceCount - 1}: {choiceAllocated / (double)MiB:F2} MiB, {choiceTimer.Elapsed.TotalMilliseconds:F1} ms.");

        MeasureSave("independent save end", () => independentHistory.Save(), IndependentSaveCeiling, ChoiceCount);
        MeasureSave("independent save redo", () => independentRedoHistory.Save(), IndependentSaveCeiling, RedoCursor);
        Measure("independent restore end", () => StoryHistory.Restore(independent, independentEndSave),
            IndependentRestoreCeiling, ChoiceCount, ChoiceCount);
        Measure("independent restore legacy", () => StoryHistory.Restore(independent, independentLegacySave),
            IndependentRestoreCeiling, ChoiceCount, ChoiceCount);
        Measure("independent restore redo", () => StoryHistory.Restore(independent, independentRedoSave),
            IndependentRestoreCeiling, RedoCursor, RedoCursor);
        Measure("independent continuation restore", () => StoryHistory.Restore(independentChanged, independentEndSave),
            IndependentRestoreCeiling, 0, ChoiceCount, expectedContinuation: true);

        var independentContinuationHistory = StoryHistory.Restore(independentChanged, independentEndSave);
        var independentBaseSave = independentContinuationHistory.Save();
        Measure("independent continuation-base restore",
            () => StoryHistory.Restore(independentChanged, independentBaseSave),
            IndependentRestoreCeiling, 0, ChoiceCount);

        var realistic = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 });
        var realisticSource = realistic.ToSource();
        var realisticStory = Compile(realisticSource);
        var realisticHistory = PlayRealistic(realisticStory, ChoiceCount);
        Assert.Equal(ChoiceCount, realisticHistory.PlaythroughLength);
        Assert.Contains(realisticHistory.Transcript, frame => frame.ChosenAutomatically);
        var realisticEndSave = realisticHistory.Save();
        var realisticRedoSave = MakeRedoSave(realisticEndSave, RedoCursor);
        var realisticRedoHistory = StoryHistory.Restore(realisticStory, realisticRedoSave);
        var realisticChanged = Compile(AddStartEffect(realisticSource));

        MeasureSave("realistic save end", () => realisticHistory.Save(), RealisticSaveCeiling, ChoiceCount);
        MeasureSave("realistic save redo", () => realisticRedoHistory.Save(), RealisticSaveCeiling, RedoCursor);
        Measure("realistic restore end", () => StoryHistory.Restore(realisticStory, realisticEndSave),
            RealisticReplayCeiling, ChoiceCount, ChoiceCount);
        Measure("realistic restore redo", () => StoryHistory.Restore(realisticStory, realisticRedoSave),
            RealisticReplayCeiling, RedoCursor, RedoCursor);
        Measure("realistic continuation restore", () => StoryHistory.Restore(realisticChanged, realisticEndSave),
            RealisticContinuationCeiling, 0, ChoiceCount, expectedContinuation: true);
    }

    private void MeasureSave(string name, Func<byte[]> operation, long ceiling, int expectedCursor)
    {
        var warm = operation();
        Validate(warm);
        Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var result = operation();
        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Validate(result);
        output.WriteLine($"{name}: {allocated / (double)MiB:F2} MiB, {timer.Elapsed.TotalMilliseconds:F1} ms; ceiling {ceiling / (double)MiB:F0} MiB.");
        Assert.True(allocated <= ceiling,
            $"{name} allocated {allocated / (double)MiB:F2} MiB, above the {ceiling / (double)MiB:F0} MiB ceiling.");

        void Validate(byte[] bytes)
        {
            var save = SaveFormat.Read(bytes);
            Assert.Equal(ChoiceCount, save.Choices.Count);
            Assert.Equal(expectedCursor, save.Cursor);
        }
    }

    private void Measure(string name, Func<StoryHistory> operation, long ceiling, int expectedCursor,
        int expectedPlaythroughLength, bool expectedContinuation = false, Action<StoryHistory>? validate = null)
    {
        var warm = operation();
        Validate(warm);
        GC.KeepAlive(warm);
        Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        var result = operation();
        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Validate(result);
        GC.KeepAlive(result);
        output.WriteLine($"{name}: {allocated / (double)MiB:F2} MiB, {timer.Elapsed.TotalMilliseconds:F1} ms; ceiling {ceiling / (double)MiB:F0} MiB.");
        Assert.True(allocated <= ceiling,
            $"{name} allocated {allocated / (double)MiB:F2} MiB, above the {ceiling / (double)MiB:F0} MiB ceiling.");

        void Validate(StoryHistory history)
        {
            Assert.Equal(expectedCursor, history.Cursor);
            Assert.Equal(expectedPlaythroughLength, history.PlaythroughLength);
            Assert.Equal(expectedContinuation, history.Continuation is not null);
            validate?.Invoke(history);
        }
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static byte[] MakeRedoSave(byte[] endSave, int cursor)
    {
        var save = SaveFormat.Read(endSave) with
        {
            Cursor = cursor,
            ReplayEvidence = null,
            ReplayChoiceIdentities = null,
            Continuation = null,
        };
        return SaveFormat.Write(save);
    }

    private static StoryHistory PlayIndependentLoop(StoryDefinition story, int count)
    {
        var history = new StoryHistory(story, 42);
        for (var index = 0; index < count; index++)
            history.Choose("c0");
        return history;
    }

    private static StoryDefinition CompileIndependentLoop(bool changed = false)
    {
        var manifest = new ManifestFaker("replay-loop", "Replay loop", "start")
        {
            Version = changed ? "2" : "1",
        };
        manifest.Number("visits", 0);
        manifest.Character("player", "Tester");
        var tale = string.Join('\n',
            "=== start",
            changed ? "~ var:visits += 1" : null,
            "* [Loop] -> start",
            "* [Finish] -> END");
        return Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = tale,
        }));
    }

    private static StoryHistory PlayRealistic(StoryDefinition story, int requestedActions)
    {
        var history = new StoryHistory(story, story.Seed);
        for (var index = 0; index < requestedActions && history.PlaythroughLength < requestedActions; index++)
        {
            if (history.Current.IsEnded)
                history = new StoryHistory(story, story.Seed);
            var choice = history.Current.Choices.FirstOrDefault()
                ?? throw new InvalidOperationException("The realistic route has no available choice.");
            history.Choose(choice.Key);
        }
        return history;
    }

    private static StorySource AddStartEffect(StorySource source)
    {
        var files = source.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string header = "=== c0.open";
        const string path = "chapters/c000.tale";
        if (!files.TryGetValue(path, out var text) || !text.Contains(header, StringComparison.Ordinal))
            throw new InvalidOperationException("The realistic story start scene was not found.");
        files[path] = InsertAfterMetadata(text, header, EffectFaker.Line(EffectFaker.Increase(TaleCast.Chapter, 1)));
        return new StorySource(files);
    }

    private static string InsertAfterMetadata(string text, string header, string effect)
    {
        var headerIndex = text.IndexOf(header, StringComparison.Ordinal);
        var lineEnd = headerIndex < 0 ? -1 : text.IndexOf('\n', headerIndex + header.Length);
        if (lineEnd < 0)
            throw new InvalidOperationException("The fixture start scene header is incomplete.");
        var insertion = lineEnd + 1;
        while (insertion < text.Length && text[insertion] == '@')
        {
            var end = text.IndexOf('\n', insertion);
            insertion = end < 0 ? text.Length : end + 1;
        }
        return text.Insert(insertion, effect + "\n");
    }

    private static StoryDefinition Compile(StorySource source) =>
        StoryCompiler.Compile(source).Story
        ?? throw new InvalidOperationException("The replay allocation fixture did not compile.");
}
