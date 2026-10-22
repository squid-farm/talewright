using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;
using Talewright.Testing;

namespace Talewright.BenchmarkDotNet;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class StoryBenchmarks
{
    private PerformanceCorpus _corpus = null!;
    private StoryDefinition _story = null!;
    private StoryDefinition _hubEntryStory = null!;
    private string[] _expectedHubChoices = [];
    private StorySource _directorySource = null!;
    private StorySource _zipSource = null!;
    private StoryHistory _history = null!;
    private StoryHistory _hubHistory = null!;
    private FileSaveStore _saveStore = null!;
    private string _saveRoot = null!;
    private string _publishOutput = null!;
    private byte[] _save = [];
    private string _choiceKey = "";

    [ParamsSource(nameof(TierNames))]
    public string TierName { get; set; } = "";

    public static IEnumerable<string> TierNames => PerformanceCorpusTier.All.Select(tier => tier.Name);

    [GlobalSetup]
    public void Setup()
    {
        var tier = PerformanceCorpusTier.Get(TierName);
        var prefix = Path.Combine(Path.GetTempPath(), $"talewright-bdn-{Environment.ProcessId}-{TierName}");
        _corpus = PerformanceCorpus.Create(tier, prefix + "-story");
        _directorySource = StorySource.FromDirectory(_corpus.Root);
        _story = Compile(_directorySource);
        _zipSource = LoadZip();
        var zippedStory = Compile(_zipSource);
        if (_story.Id != zippedStory.Id || _story.Scenes.Count != tier.SceneCount)
        {
            throw new InvalidOperationException("Directory and ZIP corpus compilation did not produce the expected story.");
        }

        _history = BuildHistory(tier.HistoryDepth);
        _choiceKey = _history.Current.Choices[0].Key;
        var hubFiles = _directorySource.Files.ToDictionary(pair => pair.Key, pair => pair.Value);
        hubFiles[StorySource.ManifestFileName] = hubFiles[StorySource.ManifestFileName]
            .Replace("start: scene.0", $"start: scene.{tier.SceneCount - 1}", StringComparison.Ordinal);
        _hubEntryStory = Compile(new StorySource(hubFiles));
        _expectedHubChoices = _story.Scenes
            .Where(scene => scene.Id.Value != "scene.0")
            .OrderByDescending(scene => scene.Priority)
            .Take(20)
            .Select(scene => StorySession.StoryletKeyPrefix + scene.Id.Value)
            .ToArray();
        PrepareHubChoice();

        _save = _history.Save();
        _saveRoot = prefix + "-saves";
        _saveStore = new FileSaveStore(_saveRoot);
        _publishOutput = prefix + "-published.zip";
        BenchmarkMetadataWriter.Write(_corpus.Metadata);
    }

    [Benchmark]
    public StorySource LoadFromDirectory() => StorySource.FromDirectory(_corpus.Root);

    [Benchmark]
    public StoryDefinition LoadAndCompileDirectory() => Compile(StorySource.FromDirectory(_corpus.Root));

    [Benchmark]
    public StorySource LoadFromZip()
    {
        using var zip = File.OpenRead(_corpus.ArchivePath);
        return StorySource.FromZip(zip);
    }

    [Benchmark]
    public StoryDefinition LoadAndCompileZip()
    {
        using var zip = File.OpenRead(_corpus.ArchivePath);
        return Compile(StorySource.FromZip(zip));
    }

    [Benchmark]
    public StoryDefinition CompileInMemory() => Compile(_directorySource);

