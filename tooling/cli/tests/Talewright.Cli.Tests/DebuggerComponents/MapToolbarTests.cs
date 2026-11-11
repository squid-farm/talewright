using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class MapToolbarTests : BunitContext
{
    [Fact]
    public void Shows_layout_progress_until_the_map_reports_a_status()
    {
        var toolbar = Render<MapToolbar>();
        Assert.Equal("Laying out the map…", toolbar.Find("p[role=status]").TextContent);

        toolbar.Render(parameters => parameters.Add(component => component.Status, "3 scenes, 2 diverts, 0 hub offers, 0 unreachable."));
        Assert.Equal("3 scenes, 2 diverts, 0 hub offers, 0 unreachable.", toolbar.Find("p[role=status]").TextContent);
    }

    [Fact]
    public void Fit_map_asks_the_map_to_fit()
    {
        var fitted = 0;
        var toolbar = Render<MapToolbar>(parameters => parameters.Add(component => component.OnFit, () => fitted++));

        toolbar.FindAll("button").Single(button => button.TextContent == "Fit map").Click();

        Assert.Equal(1, fitted);
    }

    [Fact]
    public void Focus_and_export_actions_are_available_from_the_toolbar()
    {
        var focused = 0;
        var svgExports = 0;
        var pngExports = 0;
        var toolbar = Render<MapToolbar>(parameters => parameters
            .Add(component => component.MapReady, true)
            .Add(component => component.OnFocusCurrentScene, () => focused++)
            .Add(component => component.OnExportSvg, () => svgExports++)
            .Add(component => component.OnExportPng, () => pngExports++));

        toolbar.FindAll("button").Single(button => button.TextContent == "Focus current scene").Click();
        toolbar.FindAll("button").Single(button => button.TextContent == "Export SVG").Click();
        toolbar.FindAll("button").Single(button => button.TextContent == "Export PNG").Click();

        Assert.Equal(1, focused);
        Assert.Equal(1, svgExports);
        Assert.Equal(1, pngExports);
        Assert.Equal("Export story map", toolbar.Find(".map-export-controls").GetAttribute("aria-label"));
    }
}
