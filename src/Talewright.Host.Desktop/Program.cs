using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Talewright.Player.Library;

namespace Talewright.Host.Desktop;

/// <summary>
/// Desktop entry point (Photino: native window + system webview, no browser or server).
/// Usage: <c>Talewright.Host.Desktop [story-folder-or-zip ...]</c>. Paths given on the command line are added
/// to the library alongside the built-in samples, and the first one is opened immediately.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var paths = args.Where(a => !a.StartsWith('-')).ToList();

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddSingleton<IStoryLibrary>(new CompositeStoryLibrary(
            new EmbeddedStoryLibrary(typeof(Program).Assembly),
            new FileSystemStoryLibrary(paths)));

        // Composite ids are "<library index>|<id>"; the file-system library is index 1 and uses full paths.
        builder.Services.AddSingleton(new DesktopOptions(paths.Count > 0 ? $"1|{Path.GetFullPath(paths[0])}" : null));
        builder.RootComponents.Add<App>("app");

        var app = builder.Build();
        app.MainWindow
            .SetTitle("Talewright")
            .SetSize(1200, 850)
            .SetUseOsDefaultSize(false)
            .Center();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("Talewright error", e.ExceptionObject.ToString());

        app.Run();
    }
}

/// <summary>Command-line options passed into the Blazor component tree.</summary>
/// <param name="AutoStartStoryId">Library id of the story to open on launch, if any.</param>
internal sealed record DesktopOptions(string? AutoStartStoryId);
