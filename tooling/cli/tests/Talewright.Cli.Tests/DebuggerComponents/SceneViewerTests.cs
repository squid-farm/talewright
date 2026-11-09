using Bunit;
using Talewright.Debugger;
using Talewright.Runtime;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SceneViewerTests : BunitContext
{
    [Fact]
    public void Shows_the_current_passage_and_its_choices()
    {
        using var model = ComponentStory.Start();

        var viewer = Show(model.Session!);

        var passage = viewer.Find("section[aria-label='Current passage']");
        Assert.Equal("0", passage.GetAttribute("tabindex"));
        Assert.Equal("hall Great Hall", passage.QuerySelector("h3")!.TextContent);
        Assert.Contains("Count 0.", passage.TextContent);
        // Prepared is gated by flag:ready, which is still unset.
        Assert.Equal(["1. Explore", "2. Wait"],
            viewer.FindAll("ol[aria-label='Story choices'] button").Select(button => Text(button.TextContent)));
        Assert.Empty(viewer.FindAll("[role=alert]"));
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

        Assert.Equal($"This synthetic run reached the {StoryHistory.MaxChoices}-command limit. Start a fresh scene playtest to continue.",
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
}
