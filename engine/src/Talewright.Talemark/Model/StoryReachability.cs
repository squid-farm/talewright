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

    private StoryReachability(StoryDiverts diverts, Graph<SceneId, StoryRoute> graph, Dictionary<SceneId, VertexId> vertices,
        ReachabilityResult result)
    {
        Diverts = diverts;
        Graph = graph;
        _vertices = vertices;
        _result = result;
    }

    public StoryDiverts Diverts { get; }

    /// <summary>One vertex per scene in declaration order, with one entry per divert to a scene and per hub offer.</summary>
    public Graph<SceneId, StoryRoute> Graph { get; }

    public static StoryReachability Find(IReadOnlyList<SceneDefinition> scenes, StoryDiverts diverts, SceneId start,
        CancellationCheckpoint? checkpoint = null)
    {
        var cancellationToken = checkpoint?.Token ?? CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new GraphBuilder<SceneId, StoryRoute>();
        var vertices = new Dictionary<SceneId, VertexId>();
        foreach (var scene in scenes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!vertices.ContainsKey(scene.Id))
                vertices[scene.Id] = builder.AddVertex(scene.Id);
        }

        foreach (var divert in diverts.Diverts)
        {
            checkpoint?.Check();
            if (divert.Target.Scene is { } target && vertices.TryGetValue(target, out var to))
                builder.AddEdge(vertices[divert.From], to, new StoryRoute(divert, null));
        }

        foreach (var offer in diverts.HubOffers)
        {
            checkpoint?.Check();
            builder.AddEdge(vertices[offer.Hub], vertices[offer.Scene], new StoryRoute(null, offer));
        }

        var graph = builder.Build(cancellationToken);
        VertexId[] roots = vertices.TryGetValue(start, out var root) ? [root] : [];
        var result = GraphReachability.Find(graph, roots, GraphDirection.Outgoing, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new StoryReachability(diverts, graph, vertices, result);
    }

    public bool IsReached(SceneId scene) => _vertices.TryGetValue(scene, out var vertex) && _result.IsReached(vertex);
}
