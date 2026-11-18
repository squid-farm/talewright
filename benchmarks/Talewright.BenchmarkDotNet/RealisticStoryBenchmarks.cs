using BenchmarkDotNet.Attributes;
using System.Text.Json;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.BenchmarkDotNet;

/// <summary>Compiles and analyzes a story with realistic branches, hubs, loops, conditions and endings.</summary>
[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class RealisticStoryBenchmarks
{
    private string _root = null!;
    private StorySource _source = null!;

    /// <summary>About 50 scenes each: 20 is about 1,000 scenes and 200 about 10,000.</summary>
    [Params(20, 200)]
    public int Chapters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var story = RealisticStory.Create(new RealisticStoryProfile { Chapters = Chapters });
        _root = Path.Combine(Path.GetTempPath(), $"talewright-bdn-realistic-{Environment.ProcessId}-{Chapters}");
        story.WriteDirectory(_root);
        _source = story.ToSource();
        if (Compile(_source).Scenes.Count != story.SceneCount)
        {
            throw new InvalidOperationException("The realistic story did not compile to its generated scenes.");
        }
    }

    [Benchmark]
    public StoryDefinition CompileInMemory() => Compile(_source);

    [Benchmark]
    public StoryAnalysis AnalyzeInMemory() => StoryAnalyzer.Analyze(_source);

    [Benchmark]
    public StoryDefinition LoadAndCompileDirectory() => Compile(StorySource.FromDirectory(_root));

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static StoryDefinition Compile(StorySource source) =>
        StoryCompiler.Compile(source).Story
        ?? throw new InvalidOperationException("The realistic story did not compile.");
}

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
public class RealisticStoryBenchmarksSaves
{
    private StoryDefinition _story = null!;
    private StoryDefinition _changedStory = null!;
    private StoryHistory _history = null!;
    private byte[] _save = [];
    private int _restartCount;
    private int _completedActions;

    // Playthroughs are unlimited; stopping at the former 10,000-choice cap keeps rows comparable with the scorecard.
    private const int SavedChoiceStop = 10_000;

    [Params(1_000, 5_000, 10_000)]
    public int RequestedActions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var realistic = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 });
        var source = realistic.ToSource();
        _story = Compile(source);
        _changedStory = AddStartEffect(source);
        (_history, _restartCount, _completedActions) = Play(_story, RequestedActions);
        _save = _history.Save();
        if (RequestedActions == SavedChoiceStop && _history.PlaythroughLength != SavedChoiceStop)
        {
            throw new InvalidOperationException(
                $"The realistic route saved {_history.PlaythroughLength:N0} choices instead of stopping at {SavedChoiceStop:N0}.");
        }

        if (StoryHistory.Restore(_story, _save).Continuation is not null)
        {
            throw new InvalidOperationException("The unchanged realistic story did not use replay.");
        }
        if (StoryHistory.Restore(_changedStory, _save).Continuation is null)
        {
            throw new InvalidOperationException("The changed realistic story did not use checkpoint continuation.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            fixture = "realistic-story",
            chapters = 20,
            requestedActions = RequestedActions,
            completedActions = _completedActions,
            savedChoices = _history.PlaythroughLength,
            restarts = _restartCount,
            encodedSaveBytes = _save.Length,
            decodedSaveBytes = DecodedSaveBytes(_save),
        }));
    }

    [Benchmark]
    public byte[] Save() => _history.Save();

    [Benchmark]
    public StoryHistory RestoreReplay() => StoryHistory.Restore(_story, _save);

    [Benchmark]
    public StoryHistory RestoreContinue() => StoryHistory.Restore(_changedStory, _save);

    private static (StoryHistory History, int Restarts, int CompletedActions) Play(
        StoryDefinition story, int requestedActions)
    {
        var history = new StoryHistory(story, story.Seed);
        var restarts = 0;
        var completedActions = 0;
        for (var index = 0; index < requestedActions; index++)
        {
            if (history.PlaythroughLength >= SavedChoiceStop)
            {
                break;
            }

            if (history.Current.IsEnded)
            {
                history = new StoryHistory(story, story.Seed);
                restarts++;
            }

            var firstChoice = history.Current.Choices.FirstOrDefault()
                ?? throw new InvalidOperationException("The realistic route has no first choice to take.");
            history.Choose(firstChoice.Key);
            completedActions++;
        }

        return (history, restarts, completedActions);
    }

    private static StoryDefinition AddStartEffect(StorySource source)
    {
        var files = source.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string header = "=== c0.open";
        const string path = "chapters/c000.tale";
        if (!files.TryGetValue(path, out var text) || !text.Contains(header, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The realistic story start scene was not found.");
        }

        var effect = EffectFaker.Line(EffectFaker.Increase(TaleCast.Chapter, 1));
        files[path] = InsertAfterMetadata(text, header, effect);
        return Compile(new StorySource(files));
    }

    private static string InsertAfterMetadata(string text, string header, string effect)
    {
        var index = text.IndexOf(header, StringComparison.Ordinal);
        var insertion = text.IndexOf('\n', index + header.Length);
        if (insertion < 0)
        {
            throw new InvalidOperationException("The realistic story scene header is incomplete.");
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

    private static int DecodedSaveBytes(ReadOnlySpan<byte> save) => SaveFormat.DecodedBodyLength(save);

    private static StoryDefinition Compile(StorySource source) =>
        StoryCompiler.Compile(source).Story
        ?? throw new InvalidOperationException("The changed realistic story did not compile.");
}
