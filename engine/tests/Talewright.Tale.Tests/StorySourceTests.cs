using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public sealed class StorySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-source-test-{Guid.NewGuid():N}");

    public StorySourceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1_000)]
    [InlineData(1_001)]
    public void Directory_story_file_count_has_the_same_boundary_as_archives(int count)
    {
        for (var i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"{i}.tale"), "");
        }

        if (count == 1_000)
        {
            Assert.Equal(count, StorySource.FromDirectory(_root).Files.Count);
        }
        else
        {
            Assert.Contains("1,000", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
        }
    }

    [Fact]
    public void Directory_accepts_exact_file_and_total_bounds_but_rejects_one_extra_byte()
    {
        var text = new string('x', 5 * 1024 * 1024);
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"{i}.tale"), text);
        }

        Assert.Equal(10, StorySource.FromDirectory(_root).Files.Count);
        File.WriteAllText(Path.Combine(_root, "extra.tale"), "x");
        Assert.Contains("50 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
        File.Delete(Path.Combine(_root, "extra.tale"));
        File.AppendAllText(Path.Combine(_root, "0.tale"), "x");
        Assert.Contains("5 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
    }

    [Fact]
    public void Directory_accepts_64_levels_and_rejects_65()
    {
        var path = _root;
        for (var i = 0; i < 64; i++)
        {
            path = Path.Combine(path, "d");
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "main.tale"), "Text");
        Assert.Single(StorySource.FromDirectory(_root).Files);
        Directory.CreateDirectory(Path.Combine(path, "d"));
        Assert.Contains("64 levels", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root)).Message);
    }

    [Fact]
    public void Excluded_child_is_not_read_or_counted_against_parent_limits()
    {
        File.WriteAllText(Path.Combine(_root, "main.tale"), "=== parent\n-> END\n");
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "large.tale"), new string('x', 5 * 1024 * 1024 + 1));
        for (var index = 0; index < 1_000; index++)
            File.WriteAllText(Path.Combine(child, $"{index}.tale"), "");
        var source = StorySource.FromDirectory(_root, [child]);
        Assert.Equal("main.tale", Assert.Single(source.Files).Key);
        Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root));
        File.WriteAllText(Path.Combine(_root, "large.tale"), new string('x', 5 * 1024 * 1024 + 1));
        Assert.Contains("5 MiB", Assert.Throws<InvalidDataException>(() => StorySource.FromDirectory(_root, [child])).Message);
    }

    private const string Manifest = "id: canonical\ntitle: Canonical\nstart: start\nruleset: { base: none }\ncharacters:\n  player:\n    first-name: Tester\n";
    private const string Tale = "=== start\nHello.\n-> END\n";

    [Fact]
    public void Manifest_filename_is_the_canonical_talewright_name() =>
        Assert.Equal("story.talewright.yaml", StorySource.ManifestFileName);

    [Fact]
    public void Directory_loads_canonical_manifest_and_ignores_other_yaml()
    {
        Directory.CreateDirectory(Path.Combine(_root, "chapters"));
        File.WriteAllText(Path.Combine(_root, "story.talewright.yaml"), Manifest);
        File.WriteAllText(Path.Combine(_root, "chapters", "main.tale"), Tale);
        File.WriteAllText(Path.Combine(_root, "story.yaml"), "title: Unrelated\n");
        File.WriteAllText(Path.Combine(_root, "other.talewright.yaml"), "title: Not a manifest\n");

        var source = StorySource.FromDirectory(_root);

        Assert.Equal(["chapters/main.tale", "story.talewright.yaml"], source.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Canonical", source.ReadTitle());
        Assert.True(StoryCompiler.Compile(source).Success);
    }

    [Fact]
    public void Directory_with_only_generic_story_yaml_reports_missing_canonical_manifest()
    {
        File.WriteAllText(Path.Combine(_root, "story.yaml"), Manifest);
        File.WriteAllText(Path.Combine(_root, "main.tale"), Tale);

        var source = StorySource.FromDirectory(_root);

        Assert.Null(source.ReadTitle());
        var diagnostic = Assert.Single(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
        Assert.Contains("story.talewright.yaml", diagnostic.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MyStory/")]
    public void Zip_loads_canonical_manifest_at_root_or_in_single_wrapping_folder(string prefix)
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new($"{prefix}story.talewright.yaml", Manifest),
            new($"{prefix}chapters/main.tale", Tale),
            new($"{prefix}story.yaml", "title: Unrelated\n"),
        ]));

        var source = StorySource.FromZip(zip);

        Assert.Equal(["chapters/main.tale", "story.talewright.yaml"], source.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Canonical", source.ReadTitle());
        Assert.True(StoryCompiler.Compile(source).Success);
    }

    [Fact]
    public void Zip_with_only_generic_story_yaml_reports_missing_canonical_manifest()
    {
        using var zip = new MemoryStream(Talewright.Testing.TestArchives.Create([
            new("MyStory/story.yaml", Manifest),
            new("MyStory/main.tale", Tale),
        ]));

        var source = StorySource.FromZip(zip);

        Assert.Equal(["MyStory/main.tale"], source.Files.Keys);
        Assert.Contains(StoryCompiler.Compile(source).Diagnostics, d => d.Code == "TW4002");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
