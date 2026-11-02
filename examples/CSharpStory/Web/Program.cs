using CSharpStory;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Talewright.Player.Library;
using Talewright.Player.Saves;
using Talewright.Runtime.Saves;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Web.App>("#app");
builder.Services.AddSingleton<IStoryLibrary>(new EmbeddedStoryLibrary(typeof(StoryExtensions).Assembly));
builder.Services.AddScoped<ISaveStore, IndexedDbSaveStore>();
builder.Services.AddScoped<ISaveFileExporter, BrowserSaveFileExporter>();
await builder.Build().RunAsync();
