namespace Talewright.Graphs;

/// <summary>
/// Identifies a vertex in one built graph. Ids are dense from zero in insertion order.
/// <c>default(VertexId)</c> is vertex 0, so callers must handle failed lookups explicitly rather
/// than falling back to a default id. An id from another graph passes range checks when its value
/// is in range.
/// </summary>
public readonly record struct VertexId(int Value);

/// <summary>Identifies an edge in one built graph.</summary>
public readonly record struct EdgeId(int Value);

/// <summary>Identifies a strongly connected component in one analysis result.</summary>
public readonly record struct ComponentId(int Value);

/// <summary>The direction in which a graph algorithm follows edges.</summary>
public enum GraphDirection
{
    /// <summary>Follow edges from their source to their target.</summary>
    Outgoing,

    /// <summary>Follow edges from their target to their source.</summary>
    Incoming
}

/// <summary>
/// An edge and its endpoints in a built graph. Equality and hashing use only <see cref="Id"/>;
/// the edge payload is never compared, hashed, or formatted.
/// </summary>
/// <typeparam name="TEdge">The caller-supplied edge payload type.</typeparam>
public readonly record struct GraphEdge<TEdge>(
    EdgeId Id,
    VertexId Source,
    VertexId Target,
    TEdge Value)
{
    /// <summary>Returns whether another edge has the same id.</summary>
    public bool Equals(GraphEdge<TEdge> other) => Id == other.Id;

    /// <summary>Gets a hash code based only on the edge id.</summary>
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>Returns a string containing only the edge id.</summary>
    public override string ToString() => $"GraphEdge {{ Id = {Id} }}";
}

/// <summary>
/// Builds an immutable directed multigraph in worst-case O(V + E) time and space. Vertex and
/// edge ids are dense and follow insertion order. Vertex and edge values are stored but never
/// compared, hashed, or formatted; equal values identify distinct vertices, and null is allowed.
/// </summary>
/// <typeparam name="TVertex">A caller-supplied vertex value.</typeparam>
/// <typeparam name="TEdge">A caller-supplied edge payload.</typeparam>
public sealed class GraphBuilder<TVertex, TEdge>
{
    private readonly List<TVertex> _vertices = [];
    private readonly List<PendingEdge> _edges = [];

    /// <summary>Adds a vertex and returns its id, the number of vertices added before it.</summary>
    public VertexId AddVertex(TVertex value)
    {
        var id = new VertexId(_vertices.Count);
        _vertices.Add(value);
        return id;
    }

    /// <summary>
    /// Adds an edge between vertices already added to this builder. Parallel edges and self-loops
    /// are retained.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either vertex id is outside this builder's vertex range.</exception>
    public EdgeId AddEdge(VertexId source, VertexId target, TEdge value)
    {
        ValidateAddedVertex(source, nameof(source));
        ValidateAddedVertex(target, nameof(target));
        var id = new EdgeId(_edges.Count);
        _edges.Add(new PendingEdge(source, target, value));
        return id;
    }

    /// <summary>
    /// Builds a snapshot using O(V + E) stable counting sorts. Further builder changes do not
    /// change this graph.
    /// </summary>
    public Graph<TVertex, TEdge> Build()
    {
        var vertexCount = _vertices.Count;
        var edgeCount = _edges.Count;
        var outgoingOffsets = new int[vertexCount + 1];
        var incomingOffsets = new int[vertexCount + 1];

        foreach (var edge in _edges)
        {
            outgoingOffsets[edge.Source.Value + 1]++;
            incomingOffsets[edge.Target.Value + 1]++;
        }

        PrefixSum(outgoingOffsets);
        PrefixSum(incomingOffsets);

        var outgoingEdgeIds = new EdgeId[edgeCount];
        var incomingEdgeIds = new EdgeId[edgeCount];
        var nextOutgoing = (int[])outgoingOffsets.Clone();
        var nextIncoming = (int[])incomingOffsets.Clone();
        var edges = new GraphEdge<TEdge>[edgeCount];

        for (var index = 0; index < edgeCount; index++)
        {
            var pending = _edges[index];
            var edgeId = new EdgeId(index);
            edges[index] = new GraphEdge<TEdge>(edgeId, pending.Source, pending.Target, pending.Value);
            outgoingEdgeIds[nextOutgoing[pending.Source.Value]++] = edgeId;
            incomingEdgeIds[nextIncoming[pending.Target.Value]++] = edgeId;
        }

        return new Graph<TVertex, TEdge>(
            _vertices.ToArray(),
            edges,
            outgoingOffsets,
            outgoingEdgeIds,
            incomingOffsets,
            incomingEdgeIds);
    }

