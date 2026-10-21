using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Talewright.Host.Web;
using Talewright.Player.Library;
using Talewright.Player.Saves;
using Talewright.Runtime.Saves;

// Browser entry point: the whole engine runs client-side in WebAssembly. The published output is static
// files only, so it can be hosted anywhere (no server code, no API).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Bundled samples are embedded in this assembly; players can also open a .zip from the library screen.
builder.Services.AddSingleton<IStoryLibrary>(new EmbeddedStoryLibrary(typeof(App).Assembly));
builder.Services.AddScoped<ISaveStore, IndexedDbSaveStore>();

await builder.Build().RunAsync();
