using System.IO.Compression;
using System.Text;
using Talewright.Tale.Compilation;

namespace Talewright.Cli.Tests;

public sealed class PublishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-publish-test-{Guid.NewGuid():N}");
    private string Story => Path.Combine(_root, "story");
    private string Zip => Path.Combine(_root, "output.zip");

    public PublishTests()
    {
        Directory.CreateDirectory(Path.Combine(Story, "chapters"));
        File.WriteAllText(Path.Combine(Story, "story.yaml"),
            "id: publish\ntitle: Publish\nstart: a\ncharacters:\n  player: {first-name: Tester}\n");
        File.WriteAllText(Path.Combine(Story, "chapters", "main.tale"), "=== a\nHello.\n-> END");
        File.WriteAllText(Path.Combine(Story, "private-notes.txt"), "Not story content");
    }

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(["publish", Story, "--output", Zip, .. args], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Default_publication_is_only_validated_story_files()
    {
        var result = Run();
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var zip = ZipFile.OpenRead(Zip);
        Assert.Equal(["chapters/main.tale", "story.yaml"], zip.Entries.Select(e => e.FullName).Order());
        using var stream = File.OpenRead(Zip);
        Assert.True(StoryCompiler.Compile(StorySource.FromZip(stream)).Success);
    }

    [Fact]
    public void Existing_output_requires_force_and_failed_validation_preserves_it()
    {
        Assert.Equal(0, Run().Code);
        var before = File.ReadAllBytes(Zip);
        Assert.Equal(2, Run().Code);
        Assert.Equal(before, File.ReadAllBytes(Zip));
        File.WriteAllText(Path.Combine(Story, "chapters", "main.tale"), "=== a\n-> missing");
        Assert.Equal(1, Run("--force").Code);
        Assert.Equal(before, File.ReadAllBytes(Zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("--a")]
    [InlineData("--target", "desktop")]
    [InlineData("-a", "--target", "linux-x64")]
    [InlineData("-a", "--target", "web", "--runtime", "win-x64")]
    [InlineData("-a", "--target", "desktop", "--runtime", "unsupported")]
    public void Invalid_flags_do_not_create_an_artifact(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI001", result.Error);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public void Oversized_story_file_fails_before_publication()
    {
        using (var stream = File.OpenWrite(Path.Combine(Story, "chapters", "main.tale")))
        {
            stream.SetLength(5 * 1024 * 1024 + 1);
        }

        var result = Run();
        Assert.Equal(2, result.Code);
        Assert.Contains("5 MiB", result.Error);
        Assert.False(File.Exists(Zip));
    }

    [Theory]
    [InlineData(1, 1_800_000, "5 MiB")]
    [InlineData(12, 1_500_000, "50 MiB")]
    public void Utf16_input_that_expands_beyond_utf8_limits_preserves_existing_output(int files, int characters, string limit)
    {
        Assert.Equal(0, Run().Code);
        var original = File.ReadAllBytes(Zip);
        for (var i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(Story, $"comment-{i}.tale"),
                "// " + new string('\u4E00', characters), Encoding.Unicode);
        }
        Assert.True(StoryCompiler.Compile(StorySource.FromDirectory(Story)).Success);
        var result = Run("--force");
        Assert.Equal(2, result.Code);
        Assert.Contains(limit, result.Error);
        Assert.Equal(original, File.ReadAllBytes(Zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Symbolic_links_are_not_silently_bundled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(Story, "linked.tale"), Path.Combine(Story, "chapters", "main.tale"));
        var result = Run();
        Assert.Equal(2, result.Code);
        Assert.Contains("symbolic links", result.Error);
        Assert.False(File.Exists(Zip));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
