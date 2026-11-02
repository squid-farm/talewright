using Talewright.Cli;

namespace Talewright.Cli.Tests;

[Collection("Run command working directory")]
public sealed class NewStoryCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-new-test-{Guid.NewGuid():N}");

    public NewStoryCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Help_describes_folder_and_options()
    {
        var result = Invoke(["new", "--help"]);

        Assert.Equal(0, result.Code);
        Assert.Contains("folder", result.Output);
        Assert.Contains("--id", result.Output);
        Assert.Contains("--title", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Creates_a_runnable_story_with_a_derived_id_and_title()
    {
        var story = Path.Combine(_root, "Night Ferry");
        Directory.CreateDirectory(story);
        File.WriteAllText(Path.Combine(story, "README.md"), "Keep this file.");

        var created = Invoke(["new", story]);

        Assert.Equal(0, created.Code);
        Assert.Empty(created.Error);
        Assert.Contains("talewright run", created.Output);
        Assert.Contains("in VS Code", created.Output);

        var manifest = File.ReadAllText(Path.Combine(story, "story.talewright.yaml"));
        Assert.Contains("  id: night-ferry", manifest);
        Assert.Contains("  title: \"Night Ferry\"", manifest);
        Assert.Equal("Keep this file.", File.ReadAllText(Path.Combine(story, "README.md")));

        var validation = Invoke(["validate", story]);
        var lint = Invoke(["lint", story]);
        Assert.Equal(0, validation.Code);
        Assert.Contains("Validation succeeded.", validation.Output);
        Assert.Empty(lint.Error);
        Assert.Equal(0, lint.Code);
        Assert.Contains("Lint succeeded.", lint.Output);

        NativeHostMode? mode = null;
        string? launchedStory = null;
        var run = Invoke(["run", story], new NativeHostLauncher.Operations((actualMode, root, _, _, _) =>
        {
            mode = actualMode;
            launchedStory = root;
            return 0;
        }));
        Assert.Equal(0, run.Code);
        Assert.Equal(NativeHostMode.Player, mode);
        Assert.Equal(Path.GetFullPath(story), launchedStory);
    }

    [Fact]
    public void Omitted_folder_creates_the_story_in_the_current_directory()
    {
        var story = Path.Combine(_root, "Quiet Harbor");
        Directory.CreateDirectory(story);
        var previousDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(story);
            var result = Invoke(["new"]);

            Assert.Equal(0, result.Code);
            Assert.True(File.Exists(Path.Combine(story, "story.talewright.yaml")));
            Assert.True(File.Exists(Path.Combine(story, "story.tale")));
            Assert.Contains("  id: quiet-harbor", File.ReadAllText(Path.Combine(story, "story.talewright.yaml")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public void Creates_missing_folder_and_uses_explicit_id_and_yaml_safe_title()
    {
        var story = Path.Combine(_root, "nested", "A Draft");
        var result = Invoke(["new", story, "--id", "custom-story", "--title", "A \"Strange\": Crossing"]);

        Assert.Equal(0, result.Code);
        var manifest = File.ReadAllText(Path.Combine(story, "story.talewright.yaml"));
        Assert.Contains("  id: custom-story", manifest);
        Assert.Contains("  title: \"A \\\"Strange\\\": Crossing\"", manifest);
        Assert.Equal(0, Invoke(["validate", story]).Code);
        Assert.Equal(0, Invoke(["lint", story]).Code);
    }

    [Theory]
    [InlineData("Invalid")]
    [InlineData("has_underscore")]
    [InlineData("has.dot")]
    [InlineData("-leading-hyphen")]
    [InlineData("double--hyphen")]
    public void Invalid_explicit_ids_fail_without_creating_the_folder(string id)
    {
        var story = Path.Combine(_root, $"story-{Guid.NewGuid():N}");
        var result = Invoke(["new", story, "--id", id]);

        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI001", result.Error);
        Assert.False(Directory.Exists(story));
    }

    [Fact]
    public void Invalid_folder_name_requires_an_explicit_id()
    {
        var story = Path.Combine(_root, "!!!");
        var result = Invoke(["new", story]);

        Assert.Equal(2, result.Code);
        Assert.Contains("Supply one with --id", result.Error);
        Assert.False(Directory.Exists(story));
    }

    [Fact]
    public void Refuses_to_create_over_a_manifest()
    {
        var story = Path.Combine(_root, "existing-manifest");
        Directory.CreateDirectory(story);
        var manifestPath = Path.Combine(story, "story.talewright.yaml");
        File.WriteAllText(manifestPath, "keep manifest");

        var result = Invoke(["new", story]);

        Assert.Equal(2, result.Code);
        Assert.Contains("already contains 'story.talewright.yaml'", result.Error);
        Assert.Equal("keep manifest", File.ReadAllText(manifestPath));
        Assert.False(File.Exists(Path.Combine(story, "story.tale")));
    }

    [Fact]
    public void Refuses_to_create_in_a_folder_with_a_nested_tale_file()
    {
        var story = Path.Combine(_root, "existing-tale");
        var nested = Path.Combine(story, "chapters");
        Directory.CreateDirectory(nested);
        var existingPath = Path.Combine(nested, "chapter.tale");
        File.WriteAllText(existingPath, "keep tale");

        var result = Invoke(["new", story]);

        Assert.Equal(2, result.Code);
        Assert.Contains("already contains a .tale file", result.Error);
        Assert.Equal("keep tale", File.ReadAllText(existingPath));
        Assert.False(File.Exists(Path.Combine(story, "story.talewright.yaml")));
    }

    [Fact]
    public void Refuses_to_create_over_a_target_file_or_directory()
    {
        var story = Path.Combine(_root, "existing-target");
        Directory.CreateDirectory(story);
        var target = Path.Combine(story, "story.tale");
        Directory.CreateDirectory(target);

        var result = Invoke(["new", story]);

        Assert.Equal(2, result.Code);
        Assert.Contains("already contains 'story.tale'", result.Error);
        Assert.True(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(story, "story.talewright.yaml")));
    }

    [Fact]
    public void A_file_cannot_be_used_as_the_story_folder()
    {
        var story = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(story, "keep file");

        var result = Invoke(["new", story]);

        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI002", result.Error);
        Assert.Equal("keep file", File.ReadAllText(story));
    }

    private static (int Code, string Output, string Error) Invoke(string[] args,
        NativeHostLauncher.Operations? nativeLaunch = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(args, output, error,
            new CliInput(TextReader.Null, true, true, true), nativeLaunch: nativeLaunch);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
