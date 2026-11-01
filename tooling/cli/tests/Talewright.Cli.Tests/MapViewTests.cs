using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Debugger;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Runtime;

namespace Talewright.Cli.Tests;

public sealed class MapViewTests
{
    private const string Yaml = """
        game:
          id: map
          title: Map test
          start: start
          randomness: off
        flags: [ready, lit]
        vars: {visits: 0}
        characters:
          player: {first-name: Mara, stats: {athletics: 2}}
        """;

    private const string Tale = """
        === start
        @title The gate
        ::: skill-check athletics dc 5
        success: -> road
        failure: { set flag:ready }
        :::
        {if flag:ready and var:visits > 1000}
        -> camp
        {/if}
        * [Wait] -> END
        * [Climb] skill-check athletics dc 10
          success: -> road
          failure: -> END
        === road
        @title Road
        * [Rest] -> camp
        * [Rest] -> camp
        === camp
        @title Camp
        @hub fire
        * [Leave] -> END
        === well
        @title Well
        @tags fire
        @requires flag:lit
        @once
        -> camp
        === hearth
        @tags fire
        -> camp
        -> start
        === orphan
        @title Orphan
        * [Back] -> camp
        * [Quit] -> END
        === ruin
        @hub fire
        Ruins.
        """;

    private static MapView Build(string tale = Tale)
        => MapView.Build(CompileStory(tale));

    private static StoryDefinition CompileStory(string tale, string yaml = Yaml)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml,
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    private static string Lines(MapView view) => string.Join("\n", view.Lines.Select(line =>
        $"{line.Id} {line.From} -> {line.To} [{line.Area}] {line.Kind}{(line.Outcome is null ? "" : $" {line.Outcome}")} \"{line.Label}\""));

    [Fact]
    public void Shows_every_scene_then_END_and_every_divert_with_a_short_label()
    {
        var view = Build();

        Assert.Equal("Map test", view.Title);
        Assert.Equal(["start", "road", "camp", "well", "hearth", "orphan", "ruin", "END"],
            view.Nodes.Where(node => !node.Stub).Select(node => node.Id));
        var end = Assert.Single(view.Nodes, node => node.End);
        Assert.Equal(["END"], end.Text);
        Assert.Equal("END", end.Name);
        Assert.True(Assert.Single(view.Nodes, node => node.Id == "start").Start);
        Assert.Equal(["camp", "Camp"], Assert.Single(view.Nodes, node => node.Id == "camp").Text);
        Assert.Equal(["well", "Well", "requires flag:lit"], Assert.Single(view.Nodes, node => node.Id == "well").Text);
        Assert.Equal("""
            d0 start -> road [main] EntryCheck Success "entry ✓"
            d1 start -> camp [main] Scene "flag:ready and var:visits…"
            d2 start -> END [main] Choice ""
            d3 start -> road [main] ChoiceSkillCheck Success "✓"
            d4 start -> END [main] ChoiceSkillCheck Failure "✗"
            d5 road -> camp [main] Choice "×2"
            d6 camp -> END [main] Choice ""
            d7 well -> camp [main] Scene ""
            d8 hearth -> camp [main] Scene ""
            d9 orphan -> stub:camp [unreachable] Choice ""
            d10 orphan -> stub:END [unreachable] Choice ""
            o11 camp -> group:main:fire [main] HubOffer "offers #fire (2)"
            o12 ruin -> stub:well [unreachable] HubOffer "offers #fire"
            o13 ruin -> stub:hearth [unreachable] HubOffer "offers #fire"
            """, Lines(view));
    }

    [Fact]
    public void Shows_the_full_condition_in_each_divert_and_hub_offer_description()
    {
        var view = Build();

        var start = Assert.Single(view.Nodes, node => node.Id == "start");
        Assert.Equal([
            "Entry check on success to road, main.tale line 3",
            "Divert to camp, if flag:ready and var:visits > 1000, main.tale line 8",
            "Choice c0 “Wait” to END, main.tale line 10",
            "Choice skill check c1 “Climb” on success to road, main.tale line 11",
            "Choice skill check c1 “Climb” on failure to END, main.tale line 11",
        ], start.Items.Select(item => item.Name));
        Assert.Equal(["start/step:3:1/success", "start/step:8:1", "start/step:10:1", "start/step:11:1/success", "start/step:11:1/failure"],
            start.Items.Select(item => item.Id));
        Assert.Equal(["road", "camp", "END", "road", "END"], start.Items.Select(item => item.Target));
        Assert.Equal(start.Items.Select(item => item.Name), start.Items.Select(item =>
            Assert.Single(view.Lines, line => line.Id == item.Line).Items.Single()));

        var road = Assert.Single(view.Lines, line => line.Id == "d5");
        Assert.Equal(["Choice c0 “Rest” to camp, main.tale line 16", "Choice c1 “Rest” to camp, main.tale line 17"], road.Items);
    }

