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
        => RunControlled(new UnreadableInput(), inputRedirected: true, args: args);

    private (int Code, string Output, string Error) RunControlled(TextReader input,
        bool inputRedirected = false, bool outputRedirected = false, bool errorRedirected = false,
        PublishCommand.Operations? publication = null, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(["publish", Story, "--output", Zip, .. args], output, error,
            new CliInput(input, inputRedirected, outputRedirected, errorRedirected), publication);
        return (code, output.ToString(), error.ToString());
    }

    private sealed class UnreadableInput : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("Input must not be read.");
    }

    private PublishCommand.Operations DuringArchive(Action action) => PublishCommand.Operations.Default with
    {
        CreateZip = (content, path, universal) =>
        {
            PublishCommand.Operations.Default.CreateZip(content, path, universal);
            action();
        },
    };

    [Theory]
    [InlineData("y\n")]
    [InlineData("YES\n")]
    [InlineData(" Yes \n")]
    public void Interactive_acceptance_replaces_existing_archive(string answer)
    {
        File.WriteAllText(Zip, "original");
        var result = RunControlled(new StringReader(answer));
        Assert.Equal(0, result.Code);
        Assert.Contains(Path.GetFullPath(Zip), result.Error);
        Assert.Contains("[y/N]", result.Error);
        Assert.DoesNotContain("Replace", result.Output);
        Assert.Contains("Published story archive:", result.Output);
        using var archive = ZipFile.OpenRead(Zip);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("\n")]
    [InlineData("")]
    [InlineData("maybe\n")]
    [InlineData("   \n")]
    public void Decline_default_eof_and_invalid_answer_preserve_artifact_before_any_build(string answer)
    {
        File.WriteAllText(Zip, "original");
        File.WriteAllText(Path.Combine(Story, "chapters", "main.tale"), "=== a\n-> missing");
        var calls = 0;
        var publication = PublishCommand.Operations.Default with
        {
            BuildHost = (_, _, _, _, _, _) => { calls++; throw new InvalidOperationException("Unexpected build"); },
            CreateZip = (_, _, _) => { calls++; throw new InvalidOperationException("Unexpected archive"); },
        };
        var result = RunControlled(new StringReader(answer), publication: publication, args: ["-a"]);
        Assert.Equal(2, result.Code);
        Assert.Equal(0, calls);
        Assert.Equal("original", File.ReadAllText(Zip));
        Assert.Contains("Publication cancelled", result.Error);
        Assert.Empty(result.Output);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Noninteractive_or_redirected_existing_destination_never_reads_input(
        bool inputRedirected, bool outputRedirected, bool explicitOption)
    {
        File.WriteAllText(Zip, "original");
        var result = RunControlled(new UnreadableInput(), inputRedirected, outputRedirected,
            args: explicitOption ? ["--non-interactive"] : []);
        Assert.Equal(2, result.Code);
        Assert.Equal("original", File.ReadAllText(Zip));
        Assert.Contains("--force", result.Error);
        Assert.Contains("--output", result.Error);
        Assert.DoesNotContain("[y/N]", result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redirected_prompt_stream_never_reads_input(bool force)
    {
        File.WriteAllText(Zip, "original");
        var result = RunControlled(new UnreadableInput(), errorRedirected: true,
            args: force ? ["--force"] : []);
        Assert.Equal(force ? 0 : 2, result.Code);
        Assert.DoesNotContain("[y/N]", result.Error);
        if (!force)
        {
            Assert.Equal("original", File.ReadAllText(Zip));
            Assert.Contains("--force", result.Error);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Force_authorizes_replacement_without_reading_input(
        bool inputRedirected, bool outputRedirected, bool explicitOption)
    {
        File.WriteAllText(Zip, "original");
        var result = RunControlled(new UnreadableInput(), inputRedirected, outputRedirected,
            args: explicitOption ? ["--non-interactive", "--force"] : ["--force"]);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var archive = ZipFile.OpenRead(Zip);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void New_destination_never_reads_input(bool inputRedirected, bool outputRedirected, bool explicitOption)
    {
        var result = RunControlled(new UnreadableInput(), inputRedirected, outputRedirected,
            args: explicitOption ? ["--non-interactive"] : []);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.True(File.Exists(Zip));
    }

    [Fact]
    public void Injected_writer_api_never_prompts()
    {
        File.WriteAllText(Zip, "original");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Run(["publish", Story, "--output", Zip], output, error));
        Assert.Contains("--force", error.ToString());
        Assert.Equal("original", File.ReadAllText(Zip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_failure_preserves_approved_or_forced_destination(bool force)
    {
        File.WriteAllText(Zip, "original");
        string? working = null;
        var publication = PublishCommand.Operations.Default with
        {
            BuildHost = (directory, _, _, _, _, _) =>
            {
                working = directory;
                throw new PublishCommand.PublishBuildException("controlled build failure");
            },
        };
        var result = RunControlled(force ? new UnreadableInput() : new StringReader("yes\n"),
            publication: publication, args: force ? ["-a", "--force"] : ["-a"]);
        Assert.Equal(1, result.Code);
        Assert.Contains("controlled build failure", result.Error);
        Assert.DoesNotContain("Published", result.Output);
        Assert.Equal("original", File.ReadAllText(Zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.NotNull(working);
        Assert.False(Directory.Exists(working));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_archive_write_failure_preserves_destination_and_cleans_temporary_zip(bool force)
    {
        File.WriteAllText(Zip, "original");
        var publication = PublishCommand.Operations.Default with
        {
            CreateZip = (_, path, _) =>
            {
                File.WriteAllText(path, "partial archive");
                throw new IOException("controlled write failure");
            },
        };
        var result = RunControlled(force ? new UnreadableInput() : new StringReader("yes\n"),
            publication: publication, args: force ? ["--force"] : []);
        Assert.Equal(2, result.Code);
        Assert.Contains("controlled write failure", result.Error);
        Assert.Equal("original", File.ReadAllText(Zip));
        Assert.DoesNotContain("Published", result.Output);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Failed_validation_after_approval_preserves_destination()
    {
        File.WriteAllText(Zip, "original");
        File.WriteAllText(Path.Combine(Story, "chapters", "main.tale"), "=== a\n-> missing");
        var result = RunControlled(new StringReader("yes\n"));
        Assert.Equal(1, result.Code);
        Assert.Contains("TW2002", result.Output);
        Assert.Equal("original", File.ReadAllText(Zip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Destination_appearing_during_build_requires_force(bool force)
    {
        var result = RunControlled(new UnreadableInput(),
            publication: DuringArchive(() => File.WriteAllText(Zip, "other publisher")),
            args: force ? ["--force"] : []);
        Assert.Equal(force ? 0 : 2, result.Code);
        if (force)
        {
            using var archive = ZipFile.OpenRead(Zip);
            Assert.Equal(2, archive.Entries.Count);
        }
        else
        {
            Assert.Equal("other publisher", File.ReadAllText(Zip));
            Assert.DoesNotContain("Published", result.Output);
        }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("metadata")]
    [InlineData("replace")]
    [InlineData("remove")]
    public void Approved_destination_changes_are_not_unrestricted_authorization(string change)
    {
        File.WriteAllText(Zip, "original");
        var modified = File.GetLastWriteTimeUtc(Zip);
        var result = RunControlled(new StringReader("yes\n"), publication: DuringArchive(() =>
        {
            switch (change)
            {
                case "content":
                    File.WriteAllText(Zip, "modified");
                    File.SetLastWriteTimeUtc(Zip, modified);
                    break;
                case "metadata":
                    File.SetLastWriteTimeUtc(Zip, modified.AddMinutes(1));
                    break;
                case "replace":
                    var other = Path.Combine(_root, "other.zip");
                    File.WriteAllText(other, "replacement");
                    File.Move(other, Zip, true);
                    break;
                case "remove":
                    File.Delete(Zip);
                    break;
            }
        }));
        Assert.Equal(2, result.Code);
        Assert.Contains(change == "remove" ? "disappeared" : "changed", result.Error);
        Assert.DoesNotContain("Published", result.Output);
        if (change == "remove")
        {
            Assert.False(File.Exists(Zip));
        }
        else
        {
            Assert.Equal(change == "content" ? "modified" : change == "replace" ? "replacement" : "original",
                File.ReadAllText(Zip));
        }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Force_authorizes_destination_changes_during_build()
    {
        File.WriteAllText(Zip, "original");
        var result = RunControlled(new UnreadableInput(),
            publication: DuringArchive(() => File.WriteAllText(Zip, "other publisher")), args: ["--force"]);
        Assert.Equal(0, result.Code);
        using var archive = ZipFile.OpenRead(Zip);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Fact]
    public void Directory_appearing_at_commit_reports_move_failure_and_cleans_temporary_zip()
    {
        var result = RunControlled(new UnreadableInput(), publication: DuringArchive(() =>
        {
            Directory.CreateDirectory(Zip);
            File.WriteAllText(Path.Combine(Zip, "keep"), "other publisher");
        }), args: ["--force"]);
        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI002", result.Error);
        Assert.DoesNotContain("Published", result.Output);
        Assert.Equal("other publisher", File.ReadAllText(Path.Combine(Zip, "keep")));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_final_move_preserves_existing_destination(bool force)
    {
        File.WriteAllText(Zip, "original");
        var publication = PublishCommand.Operations.Default with
        {
            CreateZip = (content, path, universal) =>
            {
                PublishCommand.Operations.Default.CreateZip(content, path, universal);
                File.Delete(path);
            },
        };
        var result = RunControlled(force ? new UnreadableInput() : new StringReader("yes\n"),
            publication: publication, args: force ? ["--force"] : []);
        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI002", result.Error);
        Assert.Equal("original", File.ReadAllText(Zip));
        Assert.DoesNotContain("Published", result.Output);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
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
