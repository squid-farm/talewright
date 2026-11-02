using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Talewright.Cli;

internal enum NativeHostMode
{
    Debugger,
    Player,
}

internal sealed record NativeHostEnvironment(
    bool IsLinux, bool IsWindows, bool IsMacOS, Architecture Architecture, string RuntimeIdentifier,
    string BinariesDirectory, Func<string, string?> GetEnvironmentVariable, Func<bool> HasWebKitGtk)
{
    public static NativeHostEnvironment Current() => new(
        OperatingSystem.IsLinux(), OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.RuntimeIdentifier,
        Path.GetDirectoryName(typeof(NativeHostLauncher).Assembly.Location)
            ?? throw new InvalidOperationException("The CLI assembly directory is unavailable."),
        Environment.GetEnvironmentVariable, WebKitGtkAvailable);

    private static bool WebKitGtkAvailable()
    {
        if (!NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var handle))
        {
            return false;
        }
        NativeLibrary.Free(handle);
        return true;
    }
}

internal static class NativeHostLauncher
{
    internal sealed record Operations(
        Func<NativeHostMode, string, TextWriter, TextWriter, CancellationToken, int> Launch)
    {
        public static Operations Default { get; } = new(
            (mode, root, output, error, cancellationToken) =>
                NativeHostLauncher.Launch(mode, root, output, error, cancellationToken));
    }

    private sealed record HostDetails(string Name, string AssetName, string[] Assets, string[] BundledAssets);

    private static readonly HostDetails Debugger = new("Author Debugger", "debugger",
    [
        "debug-assets/index.html",
        "debug-assets/debugger.css",
        "debug-assets/_content/Talewright.Player/talewright.css",
    ],
    [
        "debug-assets/index.html",
        "debug-assets/debugger.css",
        "debug-assets/_content/Talewright.Player/talewright.css",
    ]);

    private static readonly HostDetails Player = new("Talewright Player", "player",
    [
        "player-assets/player.html",
        "publish-assets/player/talewright.css",
        "publish-assets/player/Preferences/PlayerPreferencesBootstrap.js",
        "publish-assets/player/Preferences/PlayerPreferences.js",
        "publish-assets/player/Components/MediaView.razor.js",
        "publish-assets/player/Components/PlayerToolbar.razor.js",
        "publish-assets/player/Components/SavedGamesPanel.razor.js",
        "publish-assets/player/Components/StoryPlayer.razor.js",
        "publish-assets/player/Saves/IndexedDbSaveStore.js",
    ],
    [
        "player-assets/player.html",
    ]);

    internal static bool HasGraphicalDisplay(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("DISPLAY")) || !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"));

    internal static int Launch(NativeHostMode mode, string root, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, NativeHostEnvironment? environment = null)
    {
        var host = mode switch
        {
            NativeHostMode.Debugger => Debugger,
            NativeHostMode.Player => Player,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var platform = environment ?? NativeHostEnvironment.Current();
        if (!(platform.IsLinux || platform.IsWindows || platform.IsMacOS)
            || platform.Architecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new NotSupportedException($"{host.Name} does not support runtime '{platform.RuntimeIdentifier}'. Use Windows, Linux or macOS on x64/ARM64.");
        }
        if (platform.IsLinux)
        {
            if (!HasGraphicalDisplay(platform.GetEnvironmentVariable))
            {
                throw new NotSupportedException($"{host.Name} requires a graphical desktop (X11 DISPLAY or Wayland WAYLAND_DISPLAY), not a headless terminal.");
            }
            if (!platform.HasWebKitGtk())
            {
                throw new NotSupportedException($"Install WebKitGTK 4.1 (libwebkit2gtk-4.1-0) before launching {host.Name}.");
            }
        }

        var binaries = platform.BinariesDirectory;
        foreach (var file in new[] { "Talewright.Debugger.dll", "Talewright.Cli.runtimeconfig.json",
            "Talewright.Cli.deps.json" }.Concat(host.Assets))
        {
            RequireAsset(binaries, file, host.AssetName);
        }

        var hostDirectory = Path.Combine(binaries, "debug-host");
        var hostExecutable = "Talewright.Debugger" + (platform.IsWindows ? ".exe" : "");
        var bundledHost = Path.Combine(hostDirectory, hostExecutable);
        var standalone = File.Exists(bundledHost);
        if (Directory.Exists(hostDirectory) && !standalone)
        {
            RequireAsset(binaries, Path.Combine("debug-host", hostExecutable), host.AssetName);
        }
        if (standalone)
        {
            foreach (var file in new[] { "Talewright.Debugger.dll", "Talewright.Debugger.runtimeconfig.json",
                "Talewright.Debugger.deps.json" }.Concat(host.BundledAssets))
            {
                RequireAsset(binaries, Path.Combine("debug-host", file), host.AssetName);
            }
        }

        var start = new ProcessStartInfo(standalone ? bundledHost
            : platform.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = binaries,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!standalone)
        {
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(binaries, "Talewright.Cli.runtimeconfig.json"),
                "--depsfile", Path.Combine(binaries, "Talewright.Cli.deps.json"), Path.Combine(binaries, "Talewright.Debugger.dll") })
            {
                start.ArgumentList.Add(argument);
            }
        }
        if (mode == NativeHostMode.Player)
        {
            start.ArgumentList.Add("--play");
        }
        start.ArgumentList.Add(root);
        if (mode == NativeHostMode.Player)
        {
            start.ArgumentList.Add(Path.Combine(binaries, "publish-assets", "player"));
        }
        return RunProcess(start, host.Name, output, error, cancellationToken);
    }

    private static void RequireAsset(string root, string file, string assetName)
    {
        if (!File.Exists(Path.Combine(root, file)))
        {
            throw new FileNotFoundException($"Installed {assetName} asset '{file}' is missing. Reinstall the Talewright CLI.");
        }
    }

    internal static int RunProcess(ProcessStartInfo start, string name, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException($"Could not start the {name}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                process.WaitForExit();
            }
            output.Write(stdout.GetAwaiter().GetResult());
            error.Write(stderr.GetAwaiter().GetResult());
            throw;
        }
        output.Write(stdout.GetAwaiter().GetResult());
        error.Write(stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
        {
            throw new IOException($"{name} exited with code {process.ExitCode}. Check WebKitGTK 4.1 / WebView2 / WKWebView and the launch errors above.");
        }
        return 0;
    }
}
