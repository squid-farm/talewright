using System.Collections.Immutable;

namespace Talewright.Graphs;

/// <summary>The classification of a directed edge during depth-first search.</summary>
public enum DepthFirstEdgeKind
{
    /// <summary>First edge to discover its target.</summary>
    Tree,

    /// <summary>Points to a vertex still on the active DFS stack.</summary>
    Back,

    /// <summary>Points to a finished descendant.</summary>
    Forward,

    /// <summary>Points to another finished vertex.</summary>
    Cross
}

/// <summary>An edge id and its depth-first classification.</summary>
public readonly record struct DepthFirstEdgeClassification(EdgeId Edge, DepthFirstEdgeKind Kind);

/// <summary>The immutable traversal and edge classifications from depth-first search.</summary>
public sealed class DepthFirstResult
{
    internal DepthFirstResult(
        ImmutableArray<VertexId> preOrder,
        ImmutableArray<VertexId> postOrder,
        ImmutableArray<DepthFirstEdgeClassification> edgeClassifications)
    {
        PreOrder = preOrder;
        PostOrder = postOrder;
        EdgeClassifications = edgeClassifications;
    }

    /// <summary>Gets vertices in discovery order.</summary>
    public ImmutableArray<VertexId> PreOrder { get; }

    /// <summary>Gets vertices in completion order.</summary>
    public ImmutableArray<VertexId> PostOrder { get; }

    /// <summary>
    /// Gets classifications in edge insertion order. Filtered-out edges and edges that leave
    /// unvisited vertices in the selected direction are omitted.
    /// </summary>
    public ImmutableArray<DepthFirstEdgeClassification> EdgeClassifications { get; }
}

