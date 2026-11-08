using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Preferences;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class PlayerSheetRenderTests
{
    [Fact]
    public async Task Story_without_sheet_sections_omits_sheet_and_uses_full_transcript_layout()
    {
        var html = await RenderPlayerAsync(TestStories.NoStats.Compile());

        Assert.DoesNotContain("aria-label=\"Character sheet\"", html);
        Assert.Contains("class=\"tw-columns tw-columns--full\"", html);
        Assert.Contains("class=\"tw-transcript\"", html);
        Assert.Contains("A story without statistics.", html);
    }

    [Fact]
    public async Task Story_with_items_only_shows_inventory_even_when_empty()
    {
        var manifest = new ManifestFaker("items-only", "Items Only", "start", seed: 42)
        {
            BaseRuleset = "none",
            Randomness = "off",
        };
        manifest.Character("player", "Alex", pronouns: "they");
        manifest.Item("token", "Test token");
        var story = new StoryFixture("items-only", manifest.Build(), """
            === start
            A story with declared items.
            -> END
            """).Compile();

        var html = await RenderPlayerAsync(story);
        var sheetStart = html.IndexOf("<aside class=\"tw-sheet\"", StringComparison.Ordinal);
        var sheetEnd = html.IndexOf("</aside>", sheetStart, StringComparison.Ordinal);
        Assert.True(sheetStart >= 0 && sheetEnd > sheetStart, html);
        var sheet = html[sheetStart..(sheetEnd + "</aside>".Length)];

        Assert.DoesNotContain("<h2>", sheet);
        Assert.Contains("<h3>Inventory</h3>", sheet);
        Assert.Contains("<p class=\"tw-empty\">Nothing.</p>", sheet);
        Assert.Single(Regex.Matches(sheet, "<h3>").Cast<Match>());
        Assert.DoesNotContain("tw-columns--full", html);
    }

    [Fact]
    public async Task Story_with_existing_sheet_sections_keeps_them_visible()
    {
        var html = await RenderPlayerAsync(TestStories.Gates.Compile());
        var sheetStart = html.IndexOf("<aside class=\"tw-sheet\"", StringComparison.Ordinal);
        var sheetEnd = html.IndexOf("</aside>", sheetStart, StringComparison.Ordinal);
        Assert.True(sheetStart >= 0 && sheetEnd > sheetStart, html);
        var sheet = html[sheetStart..(sheetEnd + "</aside>".Length)];

        Assert.Contains("<h2>Mara Burns</h2>", sheet);
        Assert.Contains("<h3>Attributes</h3>", sheet);
        Assert.Contains("<h3>Inventory</h3>", sheet);
        Assert.Contains("<h3>Standing</h3>", sheet);
        Assert.DoesNotContain("tw-columns--full", html);
    }

    private static async Task<string> RenderPlayerAsync(StoryDefinition story)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(StoryPlayer.Story)] = story,
                [nameof(StoryPlayer.AutosaveResumeChecked)] = true,
            });
            var component = await renderer.RenderComponentAsync<StoryPlayer>(parameters);
            return component.ToHtmlString();
        });
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
