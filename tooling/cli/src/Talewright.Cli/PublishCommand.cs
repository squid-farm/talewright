using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using Talewright.Player;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli;

public sealed class PublishSettings : CliSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";

    [CommandOption("-o|--output <PATH>")]
    public string? Destination { get; set; }

    [CommandOption("-a|--all-in-one")]
    public bool AllInOne { get; set; }

    [CommandOption("--target <TARGET>")]
    public string? Target { get; set; }

    [CommandOption("--runtime <RID>")]
    public string? Runtime { get; set; }

    [CommandOption("--force")]
    public bool Force { get; set; }

    [CommandOption("--split")]
    [Description("Publish one ZIP per supported desktop runtime; --output is a directory.")]
    public bool Split { get; set; }

    public override ValidationResult Validate()
    {
        var targetAll = Target == "all";
        if (Destination is null)
        {
            return ValidationResult.Error(targetAll
                ? "--target all requires --output <directory>."
                : "--output <file.zip> is required.");
        }

        if (targetAll)
        {
            if (Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Error("--target all uses --output as a directory, not a ZIP path.");
            }
            if (Runtime is not null)
            {
                return ValidationResult.Error("--runtime cannot be combined with --target all.");
            }
        }
        else if (!Split && !Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Error("--output <file.zip> is required.");
        }

        if (Split && Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Error("--split uses --output as a directory, not a ZIP path.");
        }

        if (Split && (!AllInOne || (Target != "desktop" && Target != "all") || Runtime is not null))
        {
            return ValidationResult.Error("--split requires -a --target desktop|all and cannot be combined with --runtime.");
        }

        if ((!AllInOne && (Target is not null || Runtime is not null)) ||
            (Target is not null && Target is not ("web" or "desktop" or "all")) ||
            (Runtime is not null && (Target != "desktop" || !PublishCommand.DesktopRuntimes.Contains(Runtime))))
        {
            return ValidationResult.Error("Use --all-in-one with --target web|desktop|all; --runtime is only valid for desktop and must match a supported Photino RID.");
        }

        return ValidationResult.Success();
    }
}

public sealed class PublishCommand : Command<PublishSettings>
{
    private const string ComponentsVersion = "10.0.13";
    internal static readonly string[] DesktopRuntimes =
        ["linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"];

    public override int Execute(CommandContext context, PublishSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        return Run(settings, writers);
    }

    internal sealed record Operations(
        Func<string, string, string?, string, TextWriter, TextWriter, string> BuildHost,
        Action<string, string> CreateZip,
        Func<string, DestinationState> CaptureDestination,
        Action<string, string> MoveFile)
    {
        internal static Operations Default { get; } = new(
            PublishCommand.BuildHost, PublishCommand.CreateZip,
            DestinationState.Capture, File.Move);
        internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;
    }

    private static int Run(PublishSettings settings, CommandWriters writers)
    {
        var (output, error, _, publication) = writers;
        var root = settings.Root;
        var destination = settings.Destination!;
        var runtime = settings.Runtime;
        var target = settings.Target ?? "web";
        var allInOne = settings.AllInOne;
        var split = settings.Split;
        var force = settings.Force;
        var universalDesktop = allInOne && target == "desktop" && runtime is null;

        string final;
        DestinationState? approved = null;
        StorySource source;
        try
        {
            final = Path.GetFullPath(destination);
            if (split && File.Exists(final))
            {
                throw new IOException($"Output '{final}' is a file. Choose a directory for --output.");
            }
            if (target == "all" && File.Exists(final))
            {
                throw new IOException($"Output '{final}' is a file. Choose a directory for --target all.");
            }
            if (target != "all" && !split && Directory.Exists(final))
            {
                throw new IOException($"Output '{final}' is a directory. Choose a different --output ZIP path.");
            }
            if (target != "all" && !split && File.Exists(final) && !force)
            {
                if (!ConfirmOverwrite([final], settings, writers, null,
                    () => approved = DestinationState.Capture(final)))
                {
                    return 2;
                }
            }

            source = StorySource.FromDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"{CliDiagnosticCodes.IoFailure}: Publication failed: {ex.Message}");
            return 2;
        }

