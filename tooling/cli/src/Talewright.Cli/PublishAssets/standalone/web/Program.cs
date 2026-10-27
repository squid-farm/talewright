using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Talewright.Host.Web;
using Talewright.Player.Library;
using Talewright.Player.Saves;
using Talewright.Runtime.Saves;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddSingleton<IStoryLibrary>(new PublishedStoryLibrary(
    new HttpClient { BaseAddress = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "story/") }));
builder.Services.AddScoped<ISaveStore, IndexedDbSaveStore>();
await builder.Build().RunAsync();
