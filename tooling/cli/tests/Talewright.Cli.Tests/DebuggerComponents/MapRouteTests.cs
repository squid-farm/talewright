using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapRouteTests : BunitContext
{
    [Fact]
    public void Without_a_playtest_it_explains_how_to_show_a_route()
    {
        var route = Render<MapRoute>();

        Assert.Equal("Current run route", route.Find("section[aria-label='Current run route'] h3").TextContent);
        Assert.Equal(["Start a synthetic playtest to show its route."], Paragraphs(route));
    }

    [Fact]
    public void A_run_with_no_entered_scenes_says_so()
    {
        var route = Show(new MapRunView("hall", false, false, null, []));

        Assert.Equal(["No scenes have been entered."], Paragraphs(route));
        Assert.Empty(route.FindAll("ol"));
    }

    [Fact]
    public void Lists_the_start_then_each_taken_divert_or_offer_in_order()
    {
        var route = Show(new MapRunView("hall", false, false, "hall", [
            new MapRunStep(1, "hall", null, null, null),
            new MapRunStep(2, "cave", "d0", "Divert to cave", "line0"),
            new MapRunStep(3, "hall", null, null, null),
        ]));

        Assert.Empty(Paragraphs(route));
        Assert.Equal(["Started at hall", "Divert to cave to cave", "hall"],
            route.FindAll("ol > li").Select(item => item.TextContent));
    }

    [Fact]
    public void A_mid_story_start_names_its_setup_scene()
    {
        var route = Show(new MapRunView("cave", true, false, "cave", [new MapRunStep(1, "cave", null, null, null)]));

        Assert.Equal(["Started at mid-story scene cave from Scene setup."], Paragraphs(route));
        Assert.Equal(["Started at cave"], route.FindAll("ol > li").Select(item => item.TextContent));
    }

    [Fact]
    public void A_real_playtest_route_follows_the_chosen_divert()
    {
        using var model = ComponentStory.Start();
        ComponentStory.Choose(model, "Explore");
        var map = MapView.Build(model.Story!);

        var route = Show(MapRunView.Build(model.Story!, map, model.Session!));

        var steps = route.FindAll("ol > li").Select(item => item.TextContent).ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Equal("Started at hall", steps[0]);
        Assert.EndsWith(" to cave", steps[1]);
    }

    private IRenderedComponent<MapRoute> Show(MapRunView run) =>
        Render<MapRoute>(parameters => parameters.Add(component => component.Run, run));

    private static IEnumerable<string> Paragraphs(IRenderedComponent<MapRoute> route) =>
        route.FindAll("p").Select(paragraph => paragraph.TextContent);
}
