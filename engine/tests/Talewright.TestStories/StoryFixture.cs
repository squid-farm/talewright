using System.Text;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Testing;

public sealed record StoryFixture(string Id, string Yaml, string Tale)
{
    public IReadOnlyDictionary<string, string> Files => new Dictionary<string, string>
    {
        ["story.yaml"] = Yaml,
        ["chapters/main.tale"] = Tale,
    };

    public StorySource Source() => new(Files);

    public StoryDefinition Compile()
    {
        var result = StoryCompiler.Compile(Source());
        if (!result.Success || result.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException($"Fixture {Id}: {string.Join("\n", result.Diagnostics)}");
        }

        return result.Story!;
    }

    public byte[] Zip(string prefix = "") =>
        TestArchives.Create(Files.Select(file => new KeyValuePair<string, string>(prefix + file.Key, file.Value)));

    public void WriteDirectory(string directory)
    {
        foreach (var (path, content) in Files)
        {
            var target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }
    }
}

public sealed record PlayerStep(
    string? Choose,
    string Contains,
    string[]? Choices = null,
    string[]? Sheet = null,
    bool Ended = false);

public sealed record PlayerScenario(string Id, string Fixture, PlayerStep[] Steps);