    [Fact]
    public void Groups_hub_offered_scenes_by_tag_and_describes_each_hub_offer()
    {
        var view = Build();

        Assert.Equal([new MapGroup("group:main:fire", "main", "#fire")], view.Groups);
        Assert.Equal(["well", "hearth"], view.Nodes.Where(node => node.Group == "group:main:fire").Select(node => node.Id));
        var camp = Assert.Single(view.Nodes, node => node.Id == "camp");
        Assert.True(camp.Hub);
        Assert.Equal("Camp (camp), hub", camp.Name);
        Assert.Equal([
            ("camp/hub:well", "o11", "well", "Hub offer of Well (well) via #fire, once, if flag:lit"),
            ("camp/hub:hearth", "o11", "hearth", "Hub offer of hearth via #fire"),
        ], camp.Items.Skip(1).Select(item => (item.Id, item.Line, item.Target, item.Name)));
        Assert.Equal(camp.Items.Skip(1).Select(item => item.Name), Assert.Single(view.Lines, line => line.Id == "o11").Items);
    }

    [Fact]
    public void Puts_unreachable_scenes_in_their_own_area_with_stubs_for_reachable_targets()
    {
        var view = Build();

        Assert.Equal(2, view.UnreachableScenes);
        Assert.Equal(["orphan", "ruin"], view.Nodes.Where(node => node.Area == MapView.UnreachableArea && !node.Stub).Select(node => node.Id));
        Assert.Equal("Orphan (orphan), unreachable", Assert.Single(view.Nodes, node => node.Id == "orphan").Name);
        Assert.Equal([("stub:camp", "→ camp"), ("stub:END", "→ END"), ("stub:well", "→ well"), ("stub:hearth", "→ hearth")],
            view.Nodes.Where(node => node.Stub).Select(node => (node.Id, node.Text.Single())));
        Assert.All(view.Nodes.Where(node => node.Stub), node => Assert.Equal(MapView.UnreachableArea, node.Area));
        Assert.Equal(["camp", "END"], Assert.Single(view.Nodes, node => node.Id == "orphan").Items.Select(item => item.Target));
        Assert.Equal(["well", "hearth"], Assert.Single(view.Nodes, node => node.Id == "ruin").Items.Select(item => item.Target));

        var areas = view.Nodes.ToDictionary(node => node.Id, node => node.Area);
        foreach (var group in view.Groups)
            areas[group.Id] = group.Area;
        Assert.All(view.Lines, line => Assert.Equal((line.Area, line.Area), (areas[line.From], areas[line.To])));
    }

    [Fact]
    public void Puts_END_in_the_unreachable_area_when_only_unreachable_scenes_divert_to_it()
    {
        var view = Build("""
            === start
            -> start
            === orphan
            -> END
            """);

        var end = Assert.Single(view.Nodes, node => node.End);
        Assert.Equal((MapView.UnreachableArea, "END, unreachable"), (end.Area, end.Name));
        Assert.Equal("d1 orphan -> END [unreachable] Scene \"\"", Lines(view).Split('\n')[1]);
    }

