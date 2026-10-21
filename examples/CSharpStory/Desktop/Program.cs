using CSharpStory;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Talewright.Player.Library;
using Talewright.Runtime.Saves;

var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
builder.Services.AddSingleton<IStoryLibrary>(new EmbeddedStoryLibrary(typeof(StoryExtensions).Assembly));
builder.Services.AddSingleton<ISaveStore>(new FileSaveStore(FileSaveStore.DefaultRoot));
builder.RootComponents.Add<Desktop.App>("app");
var app = builder.Build();
app.MainWindow.SetTitle("CSharp Story");
app.Run();
