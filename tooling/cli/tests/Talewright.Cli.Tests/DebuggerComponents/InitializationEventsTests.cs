using Bunit;
using Talewright.Core.Events;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class InitializationEventsTests : BunitContext
{
    [Fact]
    public void Lists_only_events_before_the_first_scene_entry()
    {
        using var model = ComponentStory.Load();
        var count = model.Numbers.Single(field => field.Label == "var:count");
        count.Enabled = true;
        count.Value = "1";
        model.Start();
        Assert.Null(model.Error);
        ComponentStory.Choose(model, "Explore");
        var events = model.Session!.Events;
        var entry = events.ToList().FindIndex(item => item is SceneEntered);
        Assert.True(entry > 0, "The fixture must record initialization events before the first scene entry.");
        Assert.True(events.Count > entry + 1, "The fixture must record events after the first scene entry.");

        var initialization = Render<InitializationEvents>(parameters => parameters.Add(component => component.Session, model.Session));

        Assert.Equal("Initialization before target entry", initialization.Find("details > summary").TextContent);
        Assert.Equal(events.Take(entry).Select(item => item.ToString()),
            initialization.FindAll("ul > li").Select(item => item.TextContent));
    }
}