        var result = StoryCompiler.Compile(source);
        foreach (var diagnostic in result.Diagnostics)
        {
            output.WriteLine(diagnostic);
        }

        if (!result.Success)
        {
            error.WriteLine("Publication failed: the story has compiler errors.");
            return 1;
        }

        if (target == "all")
        {
            return PublishAll(final, result.Story!.Id, result.Story.Title, source, writers, settings, publication);
        }

        if (split)
        {
            return PublishSplit(final, result.Story!.Id, result.Story.Title, source, writers, settings, publication);
        }

        string? working = null;
        string? temporaryZip = null;
        try
        {
            working = Path.Combine(Path.GetTempPath(), $"talewright-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(working);
            var story = StageStory(source, root, working);
            var content = story;
            if (allInOne)
            {
                content = BuildStandalone(working, story, result.Story!.Title, result.Story.Id,
                    target, runtime, output, error, publication, universalDesktop);
            }

            if (allInOne && target == "web")
                WebHostingSize.FromDirectory(content).WriteWarning(error, estimate: false);

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            temporaryZip = Path.Combine(Path.GetDirectoryName(final)!, $".talewright-{Guid.NewGuid():N}.zip.tmp");
            publication.CreateZip(content, temporaryZip);
            if (approved is not null && approved != DestinationState.Capture(final))
            {
                throw new IOException($"Output '{final}' changed after approval. No archive was published. Retry to approve the current file, choose a new --output path, or use --force.");
            }
            // The check and move are separate operations, not a filesystem compare-and-swap.
            File.Move(temporaryZip, final, force || approved is not null);
            output.WriteLine($"Published {(allInOne ? target : "story")} archive: {final}");
            return 0;
        }
        catch (PublishBuildException ex)
        {
            error.WriteLine($"Publication failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            error.WriteLine($"{CliDiagnosticCodes.IoFailure}: Publication failed: {ex.Message}");
            return 2;
        }
        finally
        {
            if (temporaryZip is not null && File.Exists(temporaryZip))
            {
                File.Delete(temporaryZip);
            }

            if (working is not null && Directory.Exists(working))
            {
                Directory.Delete(working, recursive: true);
            }
        }
    }

    private static bool ConfirmOverwrite(IReadOnlyList<string> existing, PublishSettings settings,
        CommandWriters writers, string? group, Action? beforePrompt = null)
    {
        if (existing.Count == 0) return true;

        var (_, error, input, _) = writers;
        if (settings.NonInteractive || !input.IsInteractive)
        {
            throw new IOException(group is null
                ? $"Output '{existing[0]}' exists. Choose a new --output path or use --force to replace it explicitly."
                : $"{(group == "split" ? "Split" : "All-target")} output already exists: {string.Join(", ", existing)}. Choose a new --output directory or use --force to replace the full set explicitly.");
        }

        beforePrompt?.Invoke();
        if (group is null)
        {
            error.WriteLine($"Output '{existing[0]}' already exists.");
            error.Write("Replace it? [y/N] ");
        }
        else
        {
            error.WriteLine($"These {group} outputs already exist:");
            foreach (var path in existing) error.WriteLine($"  {path}");
            error.Write("Replace all existing outputs? [y/N] ");
        }
        error.Flush();
        var answer = input.Reader.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error.WriteLine(group switch
        {
            null => "Publication cancelled. No archive was published.",
            "split" => "Publication cancelled. No archives were published.",
            _ => "Publication cancelled. No target was published.",
        });
        return false;
    }

    private static int PublishAll(string destination, string storyId, string storyTitle, StorySource source, CommandWriters writers,
        PublishSettings settings, Operations publication)
    {
        var (output, error, _, _) = writers;
        var targets = new[] { Path.Combine(destination, "web"), Path.Combine(destination, "desktop") };
        var existing = targets
            .Where(path => Directory.Exists(path) || File.Exists(path))
            .ToArray();
        var approved = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            void CaptureApproval()
            {
                foreach (var path in targets)
                    approved[path] = existing.Contains(path, StringComparer.Ordinal) ? CaptureOutput(path) : null;
            }

            if (!settings.Force && !ConfirmOverwrite(existing, settings, writers, "all-target", CaptureApproval))
            {
                return 2;
            }
            if (!settings.Force && existing.Length == 0) CaptureApproval();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"{CliDiagnosticCodes.IoFailure}: Publication failed: {ex.Message}");
            return 2;
        }