    [Benchmark]
    [InvocationCount(1)]
    public long GenerateAndCleanTypicalDiskCorpus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"talewright-bdn-generation-{Guid.NewGuid():N}");
        using var generated = PerformanceCorpus.Create(PerformanceCorpusTier.Get("Typical"), root);
        return generated.Metadata.ExpandedBytes;
    }

    [Benchmark]
    public StoryHistory StartupAndFirstFrame() => new(_story, (ulong)_corpus.Metadata.Seed);

    [Benchmark]
    [InvocationCount(1)]
    public int FirstProcessCliValidation()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, "Talewright.Cli.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("validate");
        start.ArgumentList.Add(_corpus.Root);
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add("json");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the CLI validation process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5 * 60 * 1000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("The cold CLI validation process exceeded five minutes.");
        }

        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"CLI validation failed: {error.Result}{output.Result}");
        }

        return process.ExitCode;
    }

    [Benchmark]
    [InvocationCount(1)]
    public int ChooseAtHistoryDepth()
    {
        _history.Choose(_choiceKey);
        if (_history.Cursor != PerformanceCorpusTier.Get(TierName).HistoryDepth)
        {
            throw new InvalidOperationException("Choice did not reach the configured history depth.");
        }

        return _history.Cursor;
    }

    [Benchmark]
    [InvocationCount(1)]
    public int ChooseFromBoundedHub()
    {
        _hubHistory.Choose(_hubHistory.Current.Choices[0].Key);
        var generated = _hubHistory.Current.Choices
            .Where(choice => choice.Key.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal))
            .Select(choice => choice.Key)
            .ToArray();
        if (_hubHistory.Cursor != 1 || _hubHistory.Current.Scene?.Value != "scene.0" ||
            !generated.SequenceEqual(_expectedHubChoices))
        {
            throw new InvalidOperationException("The timed transition did not enter the hub and generate its expected storylet choices.");
        }

        return _hubHistory.Cursor;
    }

    [IterationSetup(Target = nameof(ChooseFromBoundedHub))]
    public void PrepareHubChoice() =>
        _hubHistory = new StoryHistory(_hubEntryStory, (ulong)_corpus.Metadata.Seed);

    [IterationSetup(Target = nameof(ChooseAtHistoryDepth))]
    public void PrepareChoice()
    {
        _history = BuildHistory(PerformanceCorpusTier.Get(TierName).HistoryDepth - 1);
        _choiceKey = _history.Current.Choices[0].Key;
    }

    [Benchmark]
    public byte[] SerializeSaveAtHistoryDepth() => _history.Save();

    [Benchmark]
    public StoryHistory RestoreAtHistoryDepth()
    {
        var restored = StoryHistory.Restore(_story, _save);
        if (restored.Cursor != _history.Cursor || restored.Events.Count != _history.Events.Count)
        {
            throw new InvalidOperationException("Restore did not reproduce the measured history.");
        }

        return restored;
    }

    [Benchmark]
    public async Task<int> FileSaveWriteAndReadAtHistoryDepth()
    {
        const string slot = "benchmark";
        await _saveStore.WriteAsync(_story.Id, slot, _save, overwrite: true);
        var bytes = await _saveStore.ReadAsync(_story.Id, slot);
        if (!bytes.AsSpan().SequenceEqual(_save))
        {
            throw new InvalidOperationException("FileSaveStore did not reproduce the measured save.");
        }

        return bytes.Length;
    }

    [Benchmark]
    [InvocationCount(1)]
    public StoryHistory BackAndForwardAtHistoryDepth()
    {
        _history.Back();
        _history.Forward();
        if (_history.Cursor != PerformanceCorpusTier.Get(TierName).HistoryDepth)
        {
            throw new InvalidOperationException("Back/Forward did not restore the measured cursor.");
        }

        return _history;
    }

    [IterationSetup(Target = nameof(BackAndForwardAtHistoryDepth))]
    public void PrepareBackForward() =>
        _history = BuildHistory(PerformanceCorpusTier.Get(TierName).HistoryDepth);

    [Benchmark]
    public int CliStoryOnlyPublish()
    {
        var exitCode = Talewright.Cli.CommandLine.Run(
            ["publish", _corpus.Root, "--output", _publishOutput, "--force"],
            TextWriter.Null,
            TextWriter.Null);
        if (exitCode != 0 || !File.Exists(_publishOutput))
        {
            throw new InvalidOperationException($"Story-only CLI publish failed with exit code {exitCode}.");
        }

        return checked((int)new FileInfo(_publishOutput).Length);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkMetadataWriter.Write(_corpus.Metadata, Process.GetCurrentProcess().PeakWorkingSet64);
        _corpus.Dispose();
        if (Directory.Exists(_saveRoot))
        {
            Directory.Delete(_saveRoot, recursive: true);
        }

        if (File.Exists(_publishOutput))
        {
            File.Delete(_publishOutput);
        }
    }

    private StorySource LoadZip()
    {
        using var zip = File.OpenRead(_corpus.ArchivePath);
        return StorySource.FromZip(zip);
    }

    private StoryHistory BuildHistory(int depth)
    {
        var history = new StoryHistory(_story, (ulong)_corpus.Metadata.Seed);
        for (var index = 0; index < depth; index++)
        {
            history.Choose(history.Current.Choices[0].Key);
        }

        return history;
    }

    private static StoryDefinition Compile(StorySource source)
    {
        var result = StoryCompiler.Compile(source);
        if (!result.Success || result.Story is null)
        {
            throw new InvalidOperationException(
                $"Generated corpus did not compile: {string.Join(Environment.NewLine, result.Diagnostics)}");
        }

        return result.Story;
    }

}
