using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
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
        if (Destination is null || (!Split && !Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationResult.Error(Split
                ? "--split requires --output <directory>."
                : "--output <file.zip> is required.");
        }

        if (Split && Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Error("--split uses --output as a directory, not a ZIP path.");
        }

        if (Split && (!AllInOne || Target != "desktop" || Runtime is not null))
        {
            return ValidationResult.Error("--split requires -a --target desktop and cannot be combined with --runtime.");
        }

        if ((!AllInOne && (Target is not null || Runtime is not null)) ||
            (Target is not null && Target is not ("web" or "desktop")) ||
            (Runtime is not null && (Target != "desktop" || !PublishCommand.DesktopRuntimes.Contains(Runtime))))
        {
            return ValidationResult.Error("Use --all-in-one with --target web|desktop; --runtime is only valid for desktop and must match a supported Photino RID.");
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
    }

    private static int Run(PublishSettings settings, CommandWriters writers)
    {
        var (output, error, input, publication) = writers;
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
            if (!split && Directory.Exists(final))
            {
                throw new IOException($"Output '{final}' is a directory. Choose a different --output ZIP path.");
            }
            if (!split && File.Exists(final) && !force)
            {
                if (settings.NonInteractive || !input.IsInteractive)
                {
                    throw new IOException($"Output '{final}' exists. Choose a new --output path or use --force to replace it explicitly.");
                }

                approved = DestinationState.Capture(final);
                error.WriteLine($"Output '{final}' already exists.");
                error.Write("Replace it? [y/N] ");
                error.Flush();
                var answer = input.Reader.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine("Publication cancelled. No archive was published.");
                    return 2;
                }
            }

            source = StorySource.FromDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"TWCLI002: Publication failed: {ex.Message}");
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

        if (split)
        {
            return PublishSplit(final, result.Story!.Id, source, writers, settings, publication);
        }

        string? working = null;
        string? temporaryZip = null;
        try
        {
            working = Path.Combine(Path.GetTempPath(), $"talewright-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(working);
            var story = Path.Combine(working, "story");
            WriteStory(source, root, story);
            _ = StorySource.FromDirectory(story);
            var content = story;
            if (allInOne)
            {
                content = universalDesktop
                    ? BuildUniversalDesktop(working, story, output, error, publication)
                    : publication.BuildHost(working, target, runtime, story, output, error);
                if (!universalDesktop) StageExternalStory(story, content, target);
                File.Copy(Path.Combine(AssetsRoot, "ENGINE-LICENSE.txt"), Path.Combine(content, "ENGINE-LICENSE.txt"));
                File.WriteAllText(Path.Combine(content, "DEPLOYMENT.txt"), DeploymentInstructions(target, runtime));
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
            error.WriteLine($"TWCLI002: Publication failed: {ex.Message}");
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

    private static int PublishSplit(string destination, string storyId, StorySource source, CommandWriters writers,
        PublishSettings settings, Operations publication)
    {
        var (output, error, input, _) = writers;
        string? working = null;
        var staged = new List<(string Runtime, string Path, string Final)>();
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var installed = new Dictionary<string, DestinationState?>(StringComparer.Ordinal);

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
            if (existing.Length > 0 && !settings.Force)
            {
                if (settings.NonInteractive || !input.IsInteractive)
                {
                    throw new IOException($"Split output already exists: {string.Join(", ", existing)}. Choose a new --output directory or use --force to replace the full set explicitly.");
                }

                error.WriteLine("These split outputs already exist:");
                foreach (var path in existing)
                {
                    error.WriteLine($"  {path}");
                }

                error.Write("Replace all existing outputs? [y/N] ");
                error.Flush();
                var answer = input.Reader.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine("Publication cancelled. No archives were published.");
                    return 2;
                }
            }

            working = Path.Combine(Path.GetTempPath(), $"talewright-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(working);
            var story = Path.Combine(working, "story");
            WriteStory(source, settings.Root, story);
            _ = StorySource.FromDirectory(story);

            foreach (var runtime in DesktopRuntimes)
            {
                var built = BuildDesktopRuntime(working, runtime, story, output, error, publication);
                StageExternalStory(story, built, "desktop");
                File.Copy(Path.Combine(AssetsRoot, "ENGINE-LICENSE.txt"), Path.Combine(built, "ENGINE-LICENSE.txt"));
                File.WriteAllText(Path.Combine(built, "DEPLOYMENT.txt"), DeploymentInstructions("desktop", runtime));
                var temporary = Path.Combine(destination, $".talewright-{Guid.NewGuid():N}.zip.tmp");
                staged.Add((runtime, temporary, targets[runtime]));
                publication.CreateZip(built, temporary);
            }

            if (!settings.Force)
            {
                foreach (var (path, state) in approved)
                {
                    var current = File.Exists(path) ? publication.CaptureDestination(path) : null;
                    if (current != state)
                    {
                        throw new IOException($"Output '{path}' changed after approval. No archives were published. Retry to approve the current files, choose a new --output directory, or use --force.");
                    }
                }
            }

            try
            {
                foreach (var (_, _, final) in staged)
                {
                    if (Directory.Exists(final))
                    {
                        throw new IOException($"Output '{final}' became a directory. No archives were published.");
                    }

                    if (!settings.Force)
                    {
                        var current = File.Exists(final) ? publication.CaptureDestination(final) : null;
                        if (current != approved[final])
                        {
                            throw new IOException($"Output '{final}' changed after approval. No archives were published. Retry to approve the current files, choose a new --output directory, or use --force.");
                        }
                    }

                    if (File.Exists(final))
                    {
                        var backup = Path.Combine(destination, $".talewright-{Guid.NewGuid():N}.bak");
                        publication.MoveFile(final, backup);
                        backups.Add(final, backup);
                    }
                }

                foreach (var (_, temporary, final) in staged)
                {
                    publication.MoveFile(temporary, final);
                    installed.Add(final, null);
                    installed[final] = publication.CaptureDestination(final);
                }
            }
            catch (Exception commitError)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var (path, state) in installed.Reverse())
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            if (state is null || publication.CaptureDestination(path) != state)
                            {
                                throw new IOException($"Output '{path}' changed during rollback and was left untouched.");
                            }

                            File.Delete(path);
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
                        publication.MoveFile(backup, final);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        rollbackErrors.Add(ex);
                    }
                }

                if (rollbackErrors.Count > 0)
                {
                    var recovery = string.Join(", ", backups.Values.Where(File.Exists)
                        .Concat(installed.Keys.Where(File.Exists)));
                    throw new IOException(
                        $"Split publication failed and rollback was incomplete. Preserve recovery files: {recovery}.",
                        new AggregateException([commitError, .. rollbackErrors]));
                }

                throw;
            }

            foreach (var backup in backups.Values)
            {
                try
                {
                    File.Delete(backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"Warning: all split archives were published, but backup '{backup}' could not be removed: {ex.Message}");
                }
            }

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
            error.WriteLine($"TWCLI002: Publication failed: {ex.Message}");
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

    private static string DeploymentInstructions(string target, string? runtime)
    {
        if (target == "web")
        {
            return "Extract and serve this directory with a static HTTP(S) host, or upload the ZIP as an itch.io HTML5 game. No .NET runtime or backend server is required. Saves use this origin's IndexedDB.";
        }

        var launch = "Universal self-contained desktop package for Windows, Linux and macOS, x64 and ARM64. Extract the entire ZIP; start start.cmd on Windows, start.sh on Linux or start.command on macOS. Launchers select the matching platform directory. If executable permissions were lost during extraction, use sh start.sh on Linux/macOS.";
        if (runtime is not null)
        {
            var executable = runtime.StartsWith("win-", StringComparison.Ordinal)
                ? "Talewright.Host.Desktop.exe" : "Talewright.Host.Desktop";
            launch = $"Self-contained {runtime} desktop build. Start {executable}.";
        }

        return launch + " The .NET runtime is included. Platform webview prerequisite: WebKitGTK 4.1 on Linux, WebView2 on Windows, built-in WKWebView on macOS. Linux x64 is automated here; other OS/CPU combinations require runtime verification. This ZIP is not a signed installer or macOS .app bundle.";
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

    private static string BuildUniversalDesktop(string working, string story, TextWriter output, TextWriter error,
        Operations publication)
    {
        var bundle = Path.Combine(working, "bundle");
        Directory.CreateDirectory(bundle);
        foreach (var runtime in DesktopRuntimes)
        {
            var built = BuildDesktopRuntime(working, runtime, story, output, error, publication);
            Directory.Move(built, Path.Combine(bundle, runtime));
        }

        CopyDirectory(story, Path.Combine(bundle, "story"));
        CopyDirectory(Path.Combine(AssetsRoot, "launchers"), bundle);
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
        foreach (var name in new[] { "Talewright.Core", "Talewright.Rules", "Talewright.Narrative",
            "Talewright.Rulesets.Generic", "Talewright.Talemark", "Talewright.Runtime", "Talewright.Player", "Markdig", "YamlDotNet" })
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

    private static void CreateZip(string directory, string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var entry = zip.CreateEntryFromFile(file, Path.GetRelativePath(directory, file).Replace('\\', '/'));
            var mode = OperatingSystem.IsWindows()
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : File.GetUnixFileMode(file);
            if (Path.GetFileName(file) is "Talewright.Host.Desktop" or "start.sh" or "start.command")
            {
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            }

            entry.ExternalAttributes = ((int)mode | 0x8000) << 16;
        }
    }

    internal sealed class PublishBuildException(string message) : Exception(message);
}
