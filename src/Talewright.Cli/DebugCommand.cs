using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console.Cli;

namespace Talewright.Cli;

public sealed class DebugSettings : CommandSettings
{
    [CommandArgument(0, "<story-root>")]
    public string Root { get; set; } = "";
}

public sealed class DebugCommand : Command<DebugSettings>
{
    public override int Execute(CommandContext context, DebugSettings settings, CancellationToken cancellationToken)
    {
        var writers = (CommandWriters)context.Data!;
        try
        {
            var root = Path.GetFullPath(settings.Root);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Story folder '{root}' does not exist.");
            }
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            {
                throw new NotSupportedException($"Author Debugger does not support runtime '{RuntimeInformation.RuntimeIdentifier}'. Use Windows, Linux or macOS on x64/ARM64.");
            }
            if (OperatingSystem.IsLinux())
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                {
                    throw new NotSupportedException("Author Debugger requires a graphical desktop (DISPLAY), not a headless terminal.");
                }
                if (!NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var handle))
                {
                    throw new NotSupportedException("Install WebKitGTK 4.1 (libwebkit2gtk-4.1-0) before launching Author Debugger.");
                }
                NativeLibrary.Free(handle);
            }
            var binaries = Path.GetDirectoryName(typeof(DebugCommand).Assembly.Location)!;
            foreach (var file in new[] { "Talewright.Debugger.dll", "Talewright.Cli.runtimeconfig.json",
                "Talewright.Cli.deps.json", "debug-assets/index.html", "debug-assets/debugger.css",
                "debug-assets/_content/Talewright.Player/talewright.css" })
            {
                if (!File.Exists(Path.Combine(binaries, file)))
                {
                    throw new FileNotFoundException($"Installed debugger asset '{file}' is missing. Reinstall the Talewright CLI.");
                }
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = binaries, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(binaries, "Talewright.Cli.runtimeconfig.json"),
                "--depsfile", Path.Combine(binaries, "Talewright.Cli.deps.json"), Path.Combine(binaries, "Talewright.Debugger.dll"), root })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not start the Author Debugger.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                throw;
            }
            writers.Output.Write(stdout.GetAwaiter().GetResult());
            writers.Error.Write(stderr.GetAwaiter().GetResult());
            if (process.ExitCode != 0)
            {
                throw new IOException($"Author Debugger exited with code {process.ExitCode}. Check WebKitGTK 4.1 / WebView2 / WKWebView and the launch errors above.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return CommandLine.Failure("TWCLI002", $"Debugger launch failed: {ex.Message}", false, writers.Output, writers.Error);
        }
    }
}
