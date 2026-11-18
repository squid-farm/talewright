using System.Diagnostics;
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
    private const long RealisticReplayCeiling = 1_536 * MiB;
    private const long RealisticContinuationCeiling = 1_280 * MiB;

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
        int expectedPlaythroughLength, bool expectedContinuation = false)
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
        var insertion = text.IndexOf('\n', text.IndexOf(header, StringComparison.Ordinal) + header.Length) + 1;
        while (insertion < text.Length && text[insertion] == '@')
        {
            var end = text.IndexOf('\n', insertion);
            insertion = end < 0 ? text.Length : end + 1;
        }
        files[path] = text.Insert(insertion, EffectFaker.Line(EffectFaker.Increase(TaleCast.Chapter, 1)) + "\n");
        return new StorySource(files);
    }

    private static StoryDefinition Compile(StorySource source) =>
        StoryCompiler.Compile(source).Story
        ?? throw new InvalidOperationException("The replay allocation fixture did not compile.");
}