        string? working = null;
        var staged = new List<(string Target, string Path)>();
        try
        {
            Directory.CreateDirectory(destination);
            working = Path.Combine(destination, $".talewright-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(working);
            var story = StageStory(source, settings.Root, working);

            var webWorking = Path.Combine(working, "web-output");
            Directory.CreateDirectory(webWorking);
            var webContent = BuildStandalone(working, story, storyTitle, storyId, "web", null,
                output, error, publication);
            CopyDirectory(webContent, webWorking);
            WebHostingSize.FromDirectory(webWorking).WriteWarning(error, estimate: false, target: "web");
            staged.Add(("web", webWorking));

            var desktopWorking = Path.Combine(working, "desktop-output");
            Directory.CreateDirectory(desktopWorking);
            if (settings.Split)
            {
                var stem = Uri.EscapeDataString(storyId);
                foreach (var runtime in DesktopRuntimes)
                {
                    var built = BuildSplitDesktop(working, runtime, story, storyTitle, storyId, output, error, publication);
                    publication.CreateZip(built, Path.Combine(desktopWorking, $"{stem}-{runtime}.zip"));
                }
            }
            else
            {
                var desktopContent = BuildStandalone(working, story, storyTitle, storyId, "desktop", null,
                    output, error, publication, universalDesktop: true);
                CopyDirectory(desktopContent, desktopWorking);
            }
            staged.Add(("desktop", desktopWorking));

            InstallOutputs(staged.Select(item => (item.Path, Final: Path.Combine(destination, item.Target))).ToArray(),
                approved, settings.Force, CaptureOutput, allowDirectories: true, publication, error);

            foreach (var (target, _) in staged)
            {
                output.WriteLine($"Published {target} target: {Path.Combine(destination, target)}");
            }
            return 0;
        }
        catch (PublishBuildException ex)
        {
            error.WriteLine($"Publication failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            error.WriteLine($"{CliDiagnosticCodes.IoFailure}: Publication failed: {ex.Message}");
            return 2;
        }
        finally
        {
            if (working is not null && Directory.Exists(working))
            {
                Directory.Delete(working, recursive: true);
            }
        }
    }

    private static int PublishSplit(string destination, string storyId, string storyTitle, StorySource source, CommandWriters writers,
        PublishSettings settings, Operations publication)
    {
        var (output, error, _, _) = writers;
        string? working = null;
        var staged = new List<(string Runtime, string Path, string Final)>();

        try
        {
            Directory.CreateDirectory(destination);
            var stem = Uri.EscapeDataString(storyId);
            var targets = DesktopRuntimes.ToDictionary(
                runtime => runtime,
                runtime => Path.Combine(destination, $"{stem}-{runtime}.zip"),
                StringComparer.Ordinal);
            var approved = new Dictionary<string, DestinationState?>(StringComparer.Ordinal);
            foreach (var (runtime, path) in targets)
            {
                if (Directory.Exists(path))
                {
                    throw new IOException($"Output '{path}' is a directory. No archives were published.");
                }

                approved[path] = !settings.Force && File.Exists(path)
                    ? publication.CaptureDestination(path)
                    : null;
            }

            var existing = approved.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray();
            if (!settings.Force && !ConfirmOverwrite(existing, settings, writers, "split"))
            {
                return 2;
            }

            working = Path.Combine(Path.GetTempPath(), $"talewright-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(working);
            var story = StageStory(source, settings.Root, working);

            foreach (var runtime in DesktopRuntimes)
            {
                var built = BuildSplitDesktop(working, runtime, story, storyTitle, storyId, output, error, publication);
                var temporary = Path.Combine(destination, $".talewright-{Guid.NewGuid():N}.zip.tmp");
                staged.Add((runtime, temporary, targets[runtime]));
                publication.CreateZip(built, temporary);
            }

            InstallOutputs(staged.Select(item => (item.Path, item.Final)).ToArray(), approved, settings.Force,
                path => File.Exists(path) ? publication.CaptureDestination(path) : null,
                allowDirectories: false, publication, error);

            foreach (var (_, _, final) in staged)
            {
                output.WriteLine($"Published desktop runtime archive: {final}");
            }
            return 0;
        }
        catch (PublishBuildException ex)
        {
            error.WriteLine($"Publication failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            error.WriteLine($"{CliDiagnosticCodes.IoFailure}: Publication failed: {ex.Message}");
            return 2;
        }
        finally
        {
            foreach (var (_, temporary, _) in staged)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            if (working is not null && Directory.Exists(working))
            {
                Directory.Delete(working, recursive: true);
            }
        }
    }

    private static void InstallOutputs<T>(IReadOnlyList<(string Path, string Final)> staged,
        IReadOnlyDictionary<string, T?> approved, bool force, Func<string, T?> capture,
        bool allowDirectories, Operations publication, TextWriter error) where T : class
    {
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var installed = new Dictionary<string, T?>(StringComparer.Ordinal);
        var kind = allowDirectories ? "All-target" : "Split";
        var outputs = allowDirectories ? "targets" : "archives";

        void CheckApproval(string path)
        {
            if (!Equals(capture(path), approved[path]))
                throw new IOException($"Output '{path}' changed after approval. No {outputs} were published. Retry to approve the current files, choose a new --output directory, or use --force.");
        }

        bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        void Move(string from, string to)
        {
            if (allowDirectories && Directory.Exists(from)) publication.MoveDirectory(from, to);
            else publication.MoveFile(from, to);
        }
        void Delete(string path)
        {
            if (allowDirectories && Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }

        if (!force)
        {
            foreach (var (_, final) in staged) CheckApproval(final);
        }

        try
        {
            foreach (var (_, final) in staged)
            {
                if (!allowDirectories && Directory.Exists(final))
                    throw new IOException($"Output '{final}' became a directory. No archives were published.");
                if (!force) CheckApproval(final);
                if (Exists(final))
                {
                    var backup = Path.Combine(Path.GetDirectoryName(final)!, $".talewright-{Guid.NewGuid():N}.bak");
                    Move(final, backup);
                    backups.Add(final, backup);
                }
            }

            foreach (var (temporary, final) in staged)
            {
                Move(temporary, final);
                installed.Add(final, null);
                installed[final] = capture(final);
            }
        }
        catch (Exception commitError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var (path, state) in installed.Reverse())
            {
                try
                {
                    if (Exists(path))
                    {
                        if ((!allowDirectories && Directory.Exists(path)) || state is null || !Equals(capture(path), state))
                            throw new IOException($"Output '{path}' changed during rollback and was left untouched.");
                        Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }

            foreach (var (final, backup) in backups.Reverse())
            {
                try
                {
                    Move(backup, final);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(ex);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                var recovery = string.Join(", ", backups.Values.Where(Exists).Concat(installed.Keys.Where(Exists)));
                throw new IOException(
                    $"{kind} publication failed and rollback was incomplete. Preserve recovery files: {recovery}.",
                    new AggregateException([commitError, .. rollbackErrors]));
            }
            throw;
        }

        foreach (var backup in backups.Values)
        {
            try
            {
                Delete(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Warning: {kind.ToLowerInvariant()} outputs were published, but backup '{backup}' could not be removed: {ex.Message}");
            }
        }
    }

    private static string? CaptureOutput(string path)
    {
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path)) return null;

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(isFile);
            if (isFile) CaptureFile(path);
            else CaptureDirectory(path, "");

            void CaptureFile(string file)
            {
                var state = DestinationState.Capture(file);
                writer.Write(state.Length);
                writer.Write(state.LastWriteTimeUtc.Ticks);
                writer.Write(state.CreationTimeUtc.Ticks);
                writer.Write(state.LinkTarget ?? "");
                writer.Write(state.Hash);
            }

            void CaptureDirectory(string directory, string relative)
            {
                var info = new DirectoryInfo(directory);
                var modified = info.LastWriteTimeUtc;
                var created = info.CreationTimeUtc;
                writer.Write(relative);
                writer.Write(modified.Ticks);
                writer.Write(created.Ticks);
                writer.Write(info.LinkTarget ?? "");
                if (info.LinkTarget is not null) return;

                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(entry);
                    var child = Path.Combine(relative, name);
                    var attributes = File.GetAttributes(entry);
                    writer.Write((int)attributes);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        CaptureDirectory(entry, child);
                    }
                    else if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        writer.Write(child);
                        writer.Write(new FileInfo(entry).LinkTarget ?? "");
                    }
                    else
                    {
                        writer.Write(child);
                        CaptureFile(entry);
                    }
                }

                info.Refresh();
                if (!info.Exists || info.LastWriteTimeUtc != modified || info.CreationTimeUtc != created)
                    throw new IOException($"Output '{directory}' changed while checking it; retry or use --force.");
            }
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    internal sealed record DestinationState(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc,
        string? LinkTarget, string Hash)
    {
        internal static DestinationState Capture(string path)
        {
            var before = new FileInfo(path);
            if (!before.Exists)
            {
                throw new IOException($"Output '{path}' disappeared while publishing. No archive was published; retry or choose a new --output path.");
            }
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            var created = before.CreationTimeUtc;
            var link = before.LinkTarget;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified ||
                after.CreationTimeUtc != created || after.LinkTarget != link)
            {
                throw new IOException($"Output '{path}' changed while checking it. No archive was published; retry or use --force.");
            }
            return new(length, modified, created, link, hash);
        }
    }

    private static string AssetsRoot => Path.Combine(Path.GetDirectoryName(typeof(PublishCommand).Assembly.Location)!, "publish-assets");

    private static string DeploymentInstructions(string target, string? runtime, string? storyTitle = null)
    {
        if (target == "web")
        {
            return "Extract and serve this directory with a static HTTP(S) host, or upload the ZIP as an itch.io HTML5 game. No .NET runtime or backend server is required. Saves use this origin's IndexedDB.";
        }

        var launcherName = string.IsNullOrWhiteSpace(storyTitle)
            ? "the story-named launcher"
            : $"'{SafeDesktopName(storyTitle, "Story")}'";
        var launch = $"Universal self-contained desktop package for Windows, Linux and macOS, x64 and ARM64. Extract the entire ZIP; use {launcherName}.cmd on Windows, {launcherName}.sh on Linux or {launcherName}.command on macOS. Launchers select the matching platform directory.";
        if (runtime is not null)
        {
            var name = SafeDesktopName(storyTitle ?? "Story", "Story");
            var launcher = runtime.StartsWith("win-", StringComparison.Ordinal)
                ? name + ".cmd"
                : runtime.StartsWith("osx-", StringComparison.Ordinal)
                    ? name + ".command"
                    : name + ".sh";
            launch = $"Self-contained {runtime} desktop build. Start {launcher}.";
        }

        return launch + " The .NET runtime is included. Platform webview prerequisite: WebKitGTK 4.1 on Linux, WebView2 on Windows, built-in WKWebView on macOS. Linux x64 is automated here; other OS/CPU combinations require runtime verification. This ZIP is not a signed installer or macOS .app bundle.";
    }

    private static string StageStory(StorySource source, string root, string working)
    {
        var story = Path.Combine(working, "story");
        WriteStory(source, root, story);
        _ = StorySource.FromDirectory(story);
        return story;
    }

    private static void WriteStory(StorySource source, string root, string directory)
    {
        foreach (var (path, text) in source.Files)
        {
            var destination = Destination(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text);
        }

        foreach (var path in StoryImageResolver.CompletionPaths(source))
        {
            var relative = "images/" + path;
            var destination = Destination(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(root, relative), destination);
        }

        string Destination(string path)
        {
            var destination = Path.GetFullPath(Path.Combine(directory, path));
            if (!destination.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe story path '{path}'.");
            }

            return destination;
        }
    }

    private static string BuildStandalone(string working, string story, string storyTitle, string storyId,
        string target, string? runtime, TextWriter output, TextWriter error, Operations publication,
        bool universalDesktop = false)
    {
        var content = universalDesktop
            ? BuildUniversalDesktop(working, story, storyTitle, storyId, output, error, publication)
            : publication.BuildHost(working, target, runtime, story, output, error);
        if (!universalDesktop)
        {
            if (target == "desktop")
                PrepareDesktopRuntime(content, runtime!, story, storyTitle, storyId);
            else
                StageExternalStory(story, content, target);
        }
        AddDeploymentFiles(content, target, runtime, storyTitle);
        return content;
    }

    private static string BuildSplitDesktop(string working, string runtime, string story, string storyTitle,
        string storyId, TextWriter output, TextWriter error, Operations publication)
    {
        var content = BuildDesktopRuntime(working, runtime, story, output, error, publication);
        PrepareDesktopRuntime(content, runtime, story, storyTitle, storyId);
        AddDeploymentFiles(content, "desktop", runtime, storyTitle);
        return content;
    }

    private static void PrepareDesktopRuntime(string content, string runtime, string story, string storyTitle, string storyId)
    {
        MoveDesktopRuntimeToInternal(content, runtime);
        var internalStory = Path.Combine(content, "internal", "story");
        Directory.CreateDirectory(internalStory);
        CopyDirectory(story, internalStory);
        CreateSplitLauncher(content, storyTitle, storyId, runtime);
    }

    private static void AddDeploymentFiles(string content, string target, string? runtime, string storyTitle)
    {
        File.Copy(Path.Combine(AssetsRoot, "ENGINE-LICENSE.txt"), Path.Combine(content, "ENGINE-LICENSE.txt"));
        File.WriteAllText(Path.Combine(content, "DEPLOYMENT.txt"), DeploymentInstructions(target, runtime, storyTitle));
    }

    private static string BuildUniversalDesktop(string working, string story, string storyTitle, string storyId,
        TextWriter output, TextWriter error,
        Operations publication)
    {
        var bundle = Path.Combine(working, "bundle");
        Directory.CreateDirectory(bundle);
        var internalDirectory = Path.Combine(bundle, "internal");
        Directory.CreateDirectory(internalDirectory);
        foreach (var runtime in DesktopRuntimes)
        {
            var built = BuildDesktopRuntime(working, runtime, story, output, error, publication);
            Directory.Move(built, Path.Combine(internalDirectory, runtime));
        }

        CopyDirectory(story, Path.Combine(bundle, "story"));
        CreateUniversalLaunchers(bundle, storyTitle, storyId);
        return bundle;
    }

    private static string BuildDesktopRuntime(string working, string runtime, string story, TextWriter output,
        TextWriter error, Operations publication)
    {
        output.WriteLine($"Building desktop runtime: {runtime}");
        return publication.BuildHost(Path.Combine(working, runtime), "desktop", runtime, story, output, error);
    }

    private static string BuildHost(string working, string target, string? runtime, string story, TextWriter output, TextWriter error)
    {
        var host = Path.Combine(working, "host");
        CopyDirectory(Path.Combine(AssetsRoot, target), host);
        var project = Directory.GetFiles(host, "*.csproj").Single();
        var xml = XDocument.Load(project);
        xml.Descendants("ProjectReference").Remove();
        xml.Descendants("EmbeddedResource").Remove();
        var binaries = Path.GetDirectoryName(typeof(PublishCommand).Assembly.Location)!;
        var references = new XElement("ItemGroup");
        foreach (var name in new[] { "Talewright.Core", "Talewright.Graphs", "Talewright.Rules", "Talewright.Narrative",
            "Talewright.Rulesets.Generic", "Talewright.Talemark", "Talewright.Runtime", "Talewright.Player",
            "MessagePack", "MessagePack.Annotations", "Microsoft.NET.StringTools", "YamlDotNet" })
        {
            var path = Path.Combine(binaries, name + ".dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The installed CLI is missing publishing assembly '{name}'.", path);
            }

            references.Add(new XElement("Reference", new XAttribute("Include", name), new XElement("HintPath", path)));
        }

        references.Add(new XElement("PackageReference", new XAttribute("Include", "Microsoft.AspNetCore.Components.Web"),
            new XAttribute("Version", ComponentsVersion)));
        xml.Root!.Add(references);
        xml.Root.Add(new XElement("PropertyGroup", new XElement("TreatWarningsAsErrors", "true"),
            new XElement("LangVersion", "latest"), new XElement("JSModulesEnabled", "false")));
        xml.Save(project);
        CopyDirectory(Path.Combine(AssetsRoot, "player"), Path.Combine(host, "wwwroot", "_content", "Talewright.Player"));

        CopyDirectory(Path.Combine(AssetsRoot, "standalone", target), host, overwrite: true);

        var published = Path.Combine(working, "output");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = host };
        foreach (var argument in new[] { "publish", project, "-c", "Release", "-o", published, "-p:TalewrightIncludeSamples=false" })
        {
            start.ArgumentList.Add(argument);
        }

        if (target == "web")
        {
            var identity = PlayerBuildIdentity.Current;
            start.ArgumentList.Add("-p:TalewrightBuildIdentityTargets=BuildIdentity.targets");
            start.ArgumentList.Add($"-p:TalewrightBuildVersion={identity.Version}");
            start.ArgumentList.Add($"-p:TalewrightBuildCommit={identity.Commit}");
            start.ArgumentList.Add($"-p:TalewrightBuildDate={identity.Date}");
        }

        if (runtime is not null)
        {
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add(runtime);
            start.ArgumentList.Add("--self-contained");
            start.ArgumentList.Add("true");
        }

        using var process = Process.Start(start) ?? throw new PublishBuildException("Could not start the .NET SDK.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        output.Write(stdout.GetAwaiter().GetResult());
        error.Write(stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
        {
            throw new PublishBuildException($"The Release host build exited with code {process.ExitCode}.");
        }

        return target == "web" ? Path.Combine(published, "wwwroot") : published;
    }

    private static void StageExternalStory(string story, string content, string target)
    {
        var externalStory = Path.Combine(content, "story");
        CopyDirectory(story, externalStory);
        if (target == "web")
        {
            var index = Directory.EnumerateFiles(externalStory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToDictionary(file => Path.GetRelativePath(externalStory, file).Replace('\\', '/'), file =>
                {
                    using var stream = File.OpenRead(file);
                    return Convert.ToHexString(SHA256.HashData(stream));
                }, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(externalStory, "index.json"), System.Text.Json.JsonSerializer.Serialize(index));
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    internal static string SafeDesktopName(string title, string fallback)
    {
        var name = title.Trim();
        foreach (var character in name)
        {
            if (character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ||
                character is '&' or ';' or '\'' or '`' or '$' or '%' or '!' or '#' or '(' or ')' ||
                char.IsControl(character))
            {
                name = name.Replace(character.ToString(), "-", StringComparison.Ordinal);
            }
        }

        name = name.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            name = fallback.Trim();

        if (name.Length > 100)
            name = name[..100].TrimEnd('.', ' ');

        var upper = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" ||
            (upper.Length == 4 && upper[3] >= '1' && upper[3] <= '9' &&
             upper.StartsWith("COM", StringComparison.Ordinal)) ||
            (upper.Length == 4 && upper[3] >= '1' && upper[3] <= '9' &&
             upper.StartsWith("LPT", StringComparison.Ordinal)))
        {
            name = "_" + name;
        }

        return name;
    }

    private static void MoveDesktopRuntimeToInternal(string content, string runtime)
    {
        var runtimeDirectory = Path.Combine(content, "internal", runtime);
        Directory.CreateDirectory(runtimeDirectory);
        foreach (var file in Directory.EnumerateFiles(content, "*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(runtimeDirectory, Path.GetFileName(file));
            Directory.Move(file, target);
        }
        foreach (var directory in Directory.EnumerateDirectories(content, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(directory) == "internal")
                continue;
            Directory.Move(directory, Path.Combine(runtimeDirectory, Path.GetFileName(directory)));
        }
    }

    private static void CreateSplitLauncher(string content, string storyTitle, string storyId, string? runtime)
    {
        var name = SafeDesktopName(storyTitle, storyId);
        if (runtime?.StartsWith("win-", StringComparison.Ordinal) == true)
        {
            File.WriteAllText(Path.Combine(content, name + ".cmd"),
                "@echo off\r\nsetlocal\r\n\"%~dp0internal\\" + runtime + "\\Talewright.Host.Desktop.exe\" --no-library \"%~dp0internal\\story\" %*\r\nexit /b %errorlevel%\r\n");
        }
        else if (runtime?.StartsWith("osx-", StringComparison.Ordinal) == true)
        {
            File.WriteAllText(Path.Combine(content, name + ".command"),
                "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\nexec \"$base/internal/" + runtime + "/Talewright.Host.Desktop\" --no-library \"$base/internal/story\" \"$@\"\n");
        }
        else
        {
            File.WriteAllText(Path.Combine(content, name + ".sh"),
                "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\nexec \"$base/internal/" + runtime + "/Talewright.Host.Desktop\" --no-library \"$base/internal/story\" \"$@\"\n");
        }
    }

    private static void CreateUniversalLaunchers(string bundle, string storyTitle, string storyId)
    {
        var name = SafeDesktopName(storyTitle, storyId);
        File.WriteAllText(Path.Combine(bundle, name + ".cmd"),
            "@echo off\r\nsetlocal\r\nset \"RID=\"\r\nset \"ARCH=%PROCESSOR_ARCHITECTURE%\"\r\nif defined PROCESSOR_ARCHITEW6432 set \"ARCH=%PROCESSOR_ARCHITEW6432%\"\r\nif /i \"%ARCH%\"==\"AMD64\" set \"RID=win-x64\"\r\nif /i \"%ARCH%\"==\"ARM64\" set \"RID=win-arm64\"\r\nif not defined RID (echo Unsupported Windows architecture. 1>&2 & exit /b 1)\r\n\"%~dp0internal\\%RID%\\Talewright.Host.Desktop.exe\" --no-library \"%~dp0story\" %*\r\nexit /b %errorlevel%\r\n");
        File.WriteAllText(Path.Combine(bundle, name + ".sh"),
            "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\ncase \"$(uname -s):$(uname -m)\" in\n  Linux:x86_64) rid=linux-x64 ;; Linux:aarch64|Linux:arm64) rid=linux-arm64 ;; Darwin:x86_64) rid=osx-x64 ;; Darwin:arm64) rid=osx-arm64 ;; *) printf '%s\\n' 'Unsupported platform.' >&2; exit 1 ;; esac\nexec \"$base/internal/$rid/Talewright.Host.Desktop\" --no-library \"$base/story\" \"$@\"\n");
        File.WriteAllText(Path.Combine(bundle, name + ".command"),
            "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\nexec sh \"$base/" + name + ".sh\" \"$@\"\n");
        CopyDirectory(Path.Combine(AssetsRoot, "launchers"), bundle);
        File.WriteAllText(Path.Combine(bundle, "start.sh"),
            "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\nexec sh \"$base/" + name + ".sh\" \"$@\"\n");
        File.WriteAllText(Path.Combine(bundle, "start.command"),
            "#!/bin/sh\nset -eu\nbase=$(CDPATH= cd -P \"$(dirname \"$0\")\" && pwd)\nexec sh \"$base/" + name + ".sh\" \"$@\"\n");
        File.WriteAllText(Path.Combine(bundle, "start.cmd"),
            "@echo off\r\nsetlocal\r\ncall \"%~dp0" + name + ".cmd\" %*\r\nexit /b %errorlevel%\r\n");
    }

    private static void CreateZip(string directory, string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var entry = zip.CreateEntryFromFile(file, Path.GetRelativePath(directory, file).Replace('\\', '/'));
            var mode = OperatingSystem.IsWindows()
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : File.GetUnixFileMode(file);
            var fileName = Path.GetFileName(file);
            if (fileName is "Talewright.Host.Desktop" or "start.sh" or "start.command" ||
                fileName.EndsWith(".sh", StringComparison.Ordinal) ||
                fileName.EndsWith(".command", StringComparison.Ordinal))
            {
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            }

            entry.ExternalAttributes = ((int)mode | 0x8000) << 16;
        }
    }

    internal sealed class PublishBuildException(string message) : Exception(message);
}
