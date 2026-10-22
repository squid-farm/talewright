using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;

namespace Talewright.Debugger;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Debugger requires one existing story folder.");
            return 2;
        }

        try
        {
            using var files = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "debug-assets"));
            var builder = PhotinoBlazorAppBuilder.CreateDefault(files, args);
            builder.Services.AddSingleton(new WorkbenchModel(Path.GetFullPath(args[0])));
            builder.RootComponents.Add<Workbench>("app");
            var app = builder.Build();
            app.MainWindow.SetTitle("Talewright Author Debugger").SetSize(1400, 950)
                .SetUseOsDefaultSize(false).Center();
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.ExceptionObject);
                app.MainWindow.ShowMessage("Author Debugger error", e.ExceptionObject.ToString());
            };
            app.Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
            or BadImageFormatException or TypeInitializationException)
        {
            Console.Error.WriteLine($"Author Debugger could not start: {ex}");
            Console.Error.WriteLine("Check native prerequisites: WebKitGTK 4.1 on Linux, WebView2 on Windows, WKWebView on macOS.");
            return 2;
        }
    }
}
