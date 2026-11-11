using Talewright.Core.Identity;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class StoryMapTests
{
    private static DirectoryInfo RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
            root = root.Parent;
        return root ?? throw new DirectoryNotFoundException("Could not find the Talewright repository root.");
    }

    private static string Snapshot(StoryMap map)
    {
        var lines = map.Nodes.Select(node => node.IsEnd
            ? $"end|{node.Id}|reachable={node.IsReachable.ToString().ToLowerInvariant()}"
            : $"scene|{node.Id}|{node.Title ?? "-"}|tags={string.Join(",", node.Tags)}|reachable={node.IsReachable.ToString().ToLowerInvariant()}");
        var diverts = map.Diverts.Select(divert =>
            $"divert|{divert.Id}|{divert.From}|{divert.Target}|{divert.Kind}|{divert.Outcome?.ToString() ?? "-"}|{divert.Order}|{divert.Condition.Text}|reads={string.Join(",", divert.Condition.Reads.Select(read => read.Reference))}");
        var offers = map.HubOffers.Select(offer =>
            $"hub-offer|{offer.Id}|{offer.Hub}|{offer.Scene}|{(offer.Tags.MatchAll ? "all" : "any")}({string.Join(",", offer.Tags.Tags)})|{offer.Condition.Text}|once={offer.Once.ToString().ToLowerInvariant()}|reads={string.Join(",", offer.Condition.Reads.Select(read => read.Reference))}");
        return string.Join("\n", lines.Concat(diverts).Concat(offers)) + "\n";
    }

    [Fact]
    public void Public_map_api_matches_the_LostRelic_golden_snapshot()
    {
        var source = StorySource.FromDirectory(Path.Combine(RepositoryRoot().FullName, "samples", "LostRelic"));
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = Assert.IsType<StoryDefinition>(result.Story);

        var map = StoryMap.Build(story);
        Assert.Equal(story.Scenes.Count + 1, map.Nodes.Count);
        Assert.True(map.Nodes[^1].IsEnd);
        var snapshotPath = Path.Combine(
            RepositoryRoot().FullName,
            "engine", "tests", "Talewright.Talemark.Tests", "Snapshots", "lost-relic-story-map.txt");
        Assert.Equal(File.ReadAllText(snapshotPath), Snapshot(map));
    }

    [Fact]
    public void Public_map_keeps_declaration_order_and_ignores_condition_truth_for_reachability()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                * [Unavailable] (requires false) -> gated
                === gated
                -> END
                === orphan
                -> start
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var map = StoryMap.Build(result.Story!);

        Assert.Equal(["start", "gated", "orphan", "END"], map.Nodes.Select(node => node.Id));
        Assert.Equal([true, true, false, true], map.Nodes.Select(node => node.IsReachable));
        Assert.Equal(["gated", "END", "start"], map.Diverts.Select(divert => divert.Target.ToString()));
        Assert.Equal("false", Assert.Single(map.Diverts, divert => divert.Target.Scene?.Value == "gated").Condition.Text);
        Assert.Empty(map.HubOffers);
    }

    [Fact]
    public void Public_map_follows_hub_offers_and_does_not_reach_END_from_an_unreachable_scene()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = TestStories.DefaultYaml,
            ["main.tale"] = """
                === start
                @hub camp
                * [Stay] -> start
                === camp
                @tags camp
                -> loop
                === loop
                -> camp
                === orphan
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var map = StoryMap.Build(result.Story!);

        Assert.Equal(["start", "camp", "loop", "orphan", "END"], map.Nodes.Select(node => node.Id));
        Assert.Equal([true, true, true, false, false], map.Nodes.Select(node => node.IsReachable));
        Assert.Equal("start/hub:camp", Assert.Single(map.HubOffers).Id);
    }

    [Fact]
    public void Public_map_connections_include_ordered_diverts_hub_offers_and_endpoints()
    {
        var manifest = TestStories.DefaultManifest();
        manifest.Flag("ready");
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = """
                === start
                @hub path
                {if flag:ready}
                -> road
                {/if}
                ::: skill-check athletics dc 5
                success: -> road
                failure: { set flag:ready }
                :::
                * [Again] -> start
                * [Travel] -> road
                * [Force] skill-check athletics dc 5
                  success: -> road
                  failure: -> camp
                === camp
                @tags path
                @hub path
                * [Travel] -> road
                === road
                @tags path
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var map = StoryMap.Build(result.Story!);
        var startId = new SceneId("start");
        var roadId = new SceneId("road");
        var start = map.Connections(startId);
        var road = map.Connections(roadId);

        Assert.Equal([
            "Scene:road",
            "EntryCheck:Success:road",
            "Choice:start",
            "Choice:road",
            "ChoiceSkillCheck:Success:road",
            "ChoiceSkillCheck:Failure:camp",
            "HubOffer:camp",
            "HubOffer:road",
        ], start.Outgoing.Select(Describe));
        Assert.Equal([
            "start:Scene",
            "start:EntryCheck:Success",
            "start:Choice",
            "start:ChoiceSkillCheck:Success",
            "start:HubOffer",
            "camp:Choice",
            "camp:HubOffer",
        ], road.Incoming.Select(DescribeIncoming));

        var selfDivert = Assert.Single(start.Incoming);
        var selfDivertItem = Assert.IsType<StoryConnection.DivertConnection>(selfDivert);
        Assert.Equal(startId, selfDivertItem.Divert.Target.Scene);
        Assert.Contains(start.Outgoing, connection => connection is StoryConnection.DivertConnection divert
            && divert.Divert.Target.Scene == startId);
        Assert.Contains(road.Outgoing, connection => connection is StoryConnection.DivertConnection divert
            && divert.Divert.Target.IsEnd);
        Assert.Contains(road.Incoming, connection => connection is StoryConnection.HubOfferConnection);
        Assert.Same(start, map.Connections(startId));

        // The compiler rejects dead-end scenes, so an unindexed scene id exercises the empty-connection case.
        var unconnected = map.Connections(new SceneId("unconnected"));
        Assert.Empty(unconnected.Incoming);
        Assert.Empty(unconnected.Outgoing);
        Assert.Same(unconnected, map.Connections(new SceneId("unconnected")));

        static string Describe(StoryConnection connection) => connection switch
        {
            StoryConnection.DivertConnection divert =>
                $"{divert.Divert.Kind}{(divert.Divert.Outcome is { } outcome ? $":{outcome}" : "")}:{divert.Divert.Target}",
            StoryConnection.HubOfferConnection offer => $"HubOffer:{offer.Offer.Scene}",
            _ => throw new ArgumentOutOfRangeException(nameof(connection)),
        };

        static string DescribeIncoming(StoryConnection connection) => connection switch
        {
            StoryConnection.DivertConnection divert =>
                $"{divert.Divert.From}:{divert.Divert.Kind}{(divert.Divert.Outcome is { } outcome ? $":{outcome}" : "")}",
            StoryConnection.HubOfferConnection offer => $"{offer.Offer.Hub}:HubOffer",
            _ => throw new ArgumentOutOfRangeException(nameof(connection)),
        };
    }
}
