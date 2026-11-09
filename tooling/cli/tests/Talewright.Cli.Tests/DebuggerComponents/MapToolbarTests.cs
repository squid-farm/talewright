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

        toolbar.Find("button").Click();

        Assert.Equal("Fit map", toolbar.Find("button").TextContent);
        Assert.Equal(1, fitted);
    }
}
