using Talewright.Player.Library;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryImageDiscoveryTests
{
    [Fact]
    public void Finds_direct_incoming_outgoing_and_hub_scenes_without_evaluating_templates_or_neighbors()
    {
        var manifest = TestStories.DefaultManifest("image-neighborhood");
        manifest.Flag("ready");
        var paths = new[]
        {
            "images/incoming.png",
            "images/current.png",
            "images/then.png",
            "images/else.png",
            "images/inline-then.png",
            "images/inline-else.png",
            "images/styled.png",
            "images/choice.png",
            "images/hub-only.png",
            "images/deep.png",
        };
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = """
                === start
                @hub route
                {image("current.png", "Current")}
                * [Self] -> start
                * [Outgoing] -> outgoing
                * [Finish] -> END
                === predecessor
                {image("incoming.png", "Incoming")}
                -> start
                === outgoing
                @tags route
                {if flag:ready}
                {image("then.png", "Then")}
                {else}
                {image("else.png", "Else")}
                {/if}
                {if true}{image("inline-then.png", "Then")}{else}{image("inline-else.png", "Else")}{/if}
                **{image("styled.png", "Styled")}**
                * [Deep {image("choice.png", "Choice")}] -> deep
                === hub-only
                @tags route
                {image("hub-only.png", "Hub")}
                -> deep
                === deep
                {image("deep.png", "Not direct")}
                -> END
                """,
        }, paths);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));

        var groups = StoryImageDiscovery.ConnectedScenes(compilation.Story!, compilation.Story!.Start);

        Assert.Equal(["predecessor", "outgoing", "hub-only"], groups.Select(group => group.Scene.Value));
        Assert.Equal(["images/incoming.png"], groups[0].Paths);
        Assert.Equal([
            "images/then.png",
            "images/else.png",
            "images/inline-then.png",
            "images/inline-else.png",
            "images/styled.png",
            "images/choice.png",
        ], groups[1].Paths);
        Assert.Equal(["images/hub-only.png"], groups[2].Paths);
        Assert.DoesNotContain(groups.SelectMany(group => group.Paths), path => path == "images/deep.png");
        Assert.DoesNotContain(groups.SelectMany(group => group.Paths), path =>
            path == "images/current.png");
    }

    [Fact]
    public void Deduplicates_paths_across_connected_scenes_preserving_the_first_scene_occurrence()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                * [First] -> first
                * [Second] -> second
                * [Finish] -> END
                === first
                {image("shared.png", "First occurrence")}
                -> start
                === second
                {image("shared.png", "Second occurrence")}
                -> start
                """,
        }, ["images/shared.png"]);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));

        var groups = StoryImageDiscovery.ConnectedScenes(compilation.Story!, compilation.Story!.Start);

        Assert.Equal(["first", "second"], groups.Select(group => group.Scene.Value));
        Assert.Equal(["images/shared.png"], groups[0].Paths);
        Assert.Empty(groups[1].Paths);
    }

    [Fact]
    public void Finds_images_in_shown_text_for_choices_and_hub_offers()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                * [Visit] -> hub
                === hub
                @hub route
                * [Continue] (requires false, shown "Need {image(\"choice.png\", \"Choice\")}") -> END
                === offered
                @tags route
                @requires false, shown "Need {image(\"offer.png\", \"Offer\")}"
                -> END
                """,
        }, ["images/choice.png", "images/offer.png"]);
        var compilation = StoryCompiler.Compile(source);
        Assert.True(compilation.Success, string.Join("\n", compilation.Diagnostics));

        var groups = StoryImageDiscovery.ConnectedScenes(compilation.Story!, compilation.Story!.Start);

        Assert.Equal(["images/choice.png"], Assert.Single(groups).Paths);
        Assert.Equal(["images/offer.png"],
            StoryImageDiscovery.SceneImages(compilation.Story!, new("offered")));
    }
}
