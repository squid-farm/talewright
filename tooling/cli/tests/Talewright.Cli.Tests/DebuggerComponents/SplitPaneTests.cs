using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SplitPaneTests : BunitContext
{
    [Fact]
    public async Task Resizes_by_arrow_home_and_end_keys()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./debugger/workspace.js");
        var sizes = new List<double>();
        var pane = Render<SplitPane>(parameters => parameters
            .Add(component => component.Name, "Resize inspector")
            .Add(component => component.Orientation, "Horizontal")
            .Add(component => component.Size, 70d)
            .Add(component => component.OnSizeChanged,
                EventCallback.Factory.Create<double>(this, size => sizes.Add(size))));

        var separator = pane.Find("[role=separator]");
        Assert.Equal("vertical", separator.GetAttribute("aria-orientation"));
        Assert.Equal("Resize inspector", separator.GetAttribute("aria-label"));
        Assert.Equal("70", separator.GetAttribute("aria-valuenow"));

        await separator.KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal(72, Assert.Single(sizes));

        await separator.KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        Assert.Equal(20, sizes[^1]);
        await separator.KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Assert.Equal(80, sizes[^1]);
    }
}
