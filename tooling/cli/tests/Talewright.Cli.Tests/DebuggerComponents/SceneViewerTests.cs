using Bunit;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Debugger;
using Talewright.Player.Components;
using Talewright.Player.Diagnostics;
using Talewright.Player.Preferences;
using Talewright.Runtime;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SceneViewerTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SceneViewerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Talewright.Player/Preferences/PlayerPreferences.js");
        Services.AddScoped<PlayerPreferencesStore>();
    }

    [Fact]
    public void Shows_the_current_passage_and_its_choices()
    {
        using var model = ComponentStory.Start();

        var viewer = Show(model.Session!);

        var passage = viewer.Find("section[aria-label='Current passage']");
        Assert.Equal("-1", passage.GetAttribute("tabindex"));
        Assert.Equal("hall Great Hall", viewer.Find("h3.debugger-scene-heading").TextContent);
        Assert.Contains("Count 0.", passage.TextContent);
        // Prepared is gated by flag:ready, which is still unset.
        Assert.Equal(["1. Explore", "2. Wait"],
            viewer.FindAll("ol[aria-label='Story choices'] button").Select(button => Text(button.TextContent)));
        Assert.Empty(viewer.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task Renders_the_same_story_stage_as_the_player()
    {
        using var model = ComponentStory.Start();
        var session = model.Session!;
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime, PreferencesJsRuntime>()
            .AddSingleton<PlayerDiagnostics>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var passages = new[] { new HistoryFrame(session.Current) };

        var rendered = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var viewer = await renderer.RenderComponentAsync<SceneViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(SceneViewer.Session)] = session,
                    [nameof(SceneViewer.OnChoose)] = EventCallback<ChoiceView>.Empty,
                    [nameof(SceneViewer.OnRestart)] = EventCallback.Empty,
                }));
            var stage = await renderer.RenderComponentAsync<StoryStage>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(StoryStage.Story)] = session.Story,
                    [nameof(StoryStage.Passages)] = passages,
                    [nameof(StoryStage.PassageWindow)] = 1,
                    [nameof(StoryStage.Current)] = session.Current,
                    [nameof(StoryStage.State)] = session.State,
                    [nameof(StoryStage.Identity)] = session,
                }));
            return (Viewer: viewer.ToHtmlString(), Stage: stage.ToHtmlString());
        });

        static string StageMarkup(string html) =>
            new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).QuerySelector(".tw-columns")!.OuterHtml;

        Assert.Equal(StageMarkup(rendered.Stage), StageMarkup(rendered.Viewer));
    }

    [Fact]
    public void Choosing_raises_the_choice()
    {
        using var model = ComponentStory.Start();
        var chosen = new List<ChoiceView>();
        var viewer = Show(model.Session!, choose: chosen.Add);

        viewer.FindAll("ol[aria-label='Story choices'] button").Single(button => button.TextContent.Contains("Explore")).Click();

        Assert.Equal("Explore", Assert.Single(chosen).Text);
    }

    [Fact]
    public void An_ended_run_offers_a_restart()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        ComponentStory.Choose(model, "Leave");
        Assert.True(model.Session!.Current.IsEnded);
        var restarts = 0;
        var viewer = Show(model.Session, restart: () => restarts++);

        viewer.FindAll("button").Single(button => button.TextContent == "Play again").Click();

        Assert.Equal(1, restarts);
        Assert.Empty(viewer.FindAll("[role=alert]"));
    }

    [Fact]
    public void A_run_at_the_command_limit_is_busy_and_explains_how_to_continue()
    {
        using var model = ComponentStory.Start();
        var wait = model.Session!.Current.Choices.Single(choice => choice.Text == "Wait").Key;
        // Each author command replays the timeline, so build the limit through linear player history and fork it.
        var history = new StoryHistory(model.Story!, model.Session.Seed);
        for (var i = 0; i < StoryHistory.MaxChoices; i++)
            history.Choose(wait);
        var session = history.ForkAuthor();
        Assert.Equal(StoryHistory.MaxChoices, session.Cursor);

        var viewer = Show(session);

        Assert.Equal($"This playthrough has reached the {StoryHistory.MaxChoices:N0}-choice limit. You can save, go back, or restart to continue playing.",
            viewer.Find("p[role=alert]").TextContent);
        var choices = viewer.FindAll("ol[aria-label='Story choices'] button");
        Assert.Equal(["1. Explore", "2. Wait"], choices.Select(button => Text(button.TextContent)));
        Assert.All(choices, button => Assert.True(button.HasAttribute("disabled")));
    }

    private static string Text(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private IRenderedComponent<SceneViewer> Show(AuthorSession session, Action<ChoiceView>? choose = null, Action? restart = null) =>
        Render<SceneViewer>(parameters => parameters
            .Add(component => component.Session, session)
            .Add(component => component.OnChoose, choose ?? (_ => { }))
            .Add(component => component.OnRestart, restart ?? (() => { })));

    private sealed class PreferencesJsRuntime : IJSRuntime
    {
        private readonly PreferencesModule _module = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import" || args is not [string path]
                || path != "./_content/Talewright.Player/Preferences/PlayerPreferences.js"
                || typeof(TValue) != typeof(IJSObjectReference))
            {
                throw new InvalidOperationException($"Unexpected JavaScript invocation '{identifier}'.");
            }

            return ValueTask.FromResult((TValue)(object)_module);
        }
    }

    private sealed class PreferencesModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier != "read" && !identifier.StartsWith("apply", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected preferences invocation '{identifier}'.");

            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
