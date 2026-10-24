using System.Text.Json;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.BenchmarkDotNet;
using Talewright.Testing;

if (args is ["--verify-corpora"])
{
    VerifyCorpora();
}
else if (args.Length >= 1 && args[0] == "--generate-corpus")
{
    GenerateCorpus(args);
}
else
{
    var config = ManualConfig.Create(DefaultConfig.Instance);
    config.AddJob(Job.Default
        .WithId("OutOfProcess")
        .WithLaunchCount(1)
        .WithWarmupCount(5)
        .WithIterationCount(30)
        .WithUnrollFactor(1));
    config.AddColumn(StatisticColumn.P95);
    var summaries = BenchmarkSwitcher.FromAssembly(typeof(StoryBenchmarks).Assembly).Run(args, config).ToArray();
    if (summaries.Length == 0 || summaries.Any(summary =>
        summary.HasCriticalValidationErrors ||
        summary.Reports.Length == 0 ||
        summary.Reports.Any(report => !report.Success || report.ResultStatistics is null)))
    {
        Console.Error.WriteLine("Benchmark run failed validation or did not produce successful measurements for every case.");
        Environment.ExitCode = 1;
    }
}

static void GenerateCorpus(string[] arguments)
{
    if (arguments.Length is < 3 or > 4 ||
        arguments.Length == 4 && arguments[3] != "--low-compressibility")
    {
        throw new ArgumentException(
            "Usage: --generate-corpus <Typical|Medium|Large|Gigantic> <empty-output-directory> [--low-compressibility]");
    }

    var tier = PerformanceCorpusTier.Get(arguments[1]);
    var corpus = PerformanceCorpus.Create(tier, arguments[2], arguments.Length == 4);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        corpus.Root,
        corpus.ArchivePath,
        corpus.Metadata,
    }, new JsonSerializerOptions { WriteIndented = true }));
}

static void VerifyCorpora()
{
    foreach (var tier in PerformanceCorpusTier.All)
    {
        var root = Path.Combine(Path.GetTempPath(), $"talewright-corpus-verify-{Guid.NewGuid():N}");
        using var corpus = PerformanceCorpus.Create(tier, root);
        var directoryStory = Compile(StorySource.FromDirectory(corpus.Root));
        using var archive = File.OpenRead(corpus.ArchivePath);
        var zipStory = Compile(StorySource.FromZip(archive));
        if (directoryStory.Id != zipStory.Id || directoryStory.Scenes.Count != tier.SceneCount)
        {
            throw new InvalidOperationException($"{tier.Name} directory and ZIP stories differ.");
        }

        var history = new StoryHistory(directoryStory, (ulong)corpus.Metadata.Seed);
        for (var index = 0; index < tier.HistoryDepth; index++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        var restored = StoryHistory.Restore(directoryStory, history.Save());
        if (restored.Cursor != tier.HistoryDepth || restored.Events.Count != history.Events.Count)
        {
            throw new InvalidOperationException($"{tier.Name} history did not restore correctly.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            corpus.Metadata,
            directoryCompilation = "passed",
            zipCompilation = "passed",
            restoredHistoryChoices = restored.Cursor,
        }));
    }
}

static StoryDefinition Compile(StorySource source)
{
    var result = StoryCompiler.Compile(source);
    return result.Story
        ?? throw new InvalidOperationException($"Corpus did not compile: {string.Join("\n", result.Diagnostics)}");
}
