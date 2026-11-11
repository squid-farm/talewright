using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Runtime;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryStageRenderTests
{
    [Fact]
    public async Task Renders_recent_passages_choices_and_debugging_hooks()
    {
        var story = new StoryFixture("stage", TestStories.DefaultYaml, """
            === start
            The first passage.
            * [Continue] -> middle
            === middle
            The second passage.
            * [Continue] -> end
            === end
            The current passage.
            * [Finish] -> END
            """).Compile();
        var history = new StoryHistory(story, 42);
        history.Choose(history.Current.Choices.Single().Key);
        history.Choose(history.Current.Choices.Single().Key);
        var html = await RenderStageAsync(story, history);

        Assert.Contains("<div class=\"tw-columns\">", html);
        Assert.Contains("<main class=\"tw-transcript\" aria-label=\"Story transcript\" aria-live=\"polite\" tabindex=\"-1\">", html);
        Assert.Contains("class=\"tw-frame tw-latest\"", html);
        Assert.Contains("aria-label=\"Current passage\"", html);
        Assert.Contains("tabindex=\"-1\"", html);
        Assert.DoesNotContain("data-tw-scene=\"start\"", html);
        Assert.Contains("data-tw-scene=\"middle\"", html);
        Assert.Contains("data-tw-scene=\"end\"", html);
        Assert.Equal(2, Regex.Matches(html, "data-tw-scene=").Count);
        Assert.Contains("aria-label=\"Character sheet\"", html);
        Assert.Contains("class=\"tw-choices\" aria-label=\"Story choices\"", html);
        var choice = Assert.Single(Regex.Matches(html, @"<button\b(?<attributes>[^>]*)>(?<body>.*?)</button>",
            RegexOptions.Singleline).Cast<Match>());
        Assert.Contains("data-tw-choice=\"c0\"", choice.Groups["attributes"].Value);
        Assert.Contains("Finish", choice.Groups["body"].Value);
    }

    [Fact]
    public async Task Passage_only_presentation_omits_the_character_sheet()
    {
        var story = TestStories.Linear.Compile();
        var history = new StoryHistory(story, 42);

        var html = await RenderStageAsync(story, history, showCharacterSheet: false);

        Assert.Contains("class=\"tw-columns tw-columns--full\"", html);
        Assert.Contains("aria-label=\"Current passage\"", html);
        Assert.Contains("aria-label=\"Story choices\"", html);
        Assert.DoesNotContain("aria-label=\"Character sheet\"", html);
    }

    private static async Task<string> RenderStageAsync(Talewright.Talemark.Model.StoryDefinition story,
        StoryHistory history, bool showCharacterSheet = true)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(StoryStage.Story)] = story,
                [nameof(StoryStage.Passages)] = history.Transcript,
                [nameof(StoryStage.PassageWindow)] = 2,
                [nameof(StoryStage.Current)] = history.Current,
                [nameof(StoryStage.State)] = history.State,
                [nameof(StoryStage.Identity)] = history,
                [nameof(StoryStage.ShowCharacterSheet)] = showCharacterSheet,
            });
            var component = await renderer.RenderComponentAsync<StoryStage>(parameters);
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
