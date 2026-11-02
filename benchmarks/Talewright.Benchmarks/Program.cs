using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Talewright.Runtime;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Testing;

if (args.Length > 1 || args.Length == 1 &&
    args[0] is not ("--slow-input-stress" or "--choice-recovery-stress" or "--realistic-only"))
{
    Console.Error.WriteLine("Usage: Talewright.Benchmarks [--slow-input-stress | --choice-recovery-stress | --realistic-only]");
    return 2;
}

var includeStress = args.Length == 1;
var choiceRecoveryOnly = args is ["--choice-recovery-stress"];
var realisticOnly = args is ["--realistic-only"];
int[] slowSizes = includeStress
    ? [1, 5, 10, 20, 50, 100, 500, 1_000, 10_000]
    : [1, 5, 10, 20, 50, 100, 500, 1_000];
var harnessStarted = Stopwatch.GetTimestamp();
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; CPU: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("Seven measured iterations after one warmup; p95 is the maximum of seven. Allocations are current-thread managed bytes.");
Console.WriteLine("Source bytes are UTF-8 bytes of all supplied text, including the manifest; paths and images are excluded.");
Console.WriteLine("Workload\tMedian ms\tp95 ms\tMedian allocated MiB\tMedian allocated bytes\tMax allocated bytes\tSource bytes\tMedian B/source byte\tMax B/source byte\tTime budget ms\tAllocation ceiling B/source byte");
var failed = false;
if (realisticOnly)
{
    MeasureRealisticCorpus();
    Console.WriteLine(FormattableString.Invariant($"Harness total, excluding build: {Stopwatch.GetElapsedTime(harnessStarted).TotalSeconds:F2} s"));
    return failed ? 1 : 0;
}
if (choiceRecoveryOnly)
{
    MeasureSlowShape(SlowInputShape.BrokenChoiceLines, "broken choice lines");
    MeasureSlowShape(SlowInputShape.UnclosedChoiceLabel, "unclosed choice label");
    Console.WriteLine(FormattableString.Invariant($"Harness total, excluding build: {Stopwatch.GetElapsedTime(harnessStarted).TotalSeconds:F2} s"));
    return failed ? 1 : 0;
}

foreach (var scenes in new[] { 100, 1_000, 10_000 })
{
    var source = BenchmarkStories.CreateSceneLoop(scenes);
    var sourceBytes = PerformanceSource.Utf8ByteCount(source);
    var cold = Stopwatch.StartNew();
    var story = Compile(source);
    cold.Stop();
    Console.WriteLine($"First compile {scenes} scenes: {cold.Elapsed.TotalMilliseconds:F2} ms; {Encoding.UTF8.GetByteCount(source.Files[BenchmarkStories.TaleFileName])} Talemark bytes");
    Measure($"Compile {scenes} scenes", () => Compile(source), scenes == 10_000 ? 2_500 : 500, sourceBytes);
    MeasureAnalysis($"Analyze {scenes} scenes", source, scenes);
    using var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (path, text) in source.Files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(text);
        }
    }
    Measure($"ZIP load+compile {scenes}", () =>
    {
        zip.Position = 0;
        return Compile(StorySource.FromZip(zip));
    }, scenes == 10_000 ? 3_000 : 750);
    Measure($"Startup {scenes} scenes", () => new StoryHistory(story, 42), 250);
    var history = new StoryHistory(story, 42);
    for (var i = 0; i < scenes; i++)
    {
        history.Choose("c0");
    }
    var save = history.Save();
    Console.WriteLine($"Save for {scenes} choices: {save.Length} bytes");
    Measure($"Save {scenes} choices", () => history.Save(), 500);
    Measure($"Restore {scenes} choices", () =>
    {
        var restored = StoryHistory.Restore(story, save);
        if (restored.Cursor != scenes || restored.Events.Count != history.Events.Count)
        {
            throw new InvalidOperationException("Restore did not reproduce measured history.");
        }
        return restored;
    }, 2_000);
    Measure($"Back+Forward {scenes}", () =>
    {
        history.Back();
        history.Forward();
        return history;
    }, 2_000);
}

MeasureRealisticCorpus();

var slowShapes = new (SlowInputShape Shape, string Name)[]
{
    (SlowInputShape.ParagraphLines, "paragraph lines"),
    (SlowInputShape.InlineSubstitutions, "inline substitutions"),
    (SlowInputShape.NestedConditions, "nested conditions"),
    (SlowInputShape.NestedParentheses, "nested parentheses"),
    (SlowInputShape.UnaryPrefixes, "unary prefixes"),
    (SlowInputShape.UnmatchedEmphasis, "unmatched emphasis"),
    (SlowInputShape.UnclosedLinks, "unclosed links"),
    (SlowInputShape.Choices, "choices"),
    (SlowInputShape.ChoicesWithExpressions, "choices with expressions"),
    (SlowInputShape.BrokenChoiceLines, "broken choice lines"),
    (SlowInputShape.UnclosedChoiceLabel, "unclosed choice label"),
};
Console.WriteLine("Slow-input timings are report-only; 10,000 is opt-in. Inputs past a nesting limit must recover with one TW1016; broken choices must report TW1011.");
foreach (var (shape, name) in slowShapes)
{
    MeasureSlowShape(shape, name);
}
Console.WriteLine(FormattableString.Invariant($"Harness total, excluding build: {Stopwatch.GetElapsedTime(harnessStarted).TotalSeconds:F2} s"));
return failed ? 1 : 0;

StoryDefinition Compile(StorySource source)
{
    var result = StoryCompiler.Compile(source);
    return result.Story ?? throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
}