    [Fact]
    public void Omits_diverts_on_lines_that_cannot_run()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Yaml,
            ["main.tale"] = Tale,
        }));
        Assert.Contains("TW3012", result.Diagnostics.Select(diagnostic => diagnostic.Code));

        var view = MapView.Build(result.Story!);

        var hearth = Assert.Single(view.Nodes, node => node.Id == "hearth");
        Assert.Equal(["camp"], hearth.Items.Select(item => item.Target));
        Assert.DoesNotContain(view.Lines, line => line.From == "hearth" && line.To == "start");
    }

    [Fact]
    public void Current_run_tracks_taken_check_diverts_and_hub_offers_in_order_and_replays_undo_redo()
    {
        var story = CompileStory("""
            === start
            * [Choose] skill-check athletics dc 12
              success: -> road
              failure: -> road
            === road
            @hub fire
            * [Finish] -> END
            === camp
            @tags fire
            -> END
            """, Yaml.Replace("randomness: off", "randomness: checks-only", StringComparison.Ordinal));
        var map = MapView.Build(story);
        var outcomes = new HashSet<DecisionReason>();

        foreach (var seed in Enumerable.Range(1, 200).Select(value => (ulong)value))
        {
            var session = AuthorSession.Create(story, new SceneId("start"), captureDecisions: true, seed: seed);
            Assert.Equal(["start"], MapRunView.Build(story, map, session).Steps.Select(step => step.Scene));

            session.Choose("c0");
            var outcome = Assert.Single(session.Decisions, decision => decision.Kind == DecisionKind.CheckOutcome);
            var route = MapRunView.Build(story, map, session);
            Assert.Equal(["start", "road"], route.Steps.Select(step => step.Scene));
            Assert.Equal("road", route.CurrentScene);
            var selected = route.Steps[1];
            var outcomeName = outcome.Reason == DecisionReason.CheckSucceeded ? "success" : "failure";
            Assert.Contains($"on {outcomeName}", selected.TakenItemName);
            Assert.Equal(selected.LineId, Assert.Single(map.Nodes.SelectMany(node => node.Items),
                item => item.Id == selected.TakenItemId).Line);
            outcomes.Add(outcome.Reason);

            session.Choose($"{StorySession.StoryletKeyPrefix}camp");
            route = MapRunView.Build(story, map, session);
            Assert.Equal(["start", "road", "camp", "END"], route.Steps.Select(step => step.Scene));
            Assert.Equal("road/hub:camp", route.Steps[2].TakenItemId);
            Assert.True(route.IsEnded);

            session.Back();
            route = MapRunView.Build(story, map, session);
            Assert.Equal(["start", "road"], route.Steps.Select(step => step.Scene));
            Assert.Equal("road", route.CurrentScene);

            session.Forward();
            route = MapRunView.Build(story, map, session);
            Assert.Equal(["start", "road", "camp", "END"], route.Steps.Select(step => step.Scene));
            Assert.True(route.IsEnded);
            if (outcomes.Contains(DecisionReason.CheckSucceeded) && outcomes.Contains(DecisionReason.CheckFailed))
                break;
        }

        Assert.Contains(DecisionReason.CheckSucceeded, outcomes);
        Assert.Contains(DecisionReason.CheckFailed, outcomes);
    }

    [Fact]
    public void Current_run_marks_a_mid_story_start()
    {
        var story = CompileStory("""
            === start
            -> road
            === road
            * [Finish] -> END
            """);
        var session = AuthorSession.Create(story, new SceneId("road"), captureDecisions: true, seed: 42);

        var route = MapRunView.Build(story, MapView.Build(story), session);

        Assert.Equal("road", route.StartScene);
        Assert.True(route.StartedMidStory);
        Assert.Equal(["road"], route.Steps.Select(step => step.Scene));
    }

    [Fact]
    public void Scene_preview_renders_story_start_values_and_current_run_state_without_running_effects()
    {
        var story = CompileStory("""
            === start
            ~ var:visits += 1
            {if flag:ready}
            Ready {var:visits}.
            {else}
            Waiting {var:visits}.
            {/if}
            * [Prepared {var:visits}] (requires: flag:ready) -> END
            * [Wait] -> END
            """);
        var start = new SceneId("start");
        var initial = GameState.CreateInitial(story.World, 42);
        var startPreview = MapScenePreviewBuilder.Build(story, start, initial, usesCurrentRunState: false);

        Assert.Contains("Waiting 0.", startPreview.Document.ToPlainText());
        Assert.Equal(["Wait"], startPreview.Choices.Select(choice => choice.Text));
        Assert.False(startPreview.UsesCurrentRunState);

        var session = AuthorSession.Create(story, start, captureDecisions: true,
            setup: new AuthorSetup
            {
                Flags = new Dictionary<FlagId, bool> { [new("ready")] = true },
                Variables = new Dictionary<VarId, int> { [new("visits")] = 2 },
            }, seed: 42);
        var currentPreview = MapScenePreviewBuilder.Build(story, start, session.State, usesCurrentRunState: true);

        Assert.Contains("Ready 3.", currentPreview.Document.ToPlainText());
        Assert.Equal(["Prepared 3", "Wait"], currentPreview.Choices.Select(choice => choice.Text));
        Assert.True(currentPreview.UsesCurrentRunState);
    }
}