    private void ValidateAddedVertex(VertexId vertex, string parameterName)
    {
        if ((uint)vertex.Value >= (uint)_vertices.Count)
        {
            throw new ArgumentOutOfRangeException(parameterName, vertex, "The vertex id is outside this builder's vertex range.");
        }
    }

    private static void PrefixSum(int[] offsets)
    {
        for (var index = 1; index < offsets.Length; index++)
        {
            offsets[index] += offsets[index - 1];
        }
    }

    private readonly record struct PendingEdge(VertexId Source, VertexId Target, TEdge Value);
}

/// <summary>
/// An immutable directed multigraph with insertion-ordered vertices, edges, and adjacency.
/// Construction takes worst-case O(V + E) time and space; adjacency reads take O(degree) time
/// and allocate no memory. Vertex and edge values are stored but never compared, hashed, or
/// formatted.
/// </summary>
/// <typeparam name="TVertex">The caller-supplied vertex value type.</typeparam>
/// <typeparam name="TEdge">The caller-supplied edge payload type.</typeparam>
public sealed class Graph<TVertex, TEdge>
{
    private readonly TVertex[] _vertices;
    private readonly GraphEdge<TEdge>[] _edges;
    private readonly int[] _outgoingOffsets;
    private readonly EdgeId[] _outgoingEdgeIds;
    private readonly int[] _incomingOffsets;
    private readonly EdgeId[] _incomingEdgeIds;

    internal Graph(
        TVertex[] vertices,
        GraphEdge<TEdge>[] edges,
        int[] outgoingOffsets,
        EdgeId[] outgoingEdgeIds,
        int[] incomingOffsets,
        EdgeId[] incomingEdgeIds)
    {
        _vertices = vertices;
        _edges = edges;
        _outgoingOffsets = outgoingOffsets;
        _outgoingEdgeIds = outgoingEdgeIds;
        _incomingOffsets = incomingOffsets;
        _incomingEdgeIds = incomingEdgeIds;
    }

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount => _vertices.Length;

    /// <summary>Gets the number of edges.</summary>
    public int EdgeCount => _edges.Length;

    /// <summary>Gets the value stored for a vertex.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside this graph.</exception>
    public TVertex GetVertexValue(VertexId vertex)
    {
        ValidateVertex(vertex);
        return _vertices[vertex.Value];
    }

    /// <summary>Gets an edge and its payload by its id.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside this graph.</exception>
    public GraphEdge<TEdge> GetEdge(EdgeId edge)
    {
        ValidateEdge(edge);
        return _edges[edge.Value];
    }

    /// <summary>
    /// Gets outgoing edges in edge insertion order. Neighbouring vertices may repeat when
    /// parallel edges exist. Runs in O(out-degree) time without allocating.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside this graph.</exception>
    public ReadOnlySpan<EdgeId> OutgoingEdges(VertexId vertex)
    {
        ValidateVertex(vertex);
        var start = _outgoingOffsets[vertex.Value];
        return _outgoingEdgeIds.AsSpan(start, _outgoingOffsets[vertex.Value + 1] - start);
    }

    /// <summary>
    /// Gets incoming edges in edge insertion order. Neighbouring vertices may repeat when
    /// parallel edges exist. Runs in O(in-degree) time without allocating.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside this graph.</exception>
    public ReadOnlySpan<EdgeId> IncomingEdges(VertexId vertex)
    {
        ValidateVertex(vertex);
        var start = _incomingOffsets[vertex.Value];
        return _incomingEdgeIds.AsSpan(start, _incomingOffsets[vertex.Value + 1] - start);
    }

    internal void ValidateVertex(VertexId vertex)
    {
        if ((uint)vertex.Value >= (uint)_vertices.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(vertex), vertex, "The vertex id is outside this graph.");
        }
    }

    internal void ValidateEdge(EdgeId edge)
    {
        if ((uint)edge.Value >= (uint)_edges.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(edge), edge, "The edge id is outside this graph.");
        }
    }
}
