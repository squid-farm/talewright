using AngleSharp.Dom;
using Bunit;
using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class SourceViewNavigationTests : BunitContext
{
    private static readonly string LargeText = string.Join("\n", Enumerable.Range(1, 250).Select(i => $"// line {i}"));

    [Fact]
    public void Pages_backwards_and_forwards_and_returns_to_the_scene()
    {
        var view = Show(new AcceptedSceneSource(new("middle"), new("large.tale", 120, 130), LargeText));
        Assert.Equal("Showing accepted lines 117 to 216 of 250", Caption(view));

        Button(view, "Previous source lines").Click();
        Assert.Equal("Showing accepted lines 17 to 116 of 250", Caption(view));
        Assert.Empty(view.FindAll("tr.source-scene"));

        Button(view, "Previous source lines").Click();
        Assert.Equal("Showing accepted lines 1 to 100 of 250", Caption(view));
        Assert.True(Button(view, "Previous source lines").HasAttribute("disabled"));

        Button(view, "Next source lines").Click();
        Button(view, "Next source lines").Click();
        Assert.Equal("Showing accepted lines 201 to 250 of 250", Caption(view));
        Assert.Equal(50, view.FindAll("tbody tr").Count);
        Assert.True(Button(view, "Next source lines").HasAttribute("disabled"));
        Assert.False(Button(view, "Previous source lines").HasAttribute("disabled"));

        Button(view, "Go to current scene source").Click();
        Assert.Equal("Showing accepted lines 117 to 216 of 250", Caption(view));
        Assert.Equal(11, view.FindAll("tr.source-scene").Count);
    }

    [Fact]
    public void A_short_file_cannot_page()
    {
        var view = Show(new AcceptedSceneSource(new("only"), new("short.tale", 1, 2), "=== only\nText."));

        Assert.Equal("Showing accepted lines 1 to 2 of 2", Caption(view));
        Assert.True(Button(view, "Previous source lines").HasAttribute("disabled"));
        Assert.True(Button(view, "Next source lines").HasAttribute("disabled"));
    }

    [Fact]
    public void A_new_source_resets_navigation_but_the_same_source_keeps_it()
    {
        var source = new AcceptedSceneSource(new("middle"), new("large.tale", 120, 130), LargeText);
        var view = Show(source);
        Button(view, "Next source lines").Click();
        Assert.Equal("Showing accepted lines 217 to 250 of 250", Caption(view));

        view.Render(parameters => parameters.Add(component => component.Source, source).Add(component => component.Pending, true));
        Assert.Equal("Showing accepted lines 217 to 250 of 250", Caption(view));

        view.Render(parameters => parameters.Add(component => component.Source,
            new AcceptedSceneSource(new("first"), new("large.tale", 2, 4), LargeText)));
        Assert.Equal("Showing accepted lines 1 to 100 of 250", Caption(view));
        Assert.Equal(["2", "3", "4"], view.FindAll("tr.source-scene th").Select(cell => cell.TextContent));
    }

    [Fact]
    public void Each_navigation_replaces_the_scroll_window()
    {
        var view = Show(new AcceptedSceneSource(new("middle"), new("large.tale", 120, 130), LargeText));
        var window = view.Find(".source-window");

        Button(view, "Previous source lines").Click();

        // A fresh keyed window resets its scroll position instead of keeping the old offset.
        Assert.NotSame(window, view.Find(".source-window"));
    }

    private IRenderedComponent<SourceView> Show(AcceptedSceneSource source) =>
        Render<SourceView>(parameters => parameters.Add(component => component.Source, source));

    private static string Caption(IRenderedComponent<SourceView> view) => view.Find("caption").TextContent;

    private static IElement Button(IRenderedComponent<SourceView> view, string text) =>
        view.FindAll("button").Single(button => button.TextContent == text);
}
