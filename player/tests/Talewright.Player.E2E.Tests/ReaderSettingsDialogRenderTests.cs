using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Preferences;

namespace Talewright.Player.E2E.Tests;

public sealed class ReaderSettingsDialogRenderTests
{
    [Fact]
    public async Task Reader_settings_dialog_contains_all_six_preference_controls()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ReaderSettingsDialog>(ParameterView.Empty);
            return component.ToHtmlString();
        });

        var start = html.IndexOf("class=\"tw-settings\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = html.IndexOf("</dialog>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var controls = Regex.Matches(html[start..end], @"<(?:select|input)\b[^>]*>")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();

        Assert.Collection(controls,
            control => Assert.Contains("id=\"tw-theme\"", control),
            control => Assert.Contains("id=\"tw-text-size\"", control),
            control => Assert.Contains("type=\"checkbox\"", control),
            control => Assert.Contains("id=\"tw-reading-width\"", control),
            control => Assert.Contains("id=\"tw-line-spacing\"", control),
            control => Assert.Contains("id=\"tw-reduced-motion\"", control));
        Assert.Contains("Close settings", html);
    }

    [Fact]
    public async Task Settings_show_the_stamped_build_identity()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ReaderSettingsDialog>(ParameterView.Empty);
            return component.ToHtmlString();
        });

        Assert.Matches(@"^(dev|\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$", PlayerBuildIdentity.Version);
        Assert.Matches("^([0-9a-f]{7}|unknown)$", PlayerBuildIdentity.Commit);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", PlayerBuildIdentity.Date);
        var settings = html[html.IndexOf("class=\"tw-settings\"", StringComparison.Ordinal)..];
        settings = settings[..settings.IndexOf("</dialog>", StringComparison.Ordinal)];
        Assert.Contains(
            $"<p class=\"tw-build-identity\">Talewright {PlayerBuildIdentity.Version} · commit {PlayerBuildIdentity.Commit} · built {PlayerBuildIdentity.Date}</p>",
            System.Net.WebUtility.HtmlDecode(settings));
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
