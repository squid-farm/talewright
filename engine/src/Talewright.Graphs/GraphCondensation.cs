namespace Talewright.Graphs;

/// <summary>
/// Builds the SCC condensation graph in O(V + E) time and space, retaining one edge for every
/// original edge accepted by the component analysis that crosses components. The payload is that
/// original edge's id; orientation follows the analysis direction.
/// </summary>
public static class GraphCondensation
{
    /// <summary>
    /// Creates the acyclic graph of components for the exact graph analyzed. The selected edge
    /// filter and direction are inherited from <paramref name="components"/>.
    /// </summary>
    /// <typeparam name="TVertex">The original graph vertex key type.</typeparam>
    /// <typeparam name="TEdge">The original graph edge payload type.</typeparam>
    /// <param name="graph">The graph analyzed by <paramref name="components"/>.</param>
    /// <param name="components">Its strongly connected component result.</param>
    /// <returns>A graph with one vertex per component and one edge per crossing original edge.</returns>
    /// <exception cref="ArgumentNullException">The graph or component result is null.</exception>
    /// <exception cref="ArgumentException">The result was computed for a different graph instance.</exception>
    public static Graph<ComponentId, EdgeId> Build<TVertex, TEdge>(
        Graph<TVertex, TEdge> graph,
        StronglyConnectedComponentsResult<TVertex, TEdge> components)
        where TVertex : notnull
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(components);
        if (!ReferenceEquals(graph, components.SourceGraph))
        {
            throw new ArgumentException("The component result belongs to a different graph.", nameof(components));
        }

        var builder = new GraphBuilder<ComponentId, EdgeId>();
        for (var index = 0; index < components.Components.Length; index++)
        {
            builder.AddVertex(new ComponentId(index));
        }

        for (var index = 0; index < graph.EdgeCount; index++)
        {
            var edge = graph.GetEdge(new EdgeId(index));
            var source = components.GetComponentOf(edge.Source);
            var target = components.GetComponentOf(edge.Target);
            if (components.IncludedEdges[index] && source != target)
            {
                var from = components.Direction == GraphDirection.Outgoing ? source : target;
                var to = components.Direction == GraphDirection.Outgoing ? target : source;
                builder.AddEdge(from, to, edge.Id);
            }
        }

        return builder.Build();
    }
}
