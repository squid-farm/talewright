using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; CPU: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("Seven measured iterations after one warmup; p95 is the maximum of seven. Allocations are current-thread managed bytes.");
Console.WriteLine("Workload\tMedian ms\tp95 ms\tMedian allocated MiB\tBudget ms");
var failed = false;
foreach (var scenes in new[] { 100, 1_000, 10_000 })
{
    var tale = new StringBuilder();
    for (var i = 0; i < scenes; i++)
    {
        tale.Append(CultureInfo.InvariantCulture, $$"""
            === scene.{{i}}
            @title Scene {{i}}
            ~ var:visits += 1
            Hello, {player:first-name}. Visits: {var:visits}.
            * [Next] -> scene.{{(i + 1) % scenes}}

            """);
    }

    var source = new StorySource(new Dictionary<string, string>
    {
        ["story.talewright.yaml"] = """
            game:
              id: benchmark
              title: Benchmark
              start: scene.0
              seed: 42
            characters:
              player: {first-name: Tester}
            """,
        ["main.tale"] = tale.ToString(),
    });
    var cold = Stopwatch.StartNew();
    var story = Compile(source);
    cold.Stop();
    Console.WriteLine($"First compile {scenes} scenes: {cold.Elapsed.TotalMilliseconds:F2} ms; {Encoding.UTF8.GetByteCount(tale.ToString())} Talemark bytes");
    Measure($"Compile {scenes} scenes", () => Compile(source), scenes == 10_000 ? 2_500 : 500);
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
return failed ? 1 : 0;

StoryDefinition Compile(StorySource source)
{
    var result = StoryCompiler.Compile(source);
    return result.Story ?? throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
}

void Measure<T>(string name, Func<T> action, double budget)
{
    GC.KeepAlive(action());
    var times = new List<double>();
    var allocations = new List<long>();
    for (var i = 0; i < 7; i++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        GC.KeepAlive(action());
        timer.Stop();
        times.Add(timer.Elapsed.TotalMilliseconds);
        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
    times.Sort();
    allocations.Sort();
    Console.WriteLine(FormattableString.Invariant($"{name}\t{times[3]:F2}\t{times[^1]:F2}\t{allocations[3] / (1024.0 * 1024):F2}\t{budget:F0}"));
    if (times[^1] > budget)
    {
        Console.Error.WriteLine($"Performance budget exceeded: {name}: {times[^1]:F2} ms > {budget:F0} ms.");
        failed = true;
    }
}
