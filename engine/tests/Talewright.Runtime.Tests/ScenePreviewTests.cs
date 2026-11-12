using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class ScenePreviewTests
{
    [Fact]
    public void Preview_preserves_clear_metadata()
    {
        var story = Compile("""
            === start
            @clear
            A fresh screen.
            * [Stay] -> END
            """, CreateManifest());
        var session = new StorySession(story, 42);
        var live = session.Start();

        var preview = ScenePreview.Build(story, new SceneId("start"), session.State);

        Assert.True(live.ClearsPrevious);
        Assert.True(preview.ClearsPrevious);
    }

    [Fact]
    public void Preview_menu_matches_live_menu_for_choices_and_limited_hub_offers()
    {
        var manifest = CreateManifest();
        var story = Compile("""
            === start
            @hub shop
            @hub-limit 2
            Before {var:credits}.
            * [Available] -> END
            * [Hidden] (requires false) -> END
            * [Restricted] (requires false, shown "Need {var:credits}") -> END
            * -> END

            === shop-available
            @title Shop item
            @tags shop
            @priority 10
            -> END

            === shop-hidden
            @tags shop
            @priority 100
            @requires false
            -> END

            === shop-shown
            @title Later item
            @tags shop
            @priority 1
            @requires false, shown "Later"
            -> END
            """, manifest);
        var session = new StorySession(story, 42);
        var live = session.Start();

        var preview = ScenePreview.Build(story, new SceneId("start"), session.State);

        Assert.Equal(live.Menu.Select(entry => (entry.Key, entry.Text, entry.Number, entry.IsAvailable)),
            preview.Menu.Select(entry => (entry.Key, entry.Text, entry.Number, entry.IsAvailable)));
        Assert.Equal(
            [
                ("c0", "Available", 1, true),
                ("c2", "Need 10", 2, false),
                ("c3", "Continue", 3, true),
                ("storylet:shop-available", "Shop item", 4, true),
                ("storylet:shop-shown", "Later", 5, false),
            ],
            preview.Menu.Select(entry => (entry.Key, entry.Text, entry.Number, entry.IsAvailable)));
        Assert.Equal(live.Choices.Select(choice => choice.Key), preview.Choices.Select(choice => choice.Key));
        Assert.Equal("Before 10.", preview.Document.ToPlainText());
    }

    [Fact]
    public void Preview_does_not_change_state_or_run_entry_effects_or_checks()
    {
        var story = Compile("""
            === start
            ~ var:visits += 1
            ::: skill-check athletics dc 30
            success: { var:visits += 10 }
            failure: { var:visits += 20 }
            :::
            * [Stay] -> END
            """, CreateManifest());
        var log = new EventLog(story.World, 42);
        var state = log.State;
        var before = (
            state.CurrentScene,
            state.IsEnded,
            state.RollCount,
            state.VisitCount(new SceneId("start")),
            state.GetVariable(new VarId("visits")),
            state.GetStat(EntityId.Player, new StatId("athletics")));

        var preview = ScenePreview.Build(story, new SceneId("start"), state);

        Assert.Empty(log.Events);
        Assert.Equal(before, (
            state.CurrentScene,
            state.IsEnded,
            state.RollCount,
            state.VisitCount(new SceneId("start")),
            state.GetVariable(new VarId("visits")),
            state.GetStat(EntityId.Player, new StatId("athletics"))));
        Assert.DoesNotContain(log.Events, gameEvent => gameEvent is SkillCheckResolved);
        Assert.Equal(["Stay"], preview.Menu.Select(entry => entry.Text));
    }

    private static ManifestFaker CreateManifest()
    {
        var manifest = new ManifestFaker("scene-preview", "Scene preview", "start", 42)
        {
            BaseRuleset = "rpg",
            Randomness = "off",
        };
        manifest.Character("player", "Ada", stats: new Dictionary<string, int> { ["athletics"] = 2 });
        manifest.Number("credits", 10);
        manifest.Number("visits", 0);
        return manifest;
    }

    private static StoryDefinition Compile(string tale, ManifestFaker manifest)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }
}
