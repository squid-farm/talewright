using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Player.Saves;
using Talewright.Runtime.Saves;

namespace Talewright.Host.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var story = Path.Combine(AppContext.BaseDirectory, "story");
        if (!Directory.Exists(story))
            story = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "story"));
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
        builder.Services.AddSingleton<IStoryLibrary>(new FileSystemStoryLibrary([story]));
        var options = new DesktopOptions(story);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
        PhotinoWindow? window = null;
        builder.Services.AddSingleton<ISaveFileExporter>(new LocalSaveFileExporter(name =>
            DesktopSaveFileDialog.ChooseAsync(window ?? throw new InvalidOperationException("The desktop window is not ready."), name)));
        builder.Services.AddScoped<PlayerPreferencesStore>();
        builder.RootComponents.Add<App>("app");
        var app = builder.Build();
        window = app.MainWindow;
        options.SetTitle = title => app.MainWindow.SetTitle(title);
        app.MainWindow.SetTitle("Talewright").SetSize(1200, 850).SetUseOsDefaultSize(false).Center();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            app.MainWindow.ShowMessage("Talewright error", e.ExceptionObject.ToString());
        app.Run();
    }
}

internal sealed class DesktopOptions(string autoStartStoryId)
{
    public string AutoStartStoryId { get; } = autoStartStoryId;
    public Action<string>? SetTitle { get; set; }
}
