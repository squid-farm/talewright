using Talewright.Graphs;

namespace Talewright.Graphs.Tests;

public sealed class GraphAlgorithmTests
{
    [Fact]
    public void ReachabilityHandlesDuplicateRootsDirectionsFiltersAndShortestWitnesses()
    {
        var graph = BuildReachabilityGraph();
        var forward = GraphReachability.Find(
            graph,
            [V(0), V(0)],
            edgeFilter: edge => edge.Value != "filtered");

        Assert.Equal(new[] { V(0), V(1), V(2), V(3) }, forward.Order);
        Assert.True(forward.IsReached(V(0)));
        Assert.Null(forward.FirstEdgeTo(V(0)));
        Assert.Equal(new EdgeId(2), forward.FirstEdgeTo(V(3)));
        Assert.False(forward.IsReached(V(4)));
        Assert.Null(forward.FirstEdgeTo(V(4)));

        var reverse = GraphReachability.Find(
            graph,
            [V(3)],
            GraphDirection.Incoming,
            edge => edge.Value != "filtered");
        Assert.Equal(new[] { V(3), V(1), V(2), V(0) }, reverse.Order);
        Assert.Equal(new EdgeId(0), reverse.FirstEdgeTo(V(0)));

        var filtered = GraphReachability.Find(
            graph,
            [V(0)],
            edgeFilter: edge => edge.Value == "filtered");
        Assert.Equal(new[] { V(0), V(1), V(4) }, filtered.Order);

        var orderedRoots = GraphReachability.Find(graph, [V(2), V(0)]);
        Assert.Equal(new[] { V(2), V(0), V(3), V(1), V(4) }, orderedRoots.Order);
        Assert.Null(orderedRoots.FirstEdgeTo(V(2)));
        Assert.Null(orderedRoots.FirstEdgeTo(V(0)));
        Assert.Equal(new EdgeId(3), orderedRoots.FirstEdgeTo(V(3)));
        Assert.Equal(new EdgeId(0), orderedRoots.FirstEdgeTo(V(1)));
        Assert.Equal(new EdgeId(6), orderedRoots.FirstEdgeTo(V(4)));

        var noRoots = GraphReachability.Find(graph, []);
        Assert.Empty(noRoots.Order);
        Assert.All(Enumerable.Range(0, graph.VertexCount), index => Assert.False(noRoots.IsReached(V(index))));
    }

