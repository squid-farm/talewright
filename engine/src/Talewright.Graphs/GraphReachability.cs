using System.Collections.Immutable;

namespace Talewright.Graphs;

/// <summary>The immutable result of a breadth-first reachability search.</summary>
public sealed class ReachabilityResult
{
    private readonly ImmutableArray<bool> _reached;
    private readonly ImmutableArray<EdgeId?> _firstEdgesByVertex;

    internal ReachabilityResult(
        ImmutableArray<VertexId> order,
        ImmutableArray<bool> reached,
        ImmutableArray<EdgeId?> firstEdgesByVertex)
    {
        Order = order;
        _reached = reached;
        _firstEdgesByVertex = firstEdgesByVertex;
    }

    /// <summary>Gets vertices in discovery order.</summary>
    public ImmutableArray<VertexId> Order { get; }

    /// <summary>Returns whether the vertex was reached; rejects ids outside the searched graph.</summary>
    public bool IsReached(VertexId vertex)
    {
        ValidateVertex(vertex);
        return _reached[vertex.Value];
    }

    /// <summary>
    /// Gets the first edge used to reach a vertex, or null for a root or an unreached vertex.
    /// Rejects ids outside the searched graph.
    /// </summary>
    public EdgeId? FirstEdgeTo(VertexId vertex)
    {
        ValidateVertex(vertex);
        return _firstEdgesByVertex[vertex.Value];
    }

    private void ValidateVertex(VertexId vertex)
    {
        if ((uint)vertex.Value >= (uint)_firstEdgesByVertex.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(vertex), vertex, "The vertex id is outside this result.");
        }
    }
}

/// <summary>
/// Finds vertices by breadth-first search in O(V + E) time and O(V) extra space.
/// Roots are considered in argument order, duplicates are ignored, and adjacency follows edge
/// insertion order. First-edge witnesses use the fewest edges; ties follow this deterministic
/// traversal order.
/// </summary>
public static class GraphReachability
{
    /// <summary>
    /// Finds vertices reachable from roots, optionally following only edges accepted by
    /// <paramref name="edgeFilter"/>.
    /// </summary>
    /// <typeparam name="TVertex">The graph vertex key type.</typeparam>
    /// <typeparam name="TEdge">The graph edge payload type.</typeparam>
    /// <param name="graph">The immutable graph to search.</param>
    /// <param name="roots">Root vertex ids in traversal-priority order.</param>
    /// <param name="direction">Whether to follow outgoing or incoming edges.</param>
    /// <param name="edgeFilter">An optional predicate applied to each examined edge.</param>
    /// <returns>Discovery order and the first edge used to reach each vertex.</returns>
    /// <exception cref="ArgumentNullException">The graph or roots are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A root id or direction is outside its valid range.</exception>
    public static ReachabilityResult Find<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        IEnumerable<VertexId> roots,
        GraphDirection direction = GraphDirection.Outgoing,
        Func<GraphEdge<TEdge>, bool>? edgeFilter = null)
        where TVertex : notnull
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(roots);
        ValidateDirection(direction);

        var reached = new bool[graph.VertexCount];
        var firstEdges = new EdgeId?[graph.VertexCount];
        var order = ImmutableArray.CreateBuilder<VertexId>();
        var queue = new Queue<VertexId>();

        foreach (var root in roots)
        {
            graph.ValidateVertex(root);
            if (reached[root.Value])
            {
                continue;
            }

            reached[root.Value] = true;
            order.Add(root);
            queue.Enqueue(root);
        }

        while (queue.TryDequeue(out var current))
        {
            var adjacent = direction == GraphDirection.Outgoing
                ? graph.OutgoingEdges(current)
                : graph.IncomingEdges(current);

            foreach (var edgeId in adjacent)
            {
                var edge = graph.GetEdge(edgeId);
                if (edgeFilter is not null && !edgeFilter(edge))
                {
                    continue;
                }

                var next = direction == GraphDirection.Outgoing ? edge.Target : edge.Source;
                if (reached[next.Value])
                {
                    continue;
                }

                reached[next.Value] = true;
                firstEdges[next.Value] = edgeId;
                order.Add(next);
                queue.Enqueue(next);
            }
        }

        return new ReachabilityResult(
            order.ToImmutable(),
            ImmutableArray.CreateRange(reached),
            ImmutableArray.CreateRange(firstEdges));
    }

    internal static void ValidateDirection(GraphDirection direction)
    {
        if (direction is not (GraphDirection.Outgoing or GraphDirection.Incoming))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown graph direction.");
        }
    }
}