/// <summary>
/// Runs iterative depth-first search without recursion. Adjacency follows edge insertion order.
/// Classifications are relative to the selected traversal direction. An edge filter is called
/// at most once per edge; filtered-out and unvisited edges are omitted.
/// </summary>
public static class GraphDepthFirstSearch
{
    /// <summary>
    /// Runs deterministic depth-first search from the supplied roots, visiting only vertices
    /// reachable from them. Duplicate roots are ignored; an empty sequence visits nothing.
    /// Roots are enumerated once. Runs in O(V + E + R) time and O(V + E) extra space, where R is
    /// the number of roots supplied, counting duplicates, assuming the edge filter runs in
    /// constant time.
    /// </summary>
    /// <typeparam name="TVertex">The graph vertex value type.</typeparam>
    /// <typeparam name="TEdge">The graph edge payload type.</typeparam>
    /// <param name="graph">The immutable graph to search.</param>
    /// <param name="roots">Root vertex ids in traversal-priority order.</param>
    /// <param name="direction">Whether to follow outgoing or incoming edges.</param>
    /// <param name="edgeFilter">An optional predicate applied to each examined edge.</param>
    /// <returns>
    /// Visited vertices in pre-order and post-order, and classifications for accepted edges leaving
    /// visited vertices in edge id order.
    /// </returns>
    /// <exception cref="ArgumentNullException">The graph or roots are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A root id or direction is outside its valid range.</exception>
    public static DepthFirstResult FromRoots<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        IEnumerable<VertexId> roots,
        GraphDirection direction = GraphDirection.Outgoing,
        Func<GraphEdge<TEdge>, bool>? edgeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roots);
        GraphReachability.ValidateDirection(direction);
        return Traverse(graph, roots, direction, edgeFilter);
    }

    /// <summary>
    /// Runs a deterministic depth-first forest starting at every unvisited vertex in id order, so
    /// every accepted edge is classified. Runs in O(V + E) time and O(V + E) extra space,
    /// assuming the edge filter runs in constant time.
    /// </summary>
    /// <typeparam name="TVertex">The graph vertex value type.</typeparam>
    /// <typeparam name="TEdge">The graph edge payload type.</typeparam>
    /// <param name="graph">The immutable graph to search.</param>
    /// <param name="direction">Whether to follow outgoing or incoming edges.</param>
    /// <param name="edgeFilter">An optional predicate applied to each examined edge.</param>
    /// <returns>
    /// All vertices in pre-order and post-order, and classifications for accepted edges in edge id
    /// order.
    /// </returns>
    /// <exception cref="ArgumentNullException">The graph is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The direction is outside its valid range.</exception>
    public static DepthFirstResult FromAllVertices<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        GraphDirection direction = GraphDirection.Outgoing,
        Func<GraphEdge<TEdge>, bool>? edgeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        GraphReachability.ValidateDirection(direction);
        return Traverse(graph, AllVertices(graph.VertexCount), direction, edgeFilter);
    }

    private static DepthFirstResult Traverse<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        IEnumerable<VertexId> roots,
        GraphDirection direction,
        Func<GraphEdge<TEdge>, bool>? edgeFilter)
    {
        var state = new byte[graph.VertexCount];
        var discoveredAt = new int[graph.VertexCount];
        var finishedAt = new int[graph.VertexCount];
        var kinds = new DepthFirstEdgeKind?[graph.EdgeCount];
        var pointsToFinishedVertex = new bool[graph.EdgeCount];
        var preOrder = ImmutableArray.CreateBuilder<VertexId>();
        var postOrder = ImmutableArray.CreateBuilder<VertexId>();
        var frames = new List<Frame>();
        var clock = 0;

        void Visit(VertexId root)
        {
            if (state[root.Value] != 0)
            {
                return;
            }

            state[root.Value] = 1;
            discoveredAt[root.Value] = ++clock;
            preOrder.Add(root);
            frames.Add(new Frame(root));

            while (frames.Count > 0)
            {
                var frame = frames[^1];
                var adjacent = direction == GraphDirection.Outgoing
                    ? graph.OutgoingEdges(frame.Vertex)
                    : graph.IncomingEdges(frame.Vertex);

                if (frame.NextEdgeIndex >= adjacent.Length)
                {
                    state[frame.Vertex.Value] = 2;
                    finishedAt[frame.Vertex.Value] = ++clock;
                    postOrder.Add(frame.Vertex);
                    frames.RemoveAt(frames.Count - 1);
                    continue;
                }

                var edgeId = adjacent[frame.NextEdgeIndex];
                frame.NextEdgeIndex++;
                frames[^1] = frame;
                var edge = graph.GetEdge(edgeId);
                if (edgeFilter is not null && !edgeFilter(edge))
                {
                    continue;
                }

                var next = direction == GraphDirection.Outgoing ? edge.Target : edge.Source;
                if (state[next.Value] == 0)
                {
                    kinds[edgeId.Value] = DepthFirstEdgeKind.Tree;
                    state[next.Value] = 1;
                    discoveredAt[next.Value] = ++clock;
                    preOrder.Add(next);
                    frames.Add(new Frame(next));
                }
                else if (state[next.Value] == 1)
                {
                    kinds[edgeId.Value] = DepthFirstEdgeKind.Back;
                }
                else
                {
                    pointsToFinishedVertex[edgeId.Value] = true;
                }
            }
        }

        foreach (var root in roots)
        {
            graph.ValidateVertex(root);
            Visit(root);
        }

        var classifications = ImmutableArray.CreateBuilder<DepthFirstEdgeClassification>();
        for (var index = 0; index < graph.EdgeCount; index++)
        {
            if (pointsToFinishedVertex[index])
            {
                var edge = graph.GetEdge(new EdgeId(index));
                var from = direction == GraphDirection.Outgoing ? edge.Source : edge.Target;
                var to = direction == GraphDirection.Outgoing ? edge.Target : edge.Source;
                var isDescendant =
                    discoveredAt[from.Value] < discoveredAt[to.Value]
                    && finishedAt[to.Value] < finishedAt[from.Value];
                kinds[index] = isDescendant ? DepthFirstEdgeKind.Forward : DepthFirstEdgeKind.Cross;
            }

            if (kinds[index] is { } kind)
            {
                classifications.Add(new DepthFirstEdgeClassification(new EdgeId(index), kind));
            }
        }

        return new DepthFirstResult(
            preOrder.ToImmutable(),
            postOrder.ToImmutable(),
            classifications.ToImmutable());
    }

    private static IEnumerable<VertexId> AllVertices(int count)
    {
        for (var index = 0; index < count; index++)
        {
            yield return new VertexId(index);
        }
    }

    private struct Frame(VertexId vertex)
    {
        public VertexId Vertex { get; } = vertex;
        public int NextEdgeIndex { get; set; }
    }
}
