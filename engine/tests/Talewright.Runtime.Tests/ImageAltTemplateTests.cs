using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class ImageAltTemplateTests
{
    [Fact]
    public void Rendered_alt_is_what_display_history_and_load_failure_share()
    {
        var manifest = new ManifestFaker("alt-history", "Alt history", "gate");
        manifest.Character("player", "Ada");
        manifest.Character("guide", "Bea");
        manifest.Text("place", "South");
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("gate"),
                ImageFaker.Call("portrait", $"{SubstitutionFaker.Character(new TaleCharacterRef("guide"), "first-name")} at the {SubstitutionFaker.Variable(new TaleVariableRef("place"))} gate"),
                ChoiceFaker.Create("later",
                    $"Enter {ImageFaker.Call("portrait", SubstitutionFaker.Variable(new TaleVariableRef("place")))}",
                    effect: EffectFaker.Assign(new TaleVariableRef("place"), "\"North\"")),
                SceneFaker.Header("later"),
                ImageFaker.Call("portrait", $"{SubstitutionFaker.Variable(new TaleVariableRef("place"))} gate"),
                ChoiceFaker.Create("END", "Done")),
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
