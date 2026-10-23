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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
