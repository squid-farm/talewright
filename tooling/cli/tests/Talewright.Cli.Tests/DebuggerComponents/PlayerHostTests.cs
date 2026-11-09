using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Talewright.Debugger;
using Talewright.Player.Components;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class PlayerHostTests : BunitContext
{
    private readonly PlayerLaunchOptions _options = new("/stories/debugger-components");
    private readonly List<string> _titles = [];

    public PlayerHostTests()
    {
        _options.SetTitle = _titles.Add;
        Services.AddSingleton(_options);
        ComponentFactories.AddStub<TalewrightApp>();
    }

    [Fact]
    public void Starts_the_launched_story_without_the_library()
    {
        var host = Render<PlayerHost>();

        var app = host.FindComponent<Stub<TalewrightApp>>().Instance.Parameters;
        Assert.Equal("/stories/debugger-components", app.Get(component => component.AutoStartStoryId));
        Assert.False(app.Get(component => component.LibraryEnabled));
    }

    [Fact]
    public async Task Story_titles_become_the_window_title()
    {
        var host = Render<PlayerHost>();
        var titleChanged = host.FindComponent<Stub<TalewrightApp>>().Instance.Parameters.Get(component => component.OnTitleChanged);

        await host.InvokeAsync(() => titleChanged.InvokeAsync("Debugger components"));
        await host.InvokeAsync(() => titleChanged.InvokeAsync(null));

        Assert.Equal(["Debugger components", "Talewright"], _titles);
    }

    [Fact]
    public async Task Titles_are_ignored_without_a_window()
    {
        _options.SetTitle = null;
        var host = Render<PlayerHost>();
        var titleChanged = host.FindComponent<Stub<TalewrightApp>>().Instance.Parameters.Get(component => component.OnTitleChanged);

        await host.InvokeAsync(() => titleChanged.InvokeAsync("Debugger components"));

        Assert.Empty(_titles);
    }
}