    [Fact]
    public void ReachabilityStopsDuringAHighDegreeVertexWhenCancelled()
    {
        var builder = new GraphBuilder<int, int>();
        var root = builder.AddVertex(0);
        for (var index = 1; index <= 4096; index++)
        {
            var target = builder.AddVertex(index);
            builder.AddEdge(root, target, index);
        }
        var graph = builder.Build();
        using var cancellation = new CancellationTokenSource();
        var visited = 0;

        Assert.Throws<OperationCanceledException>(() =>
            GraphReachability.Find(graph, [root], GraphDirection.Outgoing, _ =>
            {
                if (++visited == 1) cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.InRange(visited, 1, 64);
    }

    [Fact]
    public void GraphBuildRejectsAnAlreadyCancelledToken()
    {
        var builder = new GraphBuilder<int, int>();
        builder.AddVertex(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => builder.Build(cancellation.Token));
    }

    [Fact]
    public void DepthFirstSearchFromAllVerticesReportsEveryEdgeKindInBothDirections()
    {
        var graph = BuildDepthFirstGraph();

        var outgoing = GraphDepthFirstSearch.FromAllVertices(graph);
        Assert.Equal(new[] { V(0), V(1), V(2), V(3), V(4), V(5) }, outgoing.PreOrder);
        Assert.Equal(new[] { V(2), V(1), V(3), V(0), V(4), V(5) }, outgoing.PostOrder);
        Assert.Equal(
            new[]
            {
                Classification(0, DepthFirstEdgeKind.Tree),
                Classification(1, DepthFirstEdgeKind.Tree),
                Classification(2, DepthFirstEdgeKind.Back),
                Classification(3, DepthFirstEdgeKind.Forward),
                Classification(4, DepthFirstEdgeKind.Tree),
                Classification(5, DepthFirstEdgeKind.Cross),
                Classification(6, DepthFirstEdgeKind.Back)
            },
            outgoing.EdgeClassifications);

        var incoming = GraphDepthFirstSearch.FromAllVertices(graph, GraphDirection.Incoming);
        Assert.Equal(new[] { V(0), V(2), V(1), V(3), V(4), V(5) }, incoming.PreOrder);
        Assert.Equal(new[] { V(3), V(1), V(2), V(0), V(4), V(5) }, incoming.PostOrder);
        Assert.Equal(
            new[]
            {
                Classification(0, DepthFirstEdgeKind.Back),
                Classification(1, DepthFirstEdgeKind.Tree),
                Classification(2, DepthFirstEdgeKind.Tree),
                Classification(3, DepthFirstEdgeKind.Back),
                Classification(4, DepthFirstEdgeKind.Back),
                Classification(5, DepthFirstEdgeKind.Tree),
                Classification(6, DepthFirstEdgeKind.Back)
            },
            incoming.EdgeClassifications);

        var filtered = GraphDepthFirstSearch.FromAllVertices(graph, edgeFilter: edge => edge.Value != 3);
        Assert.Equal(
            new[]
            {
                Classification(0, DepthFirstEdgeKind.Tree),
                Classification(1, DepthFirstEdgeKind.Tree),
                Classification(2, DepthFirstEdgeKind.Back),
                Classification(4, DepthFirstEdgeKind.Tree),
                Classification(5, DepthFirstEdgeKind.Cross),
                Classification(6, DepthFirstEdgeKind.Back)
            },
            filtered.EdgeClassifications);
    }

    [Fact]
    public void DepthFirstSearchFromRootsVisitsOnlyReachableVertices()
    {
        var graph = BuildDepthFirstGraph();

        var rooted = GraphDepthFirstSearch.FromRoots(graph, [V(3)]);
        Assert.Equal(new[] { V(3), V(1), V(2), V(0) }, rooted.PreOrder);
        Assert.Equal(new[] { V(0), V(2), V(1), V(3) }, rooted.PostOrder);
        Assert.Equal(
            new[]
            {
                Classification(0, DepthFirstEdgeKind.Back),
                Classification(1, DepthFirstEdgeKind.Tree),
                Classification(2, DepthFirstEdgeKind.Tree),
                Classification(3, DepthFirstEdgeKind.Back),
                Classification(4, DepthFirstEdgeKind.Back),
                Classification(5, DepthFirstEdgeKind.Tree)
            },
            rooted.EdgeClassifications);

        var duplicateRoots = GraphDepthFirstSearch.FromRoots(graph, [V(3), V(4), V(3)]);
        Assert.Equal(new[] { V(3), V(1), V(2), V(0), V(4) }, duplicateRoots.PreOrder);
        Assert.Equal(new[] { V(0), V(2), V(1), V(3), V(4) }, duplicateRoots.PostOrder);
        Assert.Equal(7, duplicateRoots.EdgeClassifications.Length);
        Assert.Contains(Classification(6, DepthFirstEdgeKind.Back), duplicateRoots.EdgeClassifications);

        var incoming = GraphDepthFirstSearch.FromRoots(graph, [V(1)], GraphDirection.Incoming);
        Assert.Equal(new[] { V(1), V(0), V(2), V(3) }, incoming.PreOrder);
        Assert.Equal(new[] { V(2), V(0), V(3), V(1) }, incoming.PostOrder);
        Assert.Equal(
            new[]
            {
                Classification(0, DepthFirstEdgeKind.Tree),
                Classification(1, DepthFirstEdgeKind.Back),
                Classification(2, DepthFirstEdgeKind.Tree),
                Classification(3, DepthFirstEdgeKind.Forward),
                Classification(4, DepthFirstEdgeKind.Cross),
                Classification(5, DepthFirstEdgeKind.Tree)
            },
            incoming.EdgeClassifications);

        var noRoots = GraphDepthFirstSearch.FromRoots(graph, []);
        Assert.Empty(noRoots.PreOrder);
        Assert.Empty(noRoots.PostOrder);
        Assert.Empty(noRoots.EdgeClassifications);
    }

    [Fact]
    public void EdgeFiltersAreCalledOncePerExaminedEdge()
    {
        var graph = BuildGraph(4, [(0, 1), (2, 3), (3, 3)]);
        var rootedReachabilityCalls = 0;
        GraphReachability.Find(graph, [V(0)], edgeFilter: edge =>
        {
            rootedReachabilityCalls++;
            return true;
        });
        Assert.Equal(1, rootedReachabilityCalls);

        var rootedDfsCalls = 0;
        GraphDepthFirstSearch.FromRoots(graph, [V(0)], edgeFilter: _ =>
        {
            rootedDfsCalls++;
            return true;
        });
        Assert.Equal(1, rootedDfsCalls);

        var fullDfsCalls = 0;
        GraphDepthFirstSearch.FromAllVertices(graph, edgeFilter: _ =>
        {
            fullDfsCalls++;
            return true;
        });
        Assert.Equal(graph.EdgeCount, fullDfsCalls);

        var componentCalls = 0;
        GraphStronglyConnectedComponents.Find(graph, edgeFilter: _ =>
        {
            componentCalls++;
            return true;
        });
        Assert.Equal(graph.EdgeCount, componentCalls);
    }

    [Fact]
    public void RootSequencesAreEnumeratedOnlyOnce()
    {
        var graph = BuildGraph(2, [(0, 1)]);
        var reachabilityEnumerations = 0;
        IEnumerable<VertexId> ReachabilityRoots()
        {
            reachabilityEnumerations++;
            if (reachabilityEnumerations > 1)
            {
                throw new InvalidOperationException("The root sequence was enumerated more than once.");
            }

            yield return V(0);
        }

        GraphReachability.Find(graph, ReachabilityRoots());
        Assert.Equal(1, reachabilityEnumerations);

        var depthFirstEnumerations = 0;
        IEnumerable<VertexId> DepthFirstRoots()
        {
            depthFirstEnumerations++;
            if (depthFirstEnumerations > 1)
            {
                throw new InvalidOperationException("The root sequence was enumerated more than once.");
            }

            yield return V(0);
        }

        GraphDepthFirstSearch.FromRoots(graph, DepthFirstRoots());
        Assert.Equal(1, depthFirstEnumerations);
    }

    [Fact]
    public void ComponentsAreTopologicallyNumberedAndCondensationKeepsCrossingEdges()
    {
        var graph = BuildComponentGraph();
        var components = GraphStronglyConnectedComponents.Find(graph);
        var condensation = GraphCondensation.Build(graph, components);

        Assert.Equal(
            new[] { "[7]", "[6]", "[0,1]", "[2,3]", "[4]", "[5]" },
            FormatComponents(components));
        Assert.Equal(new[] { true, false, true, true, false, false },
            Enumerable.Range(0, components.Components.Length)
                .Select(index => components.IsLoop(new ComponentId(index))));
        Assert.Equal(6, condensation.VertexCount);
        Assert.Equal(
            Enumerable.Range(0, 6).Select(index => new ComponentId(index)),
            Enumerable.Range(0, 6).Select(index => condensation.GetVertexValue(V(index))));
        Assert.Equal(
            new[]
            {
                (2, 3, 2),
                (2, 3, 3),
                (3, 4, 6),
                (4, 5, 7)
            },
            CondensationEdges(condensation));

        for (var index = 0; index < graph.EdgeCount; index++)
        {
            var edge = graph.GetEdge(new EdgeId(index));
            var sourceComponent = components.GetComponentOf(edge.Source);
            var targetComponent = components.GetComponentOf(edge.Target);
            if (sourceComponent != targetComponent)
            {
                Assert.True(sourceComponent.Value < targetComponent.Value);
            }
        }

        var otherGraphBuilder = new GraphBuilder<string, int>();
        otherGraphBuilder.AddVertex("different");
        Assert.Throws<ArgumentException>(() => GraphCondensation.Build(otherGraphBuilder.Build(), components));
    }

    [Fact]
    public void ComponentsAndCondensationHonorDirectionAndEdgeFilter()
    {
        var graph = BuildGraph(5, [(0, 1), (1, 0), (1, 2), (2, 3), (3, 2), (3, 4)]);
        var components = GraphStronglyConnectedComponents.Find(
            graph,
            GraphDirection.Incoming,
            edge => edge.Id != new EdgeId(1));
        var condensation = GraphCondensation.Build(graph, components);

        Assert.Equal(new[] { "[4]", "[2,3]", "[1]", "[0]" }, FormatComponents(components));
        Assert.Equal(
            new[] { 3, 2, 1, 1, 0 },
            Enumerable.Range(0, 5).Select(index => components.GetComponentOf(V(index)).Value));
        Assert.Equal(new[] { false, true, false, false },
            Enumerable.Range(0, 4).Select(index => components.IsLoop(new ComponentId(index))));
        Assert.Equal(
            new[] { (2, 3, 0), (1, 2, 2), (0, 1, 5) },
            CondensationEdges(condensation));
    }

    [Fact]
    public void ComponentNumberingAndMemberOrderArePinned()
    {
        var isolated = BuildGraph(3, []);
        var isolatedComponents = GraphStronglyConnectedComponents.Find(isolated);
        Assert.Equal(new[] { "[2]", "[1]", "[0]" }, FormatComponents(isolatedComponents));

        var ring = BuildGraph(3, [(0, 1), (1, 2), (2, 0)]);
        var ringComponents = GraphStronglyConnectedComponents.Find(ring);
        Assert.Equal(new[] { "[0,1,2]" }, FormatComponents(ringComponents));
        Assert.True(ringComponents.IsLoop(new ComponentId(0)));
    }

    [Theory]
    [InlineData(false, GraphDirection.Outgoing)]
    [InlineData(false, GraphDirection.Incoming)]
    [InlineData(true, GraphDirection.Outgoing)]
    [InlineData(true, GraphDirection.Incoming)]
    public void EveryAlgorithmHandlesADeepChainAndRingWithoutRecursion(bool ring, GraphDirection direction)
    {
        const int vertexCount = 100_000;
        var builder = new GraphBuilder<int, int>();
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            builder.AddVertex(vertex);
        }

        for (var vertex = 0; vertex < vertexCount - 1; vertex++)
        {
            builder.AddEdge(V(vertex), V(vertex + 1), vertex);
        }

        if (ring)
        {
            builder.AddEdge(V(vertexCount - 1), V(0), vertexCount - 1);
        }

        var graph = builder.Build();
        var root = direction == GraphDirection.Outgoing ? V(0) : V(vertexCount - 1);
        var reachable = GraphReachability.Find(graph, [root], direction);
        var rootedDfs = GraphDepthFirstSearch.FromRoots(graph, [root], direction);
        var fullDfs = GraphDepthFirstSearch.FromAllVertices(graph, direction);
        var components = GraphStronglyConnectedComponents.Find(graph, direction);
        var condensation = GraphCondensation.Build(graph, components);

        Assert.Equal(vertexCount, reachable.Order.Length);
        Assert.Equal(vertexCount, rootedDfs.PreOrder.Length);
        Assert.Equal(graph.EdgeCount, rootedDfs.EdgeClassifications.Length);
        Assert.Equal(vertexCount, fullDfs.PreOrder.Length);
        Assert.Equal(graph.EdgeCount, fullDfs.EdgeClassifications.Length);
        Assert.Equal(ring ? 1 : vertexCount, components.Components.Length);
        Assert.Equal(ring ? 0 : vertexCount - 1, condensation.EdgeCount);
        if (ring)
        {
            Assert.Equal(V(0), components.Components[0][0]);
            Assert.Equal(V(vertexCount - 1), components.Components[0][^1]);
            Assert.True(components.IsLoop(new ComponentId(0)));
        }
    }

    private static Graph<string, int> BuildComponentGraph()
    {
        var names = new[] { "a", "b", "c", "d", "e", "f", "isolated", "loop" };
        var builder = new GraphBuilder<string, int>();
        var vertices = names.Select(builder.AddVertex).ToArray();
        builder.AddEdge(vertices[0], vertices[1], 0);
        builder.AddEdge(vertices[1], vertices[0], 1);
        builder.AddEdge(vertices[0], vertices[2], 2);
        builder.AddEdge(vertices[1], vertices[2], 3);
        builder.AddEdge(vertices[2], vertices[3], 4);
        builder.AddEdge(vertices[3], vertices[2], 5);
        builder.AddEdge(vertices[3], vertices[4], 6);
        builder.AddEdge(vertices[4], vertices[5], 7);
        builder.AddEdge(vertices[7], vertices[7], 8);
        return builder.Build();
    }

    private static Graph<int, int> BuildGraph(int vertexCount, IEnumerable<(int Source, int Target)> edges)
    {
        var builder = new GraphBuilder<int, int>();
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            builder.AddVertex(vertex);
        }

        var payload = 0;
        foreach (var (source, target) in edges)
        {
            builder.AddEdge(V(source), V(target), payload++);
        }

        return builder.Build();
    }

    private static Graph<int, int> BuildDepthFirstGraph() =>
        BuildGraph(6, [(0, 1), (1, 2), (2, 0), (0, 1), (0, 3), (3, 1), (4, 4)]);

    private static Graph<int, string> BuildReachabilityGraph()
    {
        var builder = new GraphBuilder<int, string>();
        var vertices = Enumerable.Range(0, 5).Select(builder.AddVertex).ToArray();
        builder.AddEdge(vertices[0], vertices[1], "first");
        builder.AddEdge(vertices[0], vertices[2], "second");
        builder.AddEdge(vertices[1], vertices[3], "route");
        builder.AddEdge(vertices[2], vertices[3], "route");
        builder.AddEdge(vertices[0], vertices[0], "loop");
        builder.AddEdge(vertices[0], vertices[1], "filtered");
        builder.AddEdge(vertices[0], vertices[4], "filtered");
        return builder.Build();
    }

    private static string[] FormatComponents<TVertex, TEdge>(
        StronglyConnectedComponentsResult<TVertex, TEdge> components) =>
        components.Components
            .Select(component => $"[{string.Join(',', component.Select(vertex => vertex.Value))}]")
            .ToArray();

    private static (int Source, int Target, int Payload)[] CondensationEdges(
        Graph<ComponentId, EdgeId> graph) =>
        Enumerable.Range(0, graph.EdgeCount)
            .Select(index =>
            {
                var edge = graph.GetEdge(new EdgeId(index));
                return (edge.Source.Value, edge.Target.Value, edge.Value.Value);
            })
            .ToArray();

    private static VertexId V(int value) => new(value);

    private static DepthFirstEdgeClassification Classification(int edge, DepthFirstEdgeKind kind) =>
        new(new EdgeId(edge), kind);
}
