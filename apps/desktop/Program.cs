using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Talewright.Player.Library;
using Talewright.Runtime.Saves;

namespace Talewright.Host.Desktop;

/// <summary>
/// Desktop entry point (Photino: native window + system webview, no browser or server).
/// Usage: <c>Talewright.Host.Desktop [--no-library] [--] [story-folder-or-zip ...]</c>. Paths given on the command line are added
/// to the library alongside the built-in samples, and the first one is opened immediately.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        DesktopLaunchOptions launch;
        try
        {
            launch = DesktopLaunchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (launch.ShowHelp)
        {
            Console.WriteLine(DesktopLaunchOptions.Usage);
            Console.WriteLine("--no-library  Open exactly one story without Library navigation. Retry remains available if opening fails.");
            return 0;
        }
        var paths = launch.Paths;

        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddSingleton<IStoryLibrary>(new CompositeStoryLibrary(
            new EmbeddedStoryLibrary(typeof(Program).Assembly),
            new FileSystemStoryLibrary(paths)));

        // Composite ids are "<library index>|<id>"; the file-system library is index 1 and uses full paths.
        var options = new DesktopOptions(paths.Count > 0 ? $"1|{Path.GetFullPath(paths[0])}" : null, launch.LibraryEnabled);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
        builder.RootComponents.Add<App>("app");

        var app = builder.Build();
        options.SetTitle = title => app.MainWindow.SetTitle(title);
        app.MainWindow
            .SetTitle("Talewright")
            .SetSize(1200, 850)
            .SetUseOsDefaultSize(false)
            .Center();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("Talewright error", e.ExceptionObject.ToString());

        app.Run();
        return 0;
    }
}

/// <summary>Command-line options passed into the Blazor component tree.</summary>
/// <param name="autoStartStoryId">Library id of the story to open on launch, if any.</param>
/// <param name="libraryEnabled">Whether players can browse or return to the library.</param>
internal sealed class DesktopOptions(string? autoStartStoryId, bool libraryEnabled = true)
{
    public string? AutoStartStoryId { get; } = autoStartStoryId;
    public bool LibraryEnabled { get; } = libraryEnabled;
    public Action<string>? SetTitle { get; set; }
}
