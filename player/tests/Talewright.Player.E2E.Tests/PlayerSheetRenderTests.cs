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

    [Theory]
    [InlineData(false, null)]
    [InlineData(null, "stats,inventory,standing")]
    public async Task Author_can_hide_the_whole_sheet_without_leaving_layout_space(bool? sheet, string? hidden)
    {
        var html = await RenderPlayerAsync(GatesWithSheet(sheet, hidden));

        Assert.DoesNotContain("aria-label=\"Character sheet\"", html);
        Assert.Contains("class=\"tw-columns tw-columns--full\"", html);
        Assert.Contains("class=\"tw-transcript\"", html);
    }

    [Theory]
    [InlineData("stats", "<h2>Mara Burns</h2>", new[] { "Inventory", "Standing" })]
    [InlineData("inventory", "<h2>Mara Burns</h2>", new[] { "Attributes", "Standing" })]
    [InlineData("standing", "<h2>Mara Burns</h2>", new[] { "Attributes", "Inventory" })]
    [InlineData("stats,standing", null, new[] { "Inventory" })]
    public async Task Author_can_hide_individual_sheet_sections(string hidden, string? name, string[] shown)
    {
        var html = await RenderPlayerAsync(GatesWithSheet(null, hidden));
        var sheet = SheetHtml(html);

        if (name is null)
        {
            Assert.DoesNotContain("<h2>", sheet);
        }
        else
        {
            Assert.Contains(name, sheet);
        }
        Assert.Equal(shown, Regex.Matches(sheet, "<h3>([^<]+)</h3>").Select(match => match.Groups[1].Value)
            .Where(heading => heading is "Attributes" or "Inventory" or "Standing"));
        if (hidden.Contains("stats", StringComparison.Ordinal))
        {
            Assert.DoesNotMatch("<h3>(Attributes|Skills|Resources|Progress|Other)</h3>", sheet);
        }
        Assert.DoesNotContain("tw-columns--full", html);
    }

    private static StoryDefinition GatesWithSheet(bool? sheet, string? hidden) =>
        (TestStories.Gates with
        {
            Yaml = TestStories.DefaultManifest(
                characterSheet: sheet,
                characterSheetSections: hidden?.Split(',').ToDictionary(section => section, _ => false)).Build(),
        }).Compile();

    private static string SheetHtml(string html)
    {
        var sheetStart = html.IndexOf("<aside class=\"tw-sheet\"", StringComparison.Ordinal);
        var sheetEnd = html.IndexOf("</aside>", Math.Max(0, sheetStart), StringComparison.Ordinal);
        Assert.True(sheetStart >= 0 && sheetEnd > sheetStart, html);
        return html[sheetStart..(sheetEnd + "</aside>".Length)];
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
