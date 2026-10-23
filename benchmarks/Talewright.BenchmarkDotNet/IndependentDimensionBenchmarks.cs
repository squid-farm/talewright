using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Talewright.Runtime;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;
using Talewright.Testing;

namespace Talewright.BenchmarkDotNet;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class IndependentDimensionBenchmarks
{
    private PerformanceCorpus _corpus = null!;
    private StoryDefinition _story = null!;
    private byte[] _save = [];

    [ParamsSource(nameof(Cases))]
    public string CorpusCase { get; set; } = "";

    public static IEnumerable<string> Cases =>
    [
        "files-10", "files-100", "files-500", "files-1000",
        "bytes-512KiB", "bytes-5MiB", "bytes-25MiB", "bytes-50MiB",
        "scenes-100", "scenes-1000", "scenes-10000", "scenes-50000",
        "history-100", "history-1000", "history-5000", "history-10000",
    ];

    [GlobalSetup]
    public void Setup()
    {
        var tier = GetTier(CorpusCase);
        var root = Path.Combine(Path.GetTempPath(), $"talewright-bdn-dim-{Environment.ProcessId}-{CorpusCase}");
        _corpus = PerformanceCorpus.Create(tier, root);
        var source = StorySource.FromDirectory(_corpus.Root);
        var result = StoryCompiler.Compile(source);
        _story = result.Story
            ?? throw new InvalidOperationException($"Generated corpus did not compile: {string.Join("\n", result.Diagnostics)}");
        if (_story.Scenes.Count != tier.SceneCount)
        {
            throw new InvalidOperationException($"Expected {tier.SceneCount} scenes; compiled {_story.Scenes.Count}.");
        }

        var history = new StoryHistory(_story, (ulong)_corpus.Metadata.Seed);
        for (var index = 0; index < tier.HistoryDepth; index++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        _save = history.Save();
        BenchmarkMetadataWriter.Write(_corpus.Metadata);
    }

    [Benchmark]
    public StoryDefinition LoadAndCompileFromDirectory()
    {
        var result = StoryCompiler.Compile(StorySource.FromDirectory(_corpus.Root));
        return result.Story
            ?? throw new InvalidOperationException($"Generated corpus did not compile: {string.Join("\n", result.Diagnostics)}");
    }

    [Benchmark]
    public StoryHistory RestoreAtConfiguredDepth()
    {
        var restored = StoryHistory.Restore(_story, _save);
        if (restored.Cursor != GetTier(CorpusCase).HistoryDepth)
        {
            throw new InvalidOperationException("Restore did not reproduce the configured history depth.");
        }

        return restored;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkMetadataWriter.Write(_corpus.Metadata, Process.GetCurrentProcess().PeakWorkingSet64);
        _corpus.Dispose();
    }

    private static PerformanceCorpusTier GetTier(string corpusCase)
    {
        var parts = corpusCase.Split('-', 2);
        return parts[0] switch
        {
            "files" => new PerformanceCorpusTier(
                corpusCase, int.Parse(parts[1]), 100, 512 * 1024, 100),
            "bytes" => new PerformanceCorpusTier(
                corpusCase, 100, 100, ParseBytes(parts[1]), 100),
            "scenes" => new PerformanceCorpusTier(
                corpusCase, 100, int.Parse(parts[1]), 50L * 1024 * 1024, 100),
            "history" => new PerformanceCorpusTier(
                corpusCase, 10, 100, 512 * 1024, int.Parse(parts[1])),
            _ => throw new ArgumentException($"Unknown independent corpus case '{corpusCase}'.", nameof(corpusCase)),
        };
    }

    private static long ParseBytes(string value) => value switch
    {
        "512KiB" => 512L * 1024,
        "5MiB" => 5L * 1024 * 1024,
        "25MiB" => 25L * 1024 * 1024,
        "50MiB" => 50L * 1024 * 1024,
        _ => throw new ArgumentException($"Unknown corpus byte volume '{value}'.", nameof(value)),
    };
}
