using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;
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
        if ((!play && args.Length != 1) || !Directory.Exists(play ? args[1] : args[0])
            || (play && !Directory.Exists(args[2])))
        {
            Console.Error.WriteLine(play
                ? "Player requires an existing story folder and installed player assets."
                : "Debugger requires one existing story folder.");
            return 2;
        }

        try
        {
            var root = Path.GetFullPath(play ? args[1] : args[0]);
            using var files = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory,
                play ? "player-assets" : "debug-assets"));
            using var playerFiles = play ? new PhysicalFileProvider(Path.GetFullPath(args[2])) : null;
            IFileProvider fileProvider = playerFiles is null ? files : new PlayerAssetFileProvider(files, playerFiles);
            var builder = PhotinoBlazorAppBuilder.CreateDefault(fileProvider, args);
            PlayerLaunchOptions? player = null;
            MapExportDialog? mapExportDialog = null;
            if (play)
            {
                player = new PlayerLaunchOptions(root);
                builder.Services.Configure<PhotinoBlazorAppConfiguration>(options => options.HostPage = "player.html");
                builder.Services.AddSingleton<IStoryLibrary>(new FileSystemStoryLibrary([root]));
                builder.Services.AddSingleton(player);
                builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
                builder.Services.AddScoped<PlayerPreferencesStore>();
                builder.RootComponents.Add<PlayerHost>("app");
            }
            else
            {
                builder.Services.AddSingleton(new WorkbenchModel(root));
                mapExportDialog = new MapExportDialog();
                builder.Services.AddSingleton(mapExportDialog);
                builder.RootComponents.Add<Workbench>("app");
            }
            var app = builder.Build();
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
            try
            {
                app.Run();
            }
            finally
            {
                // Player components dispose through the webview dispatcher, which has stopped here.
                // Like the desktop host, the short-lived player process exits without awaiting it.
                if (!play)
                {
                    app.WindowManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
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
