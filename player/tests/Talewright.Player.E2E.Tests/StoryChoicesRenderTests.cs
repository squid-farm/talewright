using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Player.Components;
using Talewright.Runtime;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryChoicesRenderTests
{
    [Fact]
    public async Task Menu_renders_unavailable_entries_with_stable_numbers_and_disables_them()
    {
        var frame = new StoryFrame(null, null, RenderedDocument.Empty,
            [new ChoiceView("available", [new RenderedText("Go", TextStyle.None)])], IsEnded: false)
        {
            Menu = [
                new ChoiceMenuEntry(1, "blocked", [new RenderedText("Not yet", TextStyle.None)], IsAvailable: false),
                new ChoiceMenuEntry(2, "available", [new RenderedText("Go", TextStyle.None)], IsAvailable: true),
            ],
        };

        var buttons = Buttons(await Render(frame));
        Assert.Equal(2, buttons.Length);
        Assert.Contains("data-tw-choice=\"blocked\"", buttons[0].Groups["attributes"].Value);
        Assert.Contains("data-tw-choice=\"available\"", buttons[1].Groups["attributes"].Value);
        Assert.Contains("disabled", buttons[0].Groups["attributes"].Value);
        Assert.DoesNotContain("disabled", buttons[1].Groups["attributes"].Value);
        Assert.Contains("1.", buttons[0].Groups["body"].Value);
        Assert.Contains("Not yet", buttons[0].Groups["body"].Value);
        Assert.Contains("(Unavailable)", buttons[0].Groups["body"].Value);
        Assert.Contains("2.", buttons[1].Groups["body"].Value);
        Assert.Contains("Go", buttons[1].Groups["body"].Value);
        Assert.DoesNotContain("(Unavailable)", buttons[1].Groups["body"].Value);
    }

    [Fact]
    public async Task Shown_only_frame_renders_a_marked_unavailable_choice()
    {
        var frame = new StoryFrame(null, null, RenderedDocument.Empty, [], IsEnded: false)
        {
            Menu = [new ChoiceMenuEntry(1, "blocked", [new RenderedText("Not yet", TextStyle.None)], IsAvailable: false)],
        };

        var button = Assert.Single(Buttons(await Render(frame)));
        Assert.Contains("data-tw-choice=\"blocked\"", button.Groups["attributes"].Value);
        Assert.Contains("disabled", button.Groups["attributes"].Value);
        Assert.Contains("1.", button.Groups["body"].Value);
        Assert.Contains("Not yet", button.Groups["body"].Value);
        Assert.Contains("(Unavailable)", button.Groups["body"].Value);
    }

    private static async Task<string> Render(StoryFrame frame)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, UnusedJsRuntime>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(StoryChoices.Current)] = frame,
            });
            var component = await renderer.RenderComponentAsync<StoryChoices>(parameters);
            return component.ToHtmlString();
        });
        return html;
    }

    private static Match[] Buttons(string html) =>
        Regex.Matches(html, @"<button\b(?<attributes>[^>]*)>(?<body>.*?)</button>",
                RegexOptions.Singleline)
            .Cast<Match>()
            .ToArray();

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
    }
}
