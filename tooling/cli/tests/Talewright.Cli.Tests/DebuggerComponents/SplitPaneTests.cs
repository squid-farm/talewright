using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SplitPaneTests : BunitContext
{
    [Theory]
    [InlineData("Horizontal", "split-horizontal")]
    [InlineData("Vertical", "split-vertical")]
    public void Collapsing_and_restoring_the_second_pane_updates_the_layout(string orientation, string orientationClass)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./debugger/workspace.js");
        var pane = Render<SplitPane>(parameters => parameters
            .Add(component => component.Name, "Resize")
            .Add(component => component.Orientation, orientation)
            .Add(component => component.Size, 60d)
            .Add(component => component.FirstPane, builder => builder.AddContent(0, "First"))
            .Add(component => component.SecondPane, builder => builder.AddContent(0, "Second")));

        Assert.Contains(orientationClass, pane.Find(".split-pane").ClassList);
        Assert.DoesNotContain("split-collapsed", pane.Find(".split-pane").ClassList);
        Assert.Single(pane.FindAll("[role=separator]"));
        Assert.Equal("Second", pane.Find(".split-second").TextContent);

        pane.Render(parameters => parameters.Add(component => component.Collapsed, true));
        Assert.Contains("split-collapsed", pane.Find(".split-pane").ClassList);
        Assert.Equal("First", pane.Find(".split-first").TextContent);
        Assert.Empty(pane.FindAll("[role=separator], .split-second"));

        pane.Render(parameters => parameters.Add(component => component.Collapsed, false));
        Assert.DoesNotContain("split-collapsed", pane.Find(".split-pane").ClassList);
        Assert.Single(pane.FindAll("[role=separator]"));
        Assert.Equal("Second", pane.Find(".split-second").TextContent);
        Assert.Contains("60%", pane.Find(".split-pane").GetAttribute("style"));
    }

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
