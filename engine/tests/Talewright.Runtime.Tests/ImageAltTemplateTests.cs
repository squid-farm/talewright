using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;

namespace Talewright.Runtime.Tests;

public sealed class ImageAltTemplateTests
{
    [Fact]
    public void Rendered_alt_is_what_display_history_and_load_failure_share()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game:
                  id: alt-history
                  title: Alt history
                  version: 1
                  start: gate
                characters:
                  player: { first-name: Ada }
                  guide: { first-name: Bea }
                vars:
                  place: "South"
                """,
            ["main.tale"] = """
                === gate
                {image("portrait", "{guide:first-name} at the {var:place} gate")}
                * [Enter {image("portrait", "{var:place}")}] { var:place = "North" } -> later
                === later
                {image("portrait", "{var:place} gate")}
                * [Done] -> END
                """,
        }, ["images/portrait.png"]));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 1);
        Assert.Equal("Bea at the South gate", Alt(history.Current.Document));
        Assert.Equal("South", Alt(history.Current.Choices[0].Label));

        history.Choose("c0");
        Assert.Equal("Bea at the South gate", Alt(history.Transcript[0].Frame.Document));
        Assert.Equal("South", Alt(history.Transcript[0].Chosen!));
        Assert.Equal("North gate", Alt(history.Current.Document));

        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal("North gate", Alt(restored.Current.Document));
        Assert.Equal("Bea at the South gate", Alt(restored.Transcript[0].Frame.Document));
        restored.Back();
        Assert.Equal("Bea at the South gate", Alt(restored.Current.Document));
        Assert.Equal("South", Alt(restored.Current.Choices[0].Label));
    }

    private static string Alt(RenderedDocument document) =>
        Assert.Single(document.Blocks.OfType<RenderedParagraph>().SelectMany(paragraph => paragraph.Inlines).OfType<RenderedMedia>()).AltText;

    private static string Alt(IReadOnlyList<RenderedInline> inlines) =>
        Assert.Single(inlines.OfType<RenderedMedia>()).AltText;
}
