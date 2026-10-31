using System.Text;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Testing;

public sealed record StoryFixture(string Id, string Yaml, string Tale, IReadOnlyList<string>? ImagePaths = null)
{
    public IReadOnlyList<string> ExpectedWarningCodes { get; init; } = [];

    public IReadOnlyDictionary<string, string> Files => new Dictionary<string, string>
    {
        ["story.talewright.yaml"] = Yaml,
        ["chapters/main.tale"] = Tale,
    };

    // Empty asset entries exercise path lookup, not image decoding or rendering.
    internal IEnumerable<KeyValuePair<string, string>> Entries =>
        Files.Concat((ImagePaths ?? []).Select(path => new KeyValuePair<string, string>(path, "")));

    public StorySource Source() => new(Files, ImagePaths);

    public StoryDefinition Compile()
    {
        var result = StoryCompiler.Compile(Source());
        if (!result.Success || !result.Diagnostics.Select(diagnostic => diagnostic.Code).SequenceEqual(ExpectedWarningCodes))
        {
            throw new InvalidOperationException(
                $"Fixture {Id}: expected warnings [{string.Join(", ", ExpectedWarningCodes)}]; got:\n{string.Join("\n", result.Diagnostics)}");
        }

        return result.Story!;
    }

    public byte[] Zip(string prefix = "") =>
        TestArchives.Create(Entries.Select(file => new KeyValuePair<string, string>(prefix + file.Key, file.Value)));

    public void WriteDirectory(string directory)
    {
        foreach (var (path, content) in Entries)
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
