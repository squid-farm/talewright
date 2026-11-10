using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class ToolTabsTests : BunitContext
{
    [Fact]
    public async Task Arrow_and_home_end_keys_select_and_focus_the_tool_tab()
    {
        var selected = "";
        var tabs = Render<ToolTabs>(parameters => parameters
            .Add(component => component.Selected, "Setup")
            .Add(component => component.SelectedChanged,
                EventCallback.Factory.Create<string>(this, tab => selected = tab)));

        Assert.Equal("Debugger tools", tabs.Find("[role=tablist]").GetAttribute("aria-label"));
        Assert.Equal(["Setup", "State", "Source", "Branches", "Timeline", "Events", "Problems"],
            tabs.FindAll("[role=tab]").Select(tab => tab.TextContent));
        Assert.False(tabs.Find("#tool-setup-panel").HasAttribute("hidden"));
        Assert.True(tabs.Find("#tool-state-panel").HasAttribute("hidden"));

        await tabs.Find("#tool-setup-tab").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal("State", selected);
        Assert.Single(JSInterop.VerifyFocusAsyncInvoke(1));

        await tabs.Find("#tool-state-tab").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Assert.Equal("Problems", selected);
        Assert.Equal(2, JSInterop.VerifyFocusAsyncInvoke(2).Count());
    }
}
