using BenchmarkDotNet.Attributes;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.BenchmarkDotNet;

/// <summary>Compiles a generated story with realistic branches, hubs, loops, conditions and endings.</summary>
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
    public StoryDefinition LoadAndCompileDirectory() => Compile(StorySource.FromDirectory(_root));

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private static StoryDefinition Compile(StorySource source) =>
        StoryCompiler.Compile(source).Story
        ?? throw new InvalidOperationException("The realistic story did not compile.");
}