void MeasureRealisticCorpus()
{
    var profile = new RealisticStoryProfile { Chapters = 20 };
    var realistic = RealisticStory.Create(profile);
    var realisticSource = realistic.ToSource();
    var realisticBytes = PerformanceSource.Utf8ByteCount(realisticSource);
    Console.WriteLine($"Realistic corpus: {profile.Chapters} chapters, seed {profile.Seed}, {realistic.SceneCount} scenes, {realisticSource.Files.Count} files, {realisticBytes} UTF-8 source bytes including manifest.");
    Measure("Compile realistic 20 chapters", () => Compile(realisticSource), sourceBytes: realisticBytes, allocationCeiling: 55,
        validate: story =>
        {
            if (story.Scenes.Count != realistic.SceneCount)
            {
                throw new InvalidOperationException("The realistic corpus compiled to an unexpected scene count.");
            }
        });
    MeasureAnalysis("Analyze realistic 20 chapters", realisticSource, realistic.SceneCount, allocationCeiling: 95);
}

void MeasureSlowShape(SlowInputShape shape, string name)
{
    foreach (var size in slowSizes)
    {
        var source = SlowInputStories.Create(shape, size);
        var nestingLimit = shape switch
        {
            SlowInputShape.NestedConditions => 64,
            SlowInputShape.NestedParentheses or SlowInputShape.UnaryPrefixes => 256,
            _ => int.MaxValue,
        };
        var expectedChoiceErrors = shape switch
        {
            SlowInputShape.BrokenChoiceLines => size,
            SlowInputShape.UnclosedChoiceLabel => 1,
            _ => 0,
        };
        // Broken lines must stay well below the cost of analysing the valid choice they approximate.
        double? allocationCeiling = shape == SlowInputShape.BrokenChoiceLines && size >= 1_000 ? 150 : null;
        MeasureAnalysis($"Slow {name} {size}", source, 1, allocationCeiling: allocationCeiling,
            expectedNestingDiagnostic: size > nestingLimit, expectedChoiceErrors: expectedChoiceErrors);
    }
}

void MeasureAnalysis(string name, StorySource source, int expectedScenes, double? allocationCeiling = null,
    bool expectedNestingDiagnostic = false, int expectedChoiceErrors = 0)
{
    var sourceBytes = PerformanceSource.Utf8ByteCount(source);
    Measure(name, () => StoryAnalyzer.Analyze(source), sourceBytes: sourceBytes, allocationCeiling: allocationCeiling,
        validate: analysis =>
        {
            var errors = analysis.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var expectedErrors = expectedNestingDiagnostic
                ? errors.Count == 1 && errors[0].Code == "TW1016"
                : expectedChoiceErrors > 0
                    ? errors.Count == expectedChoiceErrors && errors.All(error => error.Code == "TW1011")
                    : errors.Count == 0;
            if (analysis.Scenes.Count != expectedScenes || !expectedErrors)
            {
                throw new InvalidOperationException(
                    $"{name} produced {analysis.Scenes.Count} scenes instead of {expectedScenes}, or has unexpected errors:\n{string.Join("\n", analysis.Diagnostics)}");
            }
        });
}

void Measure<T>(string name, Func<T> action, double? timeBudget = null, long? sourceBytes = null,
    double? allocationCeiling = null, Action<T>? validate = null)
{
    if (sourceBytes is <= 0 || allocationCeiling is not null && sourceBytes is null)
    {
        throw new ArgumentException("Allocation ratios require a positive source-byte count.");
    }

    var warmup = action();
    validate?.Invoke(warmup);
    GC.KeepAlive(warmup);
    var times = new double[7];
    var allocations = new long[7];
    for (var i = 0; i < 7; i++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = action();
        var finished = Stopwatch.GetTimestamp();
        allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        times[i] = (finished - started) * (1_000.0 / Stopwatch.Frequency);
        validate?.Invoke(result);
        GC.KeepAlive(result);
    }
    Array.Sort(times);
    Array.Sort(allocations);
    double? medianRatio = sourceBytes is not null ? allocations[3] / (double)sourceBytes.Value : null;
    double? maxRatio = sourceBytes is not null ? allocations[^1] / (double)sourceBytes.Value : null;
    var sourceLabel = sourceBytes?.ToString(CultureInfo.InvariantCulture) ?? "-";
    var medianLabel = medianRatio?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
    var maxLabel = maxRatio?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
    var timeBudgetLabel = timeBudget?.ToString("F0", CultureInfo.InvariantCulture) ?? "-";
    var allocationBudgetLabel = allocationCeiling?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
    Console.WriteLine(FormattableString.Invariant($"{name}\t{times[3]:F2}\t{times[^1]:F2}\t{allocations[3] / (1024.0 * 1024):F2}\t{allocations[3]}\t{allocations[^1]}\t{sourceLabel}\t{medianLabel}\t{maxLabel}\t{timeBudgetLabel}\t{allocationBudgetLabel}"));
    if (timeBudget is not null && times[^1] > timeBudget.Value)
    {
        Console.Error.WriteLine(FormattableString.Invariant($"Performance budget exceeded: {name}: {times[^1]:F2} ms > {timeBudget.Value:F0} ms."));
        failed = true;
    }
    if (allocationCeiling is not null && maxRatio > allocationCeiling.Value)
    {
        Console.Error.WriteLine(FormattableString.Invariant($"Allocation ceiling exceeded: {name}: {maxRatio:F2} B/source byte > {allocationCeiling.Value:F2}."));
        failed = true;
    }
}
