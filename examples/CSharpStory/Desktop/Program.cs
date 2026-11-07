using CSharpStory;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Talewright.Player.Diagnostics;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Player.Saves;
using Talewright.Runtime.Saves;

var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
builder.Services.AddSingleton<IStoryLibrary>(new EmbeddedStoryLibrary(typeof(StoryExtensions).Assembly));
builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
PhotinoWindow? window = null;
builder.Services.AddSingleton<ISaveFileExporter>(new LocalSaveFileExporter(name =>
{
    var ready = window ?? throw new InvalidOperationException("The desktop window is not ready.");
    return NativeSaveFileDialog.ChooseAsync(name, action => ready.Invoke(action),
        OperatingSystem.IsWindows() ? () => ready.WindowHandle : null);
}));
builder.Services.AddSingleton<IDiagnosticReportExporter>(new LocalDiagnosticReportExporter(name =>
{
    var ready = window ?? throw new InvalidOperationException("The desktop window is not ready.");
    return NativeSaveFileDialog.ChooseReportAsync(name, action => ready.Invoke(action),
        OperatingSystem.IsWindows() ? () => ready.WindowHandle : null);
}));
builder.Services.AddScoped<PlayerDiagnostics>();
builder.Services.AddScoped<PlayerPreferencesStore>();
builder.RootComponents.Add<Desktop.App>("app");
var app = builder.Build();
window = app.MainWindow;
app.MainWindow.SetTitle("CSharp Story");
app.Run();
