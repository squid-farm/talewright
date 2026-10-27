using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

namespace Talewright.Player.E2E.Tests;

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<WebHostFixture>
{
    public const string Name = "Published player";
}

public sealed class WebHostFixture : IAsyncLifetime
{
    private WebApplication? _server;
    private IPlaywright? _playwright;
    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";
    public string BrowserName { get; } = Environment.GetEnvironmentVariable("BROWSER") ?? "chromium";

    public async Task InitializeAsync()
    {
        if (PlayerTarget.Name is not ("web" or "linux"))
        {
            throw new InvalidOperationException($"Unknown TALEWRIGHT_TARGET '{PlayerTarget.Name}'.");
        }

        if (PlayerTarget.IsDesktop)
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("TALEWRIGHT_WEB_BASE_PATH") ?? "";
        var basePath = path.TrimEnd('/');
        if (basePath.Length > 0 && (!basePath.StartsWith('/') || basePath.Contains("..")))
        {
            throw new InvalidOperationException("TALEWRIGHT_WEB_BASE_PATH must be an absolute URL path.");
        }
        var root = Environment.GetEnvironmentVariable("TALEWRIGHT_WEB_ROOT")
            ?? throw new InvalidOperationException("Set TALEWRIGHT_WEB_ROOT to the Release-published wwwroot. See tests/README.md.");
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "index.html")) || !Directory.Exists(Path.Combine(root, "_framework")))
        {
            throw new InvalidOperationException($"Not a published Blazor web root: {root}");
        }

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                WebRootPath = root,
            });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _server = builder.Build();
            if (basePath.Length > 0)
            {
                _server.Use(async (context, next) =>
                {
                    if (!context.Request.Path.StartsWithSegments(basePath))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
                _server.UsePathBase(basePath);
            }
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".wasm"] = "application/wasm";
            types.Mappings[".dat"] = "application/octet-stream";
            types.Mappings[".tale"] = "text/plain";
            types.Mappings[".yaml"] = "text/plain";
            _server.UseDefaultFiles();
            _server.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = types });
            await _server.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
            BaseUrl = _server.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + basePath + "/";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(BaseUrl);
            response.EnsureSuccessStatusCode();
            _playwright = await Playwright.CreateAsync();
            var type = BrowserName switch
            {
                "chromium" => _playwright.Chromium,
                "firefox" => _playwright.Firefox,
                "webkit" => _playwright.Webkit,
                _ => throw new InvalidOperationException($"Unknown BROWSER '{BrowserName}'."),
            };
            Browser = await type.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Timeout = 30_000 });
            Console.WriteLine($"Browser: {BrowserName} {Browser.Version}; .NET: {Environment.Version}; host: {BaseUrl}");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }
}
