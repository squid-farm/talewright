using System.Runtime.InteropServices;
using Talewright.Cli;

namespace Talewright.Cli.Tests;

public sealed class NativeHostLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-launch-assets-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Debugger_accepts_player_assets_in_the_shared_publish_directory(bool bundled)
    {
        CreateInstallation(bundled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Cancellation at process startup proves that both asset checks passed without launching a window.
        Assert.Throws<OperationCanceledException>(() => Launch(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Debugger_rejects_missing_player_css_in_the_source_or_bundled_host(bool bundled)
    {
        CreateInstallation(bundled);
        var asset = Path.Combine(bundled ? "debug-host" : "", "publish-assets/player/talewright.css");
        File.Delete(Path.Combine(_root, asset));

        var error = Assert.Throws<FileNotFoundException>(() => Launch(CancellationToken.None));

        Assert.Contains($"Installed debugger asset '{asset}' is missing", error.Message);
        Assert.Contains("Reinstall the Talewright CLI.", error.Message);
    }

    private int Launch(CancellationToken cancellationToken) => NativeHostLauncher.Launch(
        NativeHostMode.Debugger, _root, TextWriter.Null, TextWriter.Null, cancellationToken,
        environment: new NativeHostEnvironment(false, true, false, Architecture.X64, "test-runtime",
            _root, _ => null, () => true));

    private void CreateInstallation(bool bundled)
    {
        WriteAssets(_root, ["Talewright.Debugger.dll", "Talewright.Cli.runtimeconfig.json", "Talewright.Cli.deps.json"]);
        WriteAssets(_root, ["debug-assets/index.html", "debug-assets/debugger.css", "publish-assets/player/talewright.css"]);
        if (bundled)
        {
            var host = Path.Combine(_root, "debug-host");
            WriteAssets(host, ["Talewright.Debugger.exe", "Talewright.Debugger.dll",
                "Talewright.Debugger.runtimeconfig.json", "Talewright.Debugger.deps.json",
                "debug-assets/index.html", "debug-assets/debugger.css", "publish-assets/player/talewright.css"]);
        }
    }

    private static void WriteAssets(string root, string[] assets)
    {
        foreach (var asset in assets)
        {
            var path = Path.Combine(root, asset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
