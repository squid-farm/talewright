using Talewright.Core.Identity;
using Talewright.Graphs;

namespace Talewright.Talemark.Model;

/// <summary>A divert or hub offer that leads from one scene to another.</summary>
internal readonly record struct StoryRoute(StoryDivert? Divert, HubOffer? HubOffer);

/// <summary>
/// Scenes reachable from a start scene by following <see cref="StoryDiverts"/>: every divert and hub offer.
/// It doesn't check whether a player can meet a divert's conditions. TW3009 reports the scenes it doesn't reach.
/// </summary>
internal sealed class StoryReachability
{
    private readonly Dictionary<SceneId, VertexId> _vertices;
    private readonly ReachabilityResult _result;

    private StoryReachability(Graph<SceneId, StoryRoute> graph, Dictionary<SceneId, VertexId> vertices, ReachabilityResult result)
    {
        Graph = graph;
        _vertices = vertices;
        _result = result;
    }

    /// <summary>One vertex per scene in declaration order, with one entry per divert to a scene and per hub offer.</summary>
    public Graph<SceneId, StoryRoute> Graph { get; }

    public static StoryReachability Find(IReadOnlyList<SceneDefinition> scenes, StoryDiverts diverts, SceneId start)
    {
        var builder = new GraphBuilder<SceneId, StoryRoute>();
        var vertices = new Dictionary<SceneId, VertexId>();
        foreach (var scene in scenes)
        {
            if (!vertices.ContainsKey(scene.Id))
                vertices[scene.Id] = builder.AddVertex(scene.Id);
        }

        foreach (var divert in diverts.Diverts)
        {
            if (divert.Target.Scene is { } target && vertices.TryGetValue(target, out var to))
                builder.AddEdge(vertices[divert.From], to, new StoryRoute(divert, null));
        }

        foreach (var offer in diverts.HubOffers)
            builder.AddEdge(vertices[offer.Hub], vertices[offer.Scene], new StoryRoute(null, offer));

        var graph = builder.Build();
        VertexId[] roots = vertices.TryGetValue(start, out var root) ? [root] : [];
        return new StoryReachability(graph, vertices, GraphReachability.Find(graph, roots));
    }

    public bool IsReached(SceneId scene) => _vertices.TryGetValue(scene, out var vertex) && _result.IsReached(vertex);
}
