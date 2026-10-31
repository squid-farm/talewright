using System.Collections.Immutable;

namespace Talewright.Graphs;

/// <summary>The immutable strongly connected component partition of a graph.</summary>
public sealed class StronglyConnectedComponentsResult<TVertex, TEdge>
    where TVertex : notnull
{
    private readonly Graph<TVertex, TEdge> _graph;
    private readonly ImmutableArray<ComponentId> _componentByVertex;
    private readonly ImmutableArray<bool> _isLoop;

    internal StronglyConnectedComponentsResult(
        Graph<TVertex, TEdge> graph,
        ImmutableArray<ImmutableArray<VertexId>> components,
        ImmutableArray<ComponentId> componentByVertex,
        ImmutableArray<bool> isLoop,
        ImmutableArray<bool> includedEdges,
        GraphDirection direction)
    {
        _graph = graph;
        Components = components;
        _componentByVertex = componentByVertex;
        _isLoop = isLoop;
        IncludedEdges = includedEdges;
        Direction = direction;
    }

    /// <summary>Gets components in deterministic topological order for <see cref="Direction"/>.</summary>
    public ImmutableArray<ImmutableArray<VertexId>> Components { get; }

    /// <summary>Gets the direction used to order components and orient their condensation.</summary>
    public GraphDirection Direction { get; }

    /// <summary>Gets the component containing a vertex.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The vertex id is outside the analyzed graph.</exception>
    public ComponentId GetComponentOf(VertexId vertex)
    {
        ValidateVertex(vertex);
        return _componentByVertex[vertex.Value];
    }

    /// <summary>
    /// Returns whether a component contains multiple vertices or a self-loop.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The component id is outside this result.</exception>
    public bool IsLoop(ComponentId component)
    {
        if ((uint)component.Value >= (uint)_isLoop.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(component), component, "The component id is outside this result.");
        }

        return _isLoop[component.Value];
    }

    internal Graph<TVertex, TEdge> SourceGraph => _graph;

    internal ImmutableArray<bool> IncludedEdges { get; }

    private void ValidateVertex(VertexId vertex)
    {
        if ((uint)vertex.Value >= (uint)_componentByVertex.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(vertex), vertex, "The vertex id is outside this result.");
        }
    }
}

/// <summary>
/// Computes strongly connected components with iterative Kosaraju traversal in O(V + E) time
/// and O(V + E) extra space. Components are numbered in deterministic topological order for the
/// selected direction; vertices within a component follow reverse-graph DFS order.
/// </summary>
public static class GraphStronglyConnectedComponents
{
    /// <summary>
    /// Partitions every graph vertex into exactly one strongly connected component, following the
    /// selected direction and only edges accepted by <paramref name="edgeFilter"/>.
    /// </summary>
    /// <typeparam name="TVertex">The graph vertex key type.</typeparam>
    /// <typeparam name="TEdge">The graph edge payload type.</typeparam>
    /// <param name="graph">The immutable graph to analyze.</param>
    /// <param name="direction">The direction used to order the components.</param>
    /// <param name="edgeFilter">An optional predicate applied once to each edge in insertion order.</param>
    /// <returns>The component partition, topological numbering, and loop flags.</returns>
    /// <exception cref="ArgumentNullException">The graph is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The direction is outside its valid range.</exception>
    public static StronglyConnectedComponentsResult<TVertex, TEdge> Find<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        GraphDirection direction = GraphDirection.Outgoing,
        Func<GraphEdge<TEdge>, bool>? edgeFilter = null)
        where TVertex : notnull
    {
        ArgumentNullException.ThrowIfNull(graph);
        GraphReachability.ValidateDirection(direction);

        var includedEdges = new bool[graph.EdgeCount];
        for (var index = 0; index < graph.EdgeCount; index++)
        {
            var edge = graph.GetEdge(new EdgeId(index));
            includedEdges[index] = edgeFilter is null || edgeFilter(edge);
        }

        var postOrder = GraphDepthFirstSearch.Run(
            graph,
            direction: direction,
            edgeFilter: edge => includedEdges[edge.Id.Value]).PostOrder;
        var visited = new bool[graph.VertexCount];
        var componentByVertex = new ComponentId[graph.VertexCount];
        var components = ImmutableArray.CreateBuilder<ImmutableArray<VertexId>>();
        var stack = new Stack<VertexId>();
        var reverseDirection = direction == GraphDirection.Outgoing
            ? GraphDirection.Incoming
            : GraphDirection.Outgoing;

        for (var rootIndex = postOrder.Length - 1; rootIndex >= 0; rootIndex--)
        {
            var root = postOrder[rootIndex];
            if (visited[root.Value])
            {
                continue;
            }

            var componentId = new ComponentId(components.Count);
            var members = ImmutableArray.CreateBuilder<VertexId>();
            visited[root.Value] = true;
            componentByVertex[root.Value] = componentId;
            stack.Push(root);

            while (stack.TryPop(out var current))
            {
                members.Add(current);
                var adjacent = reverseDirection == GraphDirection.Outgoing
                    ? graph.OutgoingEdges(current)
                    : graph.IncomingEdges(current);
                for (var edgeIndex = adjacent.Length - 1; edgeIndex >= 0; edgeIndex--)
                {
                    var edgeId = adjacent[edgeIndex];
                    if (!includedEdges[edgeId.Value])
                    {
                        continue;
                    }

                    var edge = graph.GetEdge(edgeId);
                    var next = reverseDirection == GraphDirection.Outgoing ? edge.Target : edge.Source;
                    if (visited[next.Value])
                    {
                        continue;
                    }

                    visited[next.Value] = true;
                    componentByVertex[next.Value] = componentId;
                    stack.Push(next);
                }
            }

            components.Add(members.ToImmutable());
        }

        var loopFlags = new bool[components.Count];
        for (var index = 0; index < components.Count; index++)
        {
            loopFlags[index] = components[index].Length > 1;
        }

        for (var index = 0; index < graph.EdgeCount; index++)
        {
            var edge = graph.GetEdge(new EdgeId(index));
            if (includedEdges[index] && edge.Source == edge.Target)
            {
                loopFlags[componentByVertex[edge.Source.Value].Value] = true;
            }
        }

        return new StronglyConnectedComponentsResult<TVertex, TEdge>(
            graph,
            components.ToImmutable(),
            ImmutableArray.CreateRange(componentByVertex),
            ImmutableArray.CreateRange(loopFlags),
            ImmutableArray.CreateRange(includedEdges),
            direction);
    }
}
