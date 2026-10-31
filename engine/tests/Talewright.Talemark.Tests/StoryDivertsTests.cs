using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StoryDivertsTests
{
    private static CompilationResult Compile(string tale)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = TestStories.DefaultYaml + "\nflags: [ready]\n",
            ["main.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }

    private static string Describe(StoryDivert divert) =>
        $"{divert.Id} #{divert.Order} {divert.Kind}{(divert.Outcome is { } outcome ? $" {outcome}" : "")} -> {divert.Target}" +
        (divert.ChoiceKey is null ? "" : $" {divert.ChoiceKey} label:{(divert.ChoiceLabel is null ? "none" : "yes")}" +
            $" requires:{(divert.ChoiceRequires is null ? "no" : "yes")}") +
        (divert.BlockConditions.Count == 0 ? "" : " in:" + string.Join(",", divert.BlockConditions.Select(block => block.InElse ? "else" : "then")));

    [Fact]
    public void Lists_every_divert_kind_in_source_order_with_ids_choices_and_enclosing_blocks()
    {
        var result = Compile("""
            === start
            {if flag:ready}
            -> start
            {else}
            {if var:visits > 1}
            -> road
            {/if}
            {/if}
            ::: skill-check athletics dc 5
            success: -> road
            failure: { set flag:ready }
            :::
            * [Go] (requires: flag:ready) -> road
            * -> road
            * [Climb] skill-check athletics dc 10
              success: -> END
              failure: -> road
            === road
            -> END
            -> start
            """);
        Assert.Equal(["TW3012"], result.Diagnostics.Select(diagnostic => diagnostic.Code));

        var diverts = StoryDiverts.From(result.Story!).Diverts;

        Assert.Equal("""
            start/step:3:1 #0 Scene -> start in:then
            start/step:6:1 #1 Scene -> road in:else,then
            start/step:9:1/success #2 EntryCheck Success -> road
            start/step:13:1 #3 Choice -> road c0 label:yes requires:yes
            start/step:14:1 #4 Choice -> road c1 label:none requires:no
            start/step:15:1/success #5 ChoiceSkillCheck Success -> END c2 label:yes requires:no
            start/step:15:1/failure #6 ChoiceSkillCheck Failure -> road c2 label:yes requires:no
            road/step:19:1 #0 Scene -> END
            """.ReplaceLineEndings("\n"), string.Join("\n", diverts.Select(Describe)));
        Assert.All(diverts, divert => Assert.Equal(divert.Id.Split('/')[1], divert.StepId));

        var start = result.Story!.Scenes[0];
        var outer = Assert.IsType<ConditionalStep>(start.Steps[0]);
        var inner = Assert.IsType<ConditionalStep>(Assert.Single(outer.Else));
        Assert.Same(outer.Condition, diverts[0].BlockConditions[0].Condition);
        Assert.Equal([outer.Condition, inner.Condition], diverts[1].BlockConditions.Select(block => block.Condition));
        Assert.Same(start.Choices[0].Requires, diverts[3].ChoiceRequires);
        Assert.Same(start.Choices[0].Label, diverts[3].ChoiceLabel);
        Assert.Equal(new StoryDivert[] { diverts[3], diverts[4] }, start.Steps.OfType<ChoiceStep>().Take(2)
            .Select(step => diverts.Single(divert => divert.StepId == step.Id)));
    }

    [Fact]
    public void Separate_diverts_between_the_same_scenes_self_diverts_and_end_are_kept()
    {
        var result = Compile("""
            === start
            * [Again] -> start
            * [Road] -> road
            * [Road too] -> road
            * [Stop] -> END
            === road
            * [Stop] -> END
            * -> start
            """);

        var diverts = StoryDiverts.From(result.Story!).Diverts;

        Assert.Equal(2, diverts.Count(divert => divert.From.Value == "start" && divert.Target.Scene?.Value == "road"));
        Assert.Contains(diverts, divert => divert.From.Value == "start" && divert.Target.Scene?.Value == "start");
        Assert.All(diverts.Where(divert => divert.Target.IsEnd), divert => Assert.Equal(DivertTarget.End, divert.Target));
        Assert.Equal(diverts.Count, diverts.Select(divert => divert.Id).Distinct().Count());
        Assert.Equal(SceneSteps.Targets(result.Story!.Scenes[0].Steps), diverts.Where(divert => divert.From.Value == "start")
            .Select(divert => divert.Target));
    }

    [Fact]
    public void Lines_that_can_never_run_produce_no_divert()
    {
        var result = Compile("""
            === start
            {if true}
            -> road
            -> hidden
            {else}
            -> hidden
            {/if}
            -> hidden
            === road
            -> END
            === hidden
            -> END
            """);

        var diverts = StoryDiverts.From(result.Story!).Diverts;

        Assert.Equal(["road", "END", "END"], diverts.Select(divert => divert.Target.ToString()));
    }

    [Fact]
    public void Hub_offers_every_matching_scene_except_itself_ignoring_the_hub_limit()
    {
        var result = Compile("""
            === start
            @tags camp
            @hub camp
            @hub-limit 1
            * -> END
            === well
            @tags camp
            -> start
            === fire
            @tags camp, night
            -> start
            === night
            @hub all(camp, night)
            * -> END
            === empty
            @hub nowhere
            * -> END
            === leaving
            @hub camp
            -> start
            """);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TW3008");

        var offers = StoryDiverts.From(result.Story!).HubOffers;

        Assert.Equal(["start/hub:well", "start/hub:fire", "night/hub:fire"], offers.Select(offer => offer.Id));
        Assert.Equal(["start", "start", "night"], offers.Select(offer => offer.Hub.Value));
        Assert.Equal(["well", "fire", "fire"], offers.Select(offer => offer.Scene.Value));
        Assert.Same(result.Story!.GetScene(new("night")).Hub, offers[2].Tags);
    }

    [Fact]
    public void Divert_ids_are_unique_in_every_sample_story()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var lostRelic = StoryCompiler.Compile(StorySource.FromDirectory(Path.Combine(root.FullName, "samples", "LostRelic")));
        Assert.True(lostRelic.Success, string.Join("\n", lostRelic.Diagnostics));

        foreach (var story in TestStories.All.Select(fixture => fixture.Compile()).Prepend(lostRelic.Story!))
        {
            var map = StoryDiverts.From(story);
            Assert.NotEmpty(map.Diverts);
            Assert.Equal(map.Diverts.Count, map.Diverts.Select(divert => divert.Id).Distinct().Count());
            Assert.Equal(map.HubOffers.Count, map.HubOffers.Select(offer => offer.Id).Distinct().Count());
        }
    }
}
