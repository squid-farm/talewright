using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;
using Photino.NET;
using Talewright.Player.Diagnostics;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Runtime.Saves;

namespace Talewright.Debugger;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var play = args.Length == 3 && args[0] == "--play";
        var debuggerScene = !play && args.Length == 3 && args[1] == "--scene" ? args[2] : null;
        var validDebuggerArguments = !play && (args.Length == 1 || debuggerScene is not null);
        if ((!play && !validDebuggerArguments) || !Directory.Exists(play ? args[1] : args[0])
            || (play && !Directory.Exists(args[2])))
        {
            Console.Error.WriteLine(play
                ? "Player requires an existing story folder and installed player assets."
                : "Debugger requires one existing story folder, optionally followed by --scene <id>.");
            return 2;
        }

        try
        {
            var root = Path.GetFullPath(play ? args[1] : args[0]);
            using var files = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory,
                play ? "player-assets" : "debug-assets"));
            using var playerFiles = new PhysicalFileProvider(play
                ? Path.GetFullPath(args[2])
                : Path.Combine(AppContext.BaseDirectory, "publish-assets/player"));
            IFileProvider fileProvider = new PlayerAssetFileProvider(files, playerFiles);
            var builder = PhotinoBlazorAppBuilder.CreateDefault(fileProvider, args);
            PlayerLaunchOptions? player = null;
            MapExportDialog? mapExportDialog = null;
            PhotinoWindow? window = null;
            if (play)
            {
                player = new PlayerLaunchOptions(root);
                builder.Services.Configure<PhotinoBlazorAppConfiguration>(options => options.HostPage = "player.html");
                builder.Services.AddSingleton<IStoryLibrary>(new FileSystemStoryLibrary([root]));
                builder.Services.AddSingleton(player);
                builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
                builder.Services.AddSingleton<IDiagnosticReportExporter>(new LocalDiagnosticReportExporter(async name =>
                {
                    var ready = window ?? throw new InvalidOperationException("The desktop window is not ready.");
                    if (Environment.GetEnvironmentVariable("TALEWRIGHT_TEST_ONLY_SAVE_EXPORT_DIRECTORY") is { Length: > 0 } directory)
                    {
                        return Path.Combine(directory, name);
                    }
                    var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    return await ready.ShowSaveFileAsync("Export diagnostic report", folder, [("Text file", ["txt"])]);
                }));
                builder.Services.AddScoped<PlayerDiagnostics>();
                builder.Services.AddScoped<PlayerPreferencesStore>();
                builder.RootComponents.Add<PlayerHost>("app");
            }
            else
            {
                builder.Services.AddScoped<PlayerDiagnostics>();
                builder.Services.AddScoped<PlayerPreferencesStore>();
                builder.Services.AddSingleton(new WorkbenchModel(root, debuggerScene));
                mapExportDialog = new MapExportDialog();
                builder.Services.AddSingleton(mapExportDialog);
                builder.RootComponents.Add<Workbench>("app");
            }
            var app = builder.Build();
            window = app.MainWindow;
            if (player is not null)
            {
                app.MainWindow.LogVerbosity = 0;
                player.SetTitle = title => app.MainWindow.SetTitle(title);
                app.MainWindow.SetTitle("Talewright").SetSize(1200, 850).SetUseOsDefaultSize(false).Center();
            }
            else
            {
                mapExportDialog!.Attach(app.MainWindow);
                app.MainWindow.SetTitle("Talewright Author Debugger").SetSize(1400, 950)
                    .SetUseOsDefaultSize(false).Center();
            }
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.ExceptionObject);
                app.MainWindow.ShowMessage(play ? "Talewright error" : "Author Debugger error",
                    e.ExceptionObject.ToString());
            };
            // Shared player components dispose their JavaScript modules on the WebView dispatcher.
            app.Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
            or BadImageFormatException or TypeInitializationException)
        {
            Console.Error.WriteLine($"{(play ? "Talewright Player" : "Author Debugger")} could not start: {ex}");
            Console.Error.WriteLine("Check native prerequisites: WebKitGTK 4.1 on Linux, WebView2 on Windows, WKWebView on macOS.");
            return 2;
        }
    }
}

internal sealed class PlayerLaunchOptions(string storyRoot)
{
    public string StoryRoot { get; } = storyRoot;
    public Action<string>? SetTitle { get; set; }
}
