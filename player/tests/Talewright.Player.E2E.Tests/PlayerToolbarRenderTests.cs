using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Preferences;

namespace Talewright.Player.E2E.Tests;

public sealed class PlayerToolbarRenderTests
{
    [Fact]
    public async Task Settings_keep_existing_controls_before_reading_preferences()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PlayerToolbar.Title)] = "Test story",
            });
            var component = await renderer.RenderComponentAsync<PlayerToolbar>(parameters);
            return component.ToHtmlString();
        });

        var start = html.IndexOf("class=\"tw-settings\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = html.IndexOf("</dialog>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var controls = Regex.Matches(html[start..end], @"<(?:select|input|button)\b[^>]*>")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();

        Assert.Collection(controls,
            control => Assert.Contains("id=\"tw-theme\"", control),
            control => Assert.Contains("id=\"tw-text-size\"", control),
            control => Assert.Contains("type=\"checkbox\"", control),
            control => Assert.Contains("id=\"tw-reading-width\"", control),
            control => Assert.Contains("id=\"tw-line-spacing\"", control),
            control => Assert.Contains("id=\"tw-reduced-motion\"", control),
            control => Assert.Contains("class=\"tw-choice\"", control));
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
    }
}
