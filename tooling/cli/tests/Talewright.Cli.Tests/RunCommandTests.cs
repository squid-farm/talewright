using System.Diagnostics;
using System.Runtime.InteropServices;
using Talewright.Cli;
using Talewright.Testing;

namespace Talewright.Cli.Tests;

[CollectionDefinition("Run command working directory", DisableParallelization = true)]
public sealed class RunCommandWorkingDirectoryCollection
{
}

[Collection("Run command working directory")]
public sealed class RunCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-run-test-{Guid.NewGuid():N}");
    private string Story => Path.Combine(_root, "story with spaces");
    private string Installation => Path.Combine(_root, "installation");

    public RunCommandTests()
    {
        Directory.CreateDirectory(Story);
        var manifest = new ManifestFaker("run", "Run story", "start");
        manifest.Character("player", "Tester");
        File.WriteAllText(Path.Combine(Story, "story.talewright.yaml"), manifest.Build(trailingNewline: true));
        File.WriteAllText(Path.Combine(Story, "main.tale"), string.Join('\n',
            SceneFaker.Header("start"), "Hello.", DivertFaker.To("END")));
    }

    private static (int Code, string Output, string Error) Invoke(string[] args,
        NativeHostLauncher.Operations? launch = null, CancellationToken cancellationToken = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(args, output, error, new CliInput(TextReader.Null, true, true, true),
            nativeLaunch: launch, cancellationToken: cancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private static NativeHostLauncher.Operations CaptureLaunch(Action<NativeHostMode, string> capture) =>
        new((mode, root, _, _, _) =>
        {
            capture(mode, root);
            return 0;
        });

    private NativeHostEnvironment Platform(bool linux = false, bool windows = true, bool macOS = false,
        Architecture architecture = Architecture.X64, Func<string, string?>? environment = null,
        Func<bool>? webKit = null) =>
        new(linux, windows, macOS, architecture, "test-runtime", Installation,
            environment ?? (_ => null), webKit ?? (() => true));

    private static NativeHostLauncher.Operations LaunchOn(NativeHostEnvironment platform) =>
        new((mode, root, output, error, cancellationToken) =>
            NativeHostLauncher.Launch(mode, root, output, error, cancellationToken, platform));

    [Theory]
    [InlineData("run", "first", "second")]
    [InlineData("run", "--unknown")]
    [InlineData("run", "--format", "json")]
    [InlineData("RUN")]
    public void Invalid_arguments_are_rejected_without_launching(params string[] args)
    {
        var launched = false;
        var result = Invoke(args, CaptureLaunch((_, _) => launched = true));

        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI001", result.Error);
        Assert.False(launched);
    }

    [Fact]
    public void Help_describes_the_optional_story_folder()
    {
        var result = Invoke(["run", "--help"]);

        Assert.Equal(0, result.Code);
        Assert.Contains("story-folder", result.Output);
        Assert.Contains("--non-interactive", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Explicit_folder_is_validated_and_passed_to_the_player()
    {
        NativeHostMode? mode = null;
        string? root = null;
        var result = Invoke(["run", Story, "--non-interactive"],
            CaptureLaunch((actualMode, actualRoot) => (mode, root) = (actualMode, actualRoot)));

        Assert.Equal(0, result.Code);
        Assert.Equal(NativeHostMode.Player, mode);
        Assert.Equal(Path.GetFullPath(Story), root);
        Assert.Contains("Validation succeeded.", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Omitted_folder_uses_the_current_directory()
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Story);
            string? root = null;
            var result = Invoke(["run"], CaptureLaunch((_, actualRoot) => root = actualRoot));

            Assert.Equal(0, result.Code);
            Assert.Equal(Path.GetFullPath(Story), root);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public void Compiler_errors_match_validate_and_never_launch()
    {
        File.WriteAllText(Path.Combine(Story, "main.tale"), "=== start\n-> missing");
        var launched = false;
        var validation = Invoke(["validate", Story]);
        var result = Invoke(["run", Story], CaptureLaunch((_, _) => launched = true));

        Assert.Equal(1, validation.Code);
        Assert.Equal(validation, result);
        Assert.Contains("TW2002", result.Output);
        Assert.False(launched);
    }

    [Fact]
    public void Unreadable_story_matches_validate_and_never_launches()
    {
        var missing = Path.Combine(_root, "missing");
        var launched = false;
        var validation = Invoke(["validate", missing]);
        var result = Invoke(["run", missing], CaptureLaunch((_, _) => launched = true));

        Assert.Equal(2, validation.Code);
        Assert.Equal(validation, result);
        Assert.Contains("TWCLI002", result.Error);
        Assert.False(launched);
    }

    [Theory]
    [InlineData(false, Architecture.X64)]
    [InlineData(true, Architecture.X86)]
    public void Unsupported_platforms_report_the_runtime(bool windows, Architecture architecture)
    {
        var result = Invoke(["run", Story], LaunchOn(Platform(windows: windows, architecture: architecture)));

        Assert.Equal(2, result.Code);
        Assert.Contains("TWCLI002: Player launch failed:", result.Error);
        Assert.Contains("Talewright Player does not support runtime 'test-runtime'", result.Error);
        Assert.Contains("Windows, Linux or macOS on x64/ARM64", result.Error);
    }

    [Fact]
    public void Missing_player_assets_report_reinstallation_instead_of_launching()
    {
        Directory.CreateDirectory(Installation);
        foreach (var file in new[] { "Talewright.Debugger.dll", "Talewright.Cli.runtimeconfig.json",
            "Talewright.Cli.deps.json" })
        {
            File.WriteAllText(Path.Combine(Installation, file), "test");
        }

        var result = Invoke(["run", Story], LaunchOn(Platform()));

        Assert.Equal(2, result.Code);
        Assert.Contains("Installed player asset 'player-assets/player.html' is missing", result.Error);
        Assert.Contains("Reinstall the Talewright CLI.", result.Error);
    }

    [Fact]
    public void Headless_linux_reports_the_graphical_desktop_requirement()
    {
        var result = Invoke(["run", Story], LaunchOn(Platform(linux: true, windows: false)));

        Assert.Equal(2, result.Code);
        Assert.Contains("Talewright Player requires a graphical desktop", result.Error);
        Assert.Contains("X11 DISPLAY or Wayland WAYLAND_DISPLAY", result.Error);
    }

    [Fact]
    public void Linux_without_webkit_reports_the_existing_prerequisite()
    {
        var platform = Platform(linux: true, windows: false,
            environment: name => name == "DISPLAY" ? ":99" : null, webKit: () => false);
        var result = Invoke(["run", Story], LaunchOn(platform));

        Assert.Equal(2, result.Code);
        Assert.Contains("Install WebKitGTK 4.1 (libwebkit2gtk-4.1-0)", result.Error);
        Assert.Contains("before launching Talewright Player", result.Error);
    }

    [Fact]
    public async Task Cancellation_reaches_the_launcher_and_returns_a_nonzero_exit_code()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = new NativeHostLauncher.Operations((_, _, _, _, token) =>
        {
            started.SetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        var command = Task.Run(() => Invoke(["run", Story], launch, cancellation.Token));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await command.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(130, result.Code);
        Assert.Contains("Canceled.", result.Error);
    }

    [Fact]
    public async Task Cancellation_stops_the_started_child_process_tree()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var pidFile = Path.Combine(_root, "pids");
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sleep 30 & child=$!; printf '%s %s\\n' \"$$\" \"$child\" > \"$1\"; wait");
        start.ArgumentList.Add("talewright-run-test");
        start.ArgumentList.Add(pidFile);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var launch = Task.Run(() => NativeHostLauncher.RunProcess(start, "Talewright Player",
            output, error, cancellation.Token));

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            string[] pids = [];
            while (DateTime.UtcNow < deadline && pids.Length != 2)
            {
                if (File.Exists(pidFile))
                {
                    pids = (await File.ReadAllTextAsync(pidFile)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
                if (pids.Length != 2)
                {
                    await Task.Delay(25);
                }
            }
            Assert.Equal(2, pids.Length);
            var parentId = int.Parse(pids[0]);
            var childId = int.Parse(pids[1]);
            Assert.True(IsRunning(parentId));
            Assert.True(IsRunning(childId));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
            Assert.False(IsRunning(parentId));
            Assert.False(IsRunning(childId));
        }
        finally
        {
            if (!launch.IsCompleted)
            {
                cancellation.Cancel();
                try
                {
                    await launch.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
