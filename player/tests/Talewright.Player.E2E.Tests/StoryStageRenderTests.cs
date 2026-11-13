using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Rulesets.Generic;
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

    [Fact]
    public async Task Speech_paragraphs_render_inside_one_speaker_card()
    {
        var yaml = """
            game: {id: speech-render, title: Speech Render, start: start}
            characters:
              player: {first-name: Ada}
              guide: {first-name: Bea}
            """;
        var story = new StoryFixture("speech-render", yaml, """
            === start
            > guide: First paragraph.
            >
            > Second paragraph.
            -> END
            """).Compile();
        var history = new StoryHistory(story, 42);

        var html = await RenderStageAsync(story, history);

        Assert.Contains("<div class=\"tw-speech\">", html);
        Assert.Contains("<span class=\"tw-speaker\">Bea</span>", html);
        Assert.Contains("<div class=\"tw-speech-body\">", html);
        Assert.Contains("<p class=\"tw-line\">First paragraph.</p>", html);
        Assert.Contains("<p class=\"tw-line\">Second paragraph.</p>", html);
        Assert.DoesNotContain("<p class=\"tw-speech\">", html);
    }

    [Fact]
    public async Task Hidden_stats_collapse_the_sheet_and_it_reappears_when_a_rule_becomes_true()
    {
        var story = VisibilityStory();
        var history = new StoryHistory(story, 42);

        var hidden = await RenderStageAsync(story, history);
        Assert.DoesNotContain("aria-label=\"Character sheet\"", hidden);
        Assert.Contains("class=\"tw-columns tw-columns--full\"", hidden);

        history.Choose(Assert.Single(history.Current.Choices).Key);
        var visible = await RenderStageAsync(story, history);
        Assert.Contains("aria-label=\"Character sheet\"", visible);
        Assert.Contains("<dt>Strength</dt>", visible);
        Assert.DoesNotContain("tw-columns--full", visible);
    }

    [Fact]
    public async Task Continued_scenes_render_in_the_previous_passage_without_a_section_break()
    {
        var story = new StoryFixture("continued-stage", TestStories.DefaultYaml, """
            === start
            @title Interview
            > guide: What brings you here?
            * [I need a job.] -> work
            === work
            @continues
            > guide: What can you do?
            * [I can mend boats.] -> answer
            === answer
            @continues
            > guide: We can use that.
            * [Thank you.] -> END
            """).Compile();
        var history = new StoryHistory(story, 42);
        history.Choose(history.Current.Choices.Single().Key);
        history.Choose(history.Current.Choices.Single().Key);

        var html = await RenderStageAsync(story, history, passageWindow: 3);

        Assert.Single(Regex.Matches(html, "<section\\b").Cast<Match>());
        Assert.Contains("data-tw-passage-index=\"2\"", html);
        Assert.Contains("Interview", html);
        Assert.Contains("What brings you here?", html);
        Assert.Contains("What can you do?", html);
        Assert.Contains("We can use that.", html);
        Assert.Contains("I can mend boats.", html);
        Assert.DoesNotContain("tw-past", html);
    }

    private static Talewright.Talemark.Model.StoryDefinition VisibilityStory()
    {
        var stats = new[] { "str", "dex", "con", "int", "wis", "cha", "hp", "xp", "level" }
            .Concat(GenericRuleset.Skills.Select(skill => skill.Id));
        var rules = string.Join('\n', stats.Select(stat =>
            $"      {stat}: {(stat == "str" ? "flag:revealed" : "false")}"));
        var yaml = $$"""
            game:
              id: visibility-render
              title: Visibility render
              start: beginning
              ruleset: rpg
              character-sheet:
                stats:
            {{rules}}
            flags: { revealed: false }
            characters:
              player:
                first-name: Ada
            """;
        return new StoryFixture("visibility-render", yaml, """
            === beginning
            * [Reveal] { set flag:revealed } -> END
            """).Compile();
    }

    private static async Task<string> RenderStageAsync(Talewright.Talemark.Model.StoryDefinition story,
        StoryHistory history, bool showCharacterSheet = true, int passageWindow = 2)
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
                [nameof(StoryStage.PassageWindow)] = passageWindow,
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
