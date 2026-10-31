using System.Collections.Immutable;

namespace Talewright.Graphs;

/// <summary>The immutable strongly connected component partition of a graph.</summary>
public sealed class StronglyConnectedComponentsResult<TVertex, TEdge>
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

    /// <summary>Gets components in topological order, with members in ascending vertex id order.</summary>
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
/// Computes strongly connected components with iterative Kosaraju traversal in worst-case
/// O(V + E) time and space. Components are numbered in a topological order for the selected
/// direction: every selected edge between components goes from the lower to the higher number.
/// When no edge orders two components, numbering is deterministic and follows a depth-first search
/// from all vertices in id order, then numbers components in reverse post-order of that search.
/// Vertices within a component are in ascending id order. The filter is called exactly once per
/// edge, in edge id order. The complexity bound assumes the filter runs in constant time.
/// </summary>
public static class GraphStronglyConnectedComponents
{
    /// <summary>
    /// Partitions every graph vertex into exactly one strongly connected component, following the
    /// selected direction and only edges accepted by <paramref name="edgeFilter"/>.
    /// </summary>
    /// <typeparam name="TVertex">The graph vertex value type.</typeparam>
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
    {
        ArgumentNullException.ThrowIfNull(graph);
        GraphReachability.ValidateDirection(direction);

        var includedEdges = new bool[graph.EdgeCount];
        for (var index = 0; index < graph.EdgeCount; index++)
        {
            var edge = graph.GetEdge(new EdgeId(index));
            includedEdges[index] = edgeFilter is null || edgeFilter(edge);
        }

        var postOrder = GraphDepthFirstSearch.FromAllVertices(
            graph,
            direction,
            edge => includedEdges[edge.Id.Value]).PostOrder;
        var visited = new bool[graph.VertexCount];
        var componentByVertex = new ComponentId[graph.VertexCount];
        var componentCount = 0;
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

            var componentId = new ComponentId(componentCount);
            visited[root.Value] = true;
            componentByVertex[root.Value] = componentId;
            stack.Push(root);

            while (stack.TryPop(out var current))
            {
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

            componentCount++;
        }

        var memberBuilders = new ImmutableArray<VertexId>.Builder[componentCount];
        for (var index = 0; index < componentCount; index++)
        {
            memberBuilders[index] = ImmutableArray.CreateBuilder<VertexId>();
        }

        for (var vertex = 0; vertex < graph.VertexCount; vertex++)
        {
            memberBuilders[componentByVertex[vertex].Value].Add(new VertexId(vertex));
        }

        var components = ImmutableArray.CreateBuilder<ImmutableArray<VertexId>>(componentCount);
        for (var index = 0; index < componentCount; index++)
        {
            components.Add(memberBuilders[index].ToImmutable());
        }

        var loopFlags = new bool[componentCount];
        for (var index = 0; index < componentCount; index++)
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
