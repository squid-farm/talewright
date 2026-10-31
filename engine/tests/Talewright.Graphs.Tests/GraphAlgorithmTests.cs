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
            [new VertexId(0), new VertexId(0)],
            edgeFilter: edge => edge.Value != "filtered");

        Assert.Equal(
            new[] { new VertexId(0), new VertexId(1), new VertexId(2), new VertexId(3) },
            forward.Order);
        Assert.True(forward.IsReached(new VertexId(0)));
        Assert.Null(forward.FirstEdgeTo(new VertexId(0)));
        Assert.Equal(new EdgeId(2), forward.FirstEdgeTo(new VertexId(3)));
        Assert.False(forward.IsReached(new VertexId(4)));
        Assert.Null(forward.FirstEdgeTo(new VertexId(4)));

        var reverse = GraphReachability.Find(
            graph,
            [new VertexId(3)],
            GraphDirection.Incoming,
            edge => edge.Value != "filtered");
        Assert.Equal(
            new[] { new VertexId(3), new VertexId(1), new VertexId(2), new VertexId(0) },
            reverse.Order);
        Assert.Equal(new EdgeId(0), reverse.FirstEdgeTo(new VertexId(0)));

        var filtered = GraphReachability.Find(
            graph,
            [new VertexId(0)],
            edgeFilter: edge => edge.Value == "filtered");
        Assert.Equal(new[] { new VertexId(0), new VertexId(1), new VertexId(4) }, filtered.Order);
    }

    [Fact]
    public void DepthFirstSearchReportsEveryEdgeKindAndStableOrders()
    {
        var builder = new GraphBuilder<int, int>();
        for (var index = 0; index < 6; index++)
        {
            builder.AddVertex(index);
        }

        builder.AddEdge(0, 1, 0);
        builder.AddEdge(1, 2, 1);
        builder.AddEdge(2, 0, 2);
        builder.AddEdge(0, 1, 3);
        builder.AddEdge(0, 3, 4);
        builder.AddEdge(3, 1, 5);
        builder.AddEdge(4, 4, 6);
        var graph = builder.Build();

        var result = GraphDepthFirstSearch.Run(graph);

        Assert.Equal(
            new[] { 0, 1, 2, 3, 4, 5 }.Select(index => new VertexId(index)),
            result.PreOrder);
        Assert.Equal(
            new[] { 2, 1, 3, 0, 4, 5 }.Select(index => new VertexId(index)),
            result.PostOrder);
        Assert.Equal(
            new[]
            {
                new DepthFirstEdgeClassification(new EdgeId(0), DepthFirstEdgeKind.Tree),
                new DepthFirstEdgeClassification(new EdgeId(1), DepthFirstEdgeKind.Tree),
                new DepthFirstEdgeClassification(new EdgeId(2), DepthFirstEdgeKind.Back),
                new DepthFirstEdgeClassification(new EdgeId(3), DepthFirstEdgeKind.Forward),
                new DepthFirstEdgeClassification(new EdgeId(4), DepthFirstEdgeKind.Tree),
                new DepthFirstEdgeClassification(new EdgeId(5), DepthFirstEdgeKind.Cross),
                new DepthFirstEdgeClassification(new EdgeId(6), DepthFirstEdgeKind.Back)
            },
            result.EdgeClassifications);

        var incoming = GraphDepthFirstSearch.Run(graph, [new VertexId(3)], GraphDirection.Incoming);
        Assert.Equal(6, incoming.PreOrder.Length);
        Assert.Equal(7, incoming.EdgeClassifications.Length);

        var filtered = GraphDepthFirstSearch.Run(graph, edgeFilter: edge => edge.Value != 3);
        Assert.DoesNotContain(filtered.EdgeClassifications, item => item.Edge == new EdgeId(3));
        Assert.Equal(6, filtered.EdgeClassifications.Length);
    }

    [Fact]
    public void ComponentsAreTopologicallyNumberedAndCondensationKeepsCrossingEdges()
    {
        var builder = new GraphBuilder<string, int>();
        foreach (var vertex in new[] { "a", "b", "c", "d", "e", "f", "isolated", "loop" })
        {
            builder.AddVertex(vertex);
        }

        builder.AddEdge("a", "b", 0);
        builder.AddEdge("b", "a", 1);
        var aToC = builder.AddEdge("a", "c", 2);
        builder.AddEdge("b", "c", 3);
        builder.AddEdge("c", "d", 4);
        builder.AddEdge("d", "c", 5);
        var dToE = builder.AddEdge("d", "e", 6);
        builder.AddEdge("e", "f", 7);
        builder.AddEdge("loop", "loop", 8);
        var graph = builder.Build();
        var components = GraphStronglyConnectedComponents.Find(graph);
        var condensation = GraphCondensation.Build(graph, components);

        Assert.Equal(6, components.Components.Length);
        Assert.Equal(components.GetComponentOf(new VertexId(0)), components.GetComponentOf(new VertexId(1)));
        Assert.Equal(components.GetComponentOf(new VertexId(2)), components.GetComponentOf(new VertexId(3)));
        Assert.NotEqual(components.GetComponentOf(new VertexId(0)), components.GetComponentOf(new VertexId(2)));
        Assert.True(components.IsLoop(components.GetComponentOf(new VertexId(0))));
        Assert.True(components.IsLoop(components.GetComponentOf(new VertexId(2))));
        Assert.False(components.IsLoop(components.GetComponentOf(new VertexId(4))));
        Assert.False(components.IsLoop(components.GetComponentOf(new VertexId(6))));
        Assert.True(components.IsLoop(components.GetComponentOf(new VertexId(7))));
        Assert.Equal(components.Components.Length, condensation.VertexCount);
        Assert.Equal(4, condensation.EdgeCount);
        Assert.Equal(new[] { aToC, new EdgeId(3), dToE, new EdgeId(7) }, Enumerable.Range(0, condensation.EdgeCount)
            .Select(index => condensation.GetEdge(new EdgeId(index)).Value));

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

        var otherGraph = new GraphBuilder<string, int>();
        otherGraph.AddVertex("different");
        Assert.Throws<ArgumentException>(() => GraphCondensation.Build(otherGraph.Build(), components));
    }

    [Fact]
    public void ComponentsAndCondensationHonorDirectionAndEdgeFilter()
    {
        var builder = new GraphBuilder<int, int>();
        for (var vertex = 0; vertex < 5; vertex++)
        {
            builder.AddVertex(vertex);
        }

        builder.AddEdge(0, 1, 0);
        builder.AddEdge(1, 0, 1);
        builder.AddEdge(1, 2, 2);
        builder.AddEdge(2, 3, 3);
        builder.AddEdge(3, 2, 4);
        builder.AddEdge(3, 4, 5);
        var graph = builder.Build();
        var components = GraphStronglyConnectedComponents.Find(
            graph,
            GraphDirection.Incoming,
            edge => edge.Id != new EdgeId(1));
        var condensation = GraphCondensation.Build(graph, components);

        Assert.NotEqual(components.GetComponentOf(new VertexId(0)), components.GetComponentOf(new VertexId(1)));
        Assert.Equal(components.GetComponentOf(new VertexId(2)), components.GetComponentOf(new VertexId(3)));
        Assert.True(components.IsLoop(components.GetComponentOf(new VertexId(2))));
        Assert.False(components.IsLoop(components.GetComponentOf(new VertexId(0))));

        var expectedCrossingEdges = new[] { new EdgeId(0), new EdgeId(2), new EdgeId(5) };
        Assert.Equal(
            expectedCrossingEdges,
            Enumerable.Range(0, condensation.EdgeCount)
                .Select(index => condensation.GetEdge(new EdgeId(index)).Value));
        for (var index = 0; index < condensation.EdgeCount; index++)
        {
            var condensedEdge = condensation.GetEdge(new EdgeId(index));
            Assert.True(condensedEdge.Source.Value < condensedEdge.Target.Value);
        }
    }

    [Fact]
    public void RandomGraphsMatchTransitiveClosureAndRemainDeterministicWithCollidingHashes()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            try
            {
                var random = new Random(seed);
                var vertexCount = random.Next(0, 10);
                var specs = new List<(int Source, int Target, int Payload)>();
                var edgeCount = random.Next(0, Math.Max(1, vertexCount * vertexCount * 2 + 1));
                for (var index = 0; index < edgeCount; index++)
                {
                    if (vertexCount == 0)
                    {
                        break;
                    }

                    specs.Add((random.Next(vertexCount), random.Next(vertexCount), index));
                }

                var normal = BuildGraph(vertexCount, specs, EqualityComparer<int>.Default);
                var collisions = BuildGraph(vertexCount, specs, new ConstantHashComparer());
                var closure = TransitiveClosure(vertexCount, specs, _ => true);
                var roots = vertexCount == 0
                    ? Array.Empty<VertexId>()
                    : new[] { new VertexId(0), new VertexId(0), new VertexId(random.Next(vertexCount)) };
                var reached = GraphReachability.Find(normal, roots);
                var collisionReached = GraphReachability.Find(collisions, roots);
                var repeatedReached = GraphReachability.Find(normal, roots);
                var expectedReached = Enumerable.Range(0, vertexCount)
                    .Where(vertex => roots.Any(root => closure[root.Value, vertex]))
                    .Select(vertex => new VertexId(vertex));
                Assert.Equal(expectedReached.OrderBy(id => id.Value), reached.Order.OrderBy(id => id.Value));
                Assert.Equal(reached.Order.ToArray(), collisionReached.Order.ToArray());
                Assert.Equal(reached.Order.ToArray(), repeatedReached.Order.ToArray());
                for (var vertex = 0; vertex < vertexCount; vertex++)
                {
                    var id = new VertexId(vertex);
                    Assert.Equal(reached.IsReached(id), collisionReached.IsReached(id));
                    Assert.Equal(reached.FirstEdgeTo(id), collisionReached.FirstEdgeTo(id));
                }

                var components = GraphStronglyConnectedComponents.Find(normal);
                var collisionComponents = GraphStronglyConnectedComponents.Find(collisions);
                Assert.Equal(
                    components.Components.Select(component => string.Join(',', component.Select(vertex => vertex.Value))),
                    collisionComponents.Components.Select(component => string.Join(',', component.Select(vertex => vertex.Value))));
                for (var left = 0; left < vertexCount; left++)
                {
                    Assert.Equal(
                        components.GetComponentOf(new VertexId(left)),
                        collisionComponents.GetComponentOf(new VertexId(left)));
                    for (var right = 0; right < vertexCount; right++)
                    {
                        Assert.Equal(
                            closure[left, right] && closure[right, left],
                            components.GetComponentOf(new VertexId(left))
                                == components.GetComponentOf(new VertexId(right)));
                    }
                }

                var dfs = GraphDepthFirstSearch.Run(normal);
                var collisionDfs = GraphDepthFirstSearch.Run(collisions);
                Assert.Equal(dfs.PreOrder.ToArray(), collisionDfs.PreOrder.ToArray());
                Assert.Equal(dfs.PostOrder.ToArray(), collisionDfs.PostOrder.ToArray());
                Assert.Equal(dfs.EdgeClassifications.ToArray(), collisionDfs.EdgeClassifications.ToArray());
                Assert.Equal(normal.EdgeCount, dfs.EdgeClassifications.Length);

                var condensed = GraphCondensation.Build(normal, components);
                var collisionCondensed = GraphCondensation.Build(collisions, collisionComponents);
                Assert.Equal(
                    Enumerable.Range(0, condensed.EdgeCount)
                        .Select(index => condensed.GetEdge(new EdgeId(index))),
                    Enumerable.Range(0, collisionCondensed.EdgeCount)
                        .Select(index => collisionCondensed.GetEdge(new EdgeId(index))));

                var reverseClosure = TransitiveClosure(vertexCount, specs, _ => true, reverse: true);
                var incoming = GraphReachability.Find(normal, roots, GraphDirection.Incoming);
                var expectedIncoming = Enumerable.Range(0, vertexCount)
                    .Where(vertex => roots.Any(root => reverseClosure[root.Value, vertex]))
                    .Select(vertex => new VertexId(vertex));
                Assert.Equal(
                    expectedIncoming.OrderBy(id => id.Value),
                    incoming.Order.OrderBy(id => id.Value));
            }
            catch (Xunit.Sdk.XunitException exception)
            {
                throw new Xunit.Sdk.XunitException($"Random graph seed {seed}: {exception.Message}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryAlgorithmHandlesADeepChainAndRingWithoutRecursion(bool ring)
    {
        const int vertexCount = 100_000;
        var builder = new GraphBuilder<int, int>();
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            builder.AddVertex(vertex);
        }

        for (var vertex = 0; vertex < vertexCount - 1; vertex++)
        {
            builder.AddEdge(vertex, vertex + 1, vertex);
        }

        if (ring)
        {
            builder.AddEdge(vertexCount - 1, 0, vertexCount - 1);
        }

        var graph = builder.Build();
        var roots = new[] { new VertexId(0) };
        var reachable = GraphReachability.Find(graph, roots);
        var depthFirst = GraphDepthFirstSearch.Run(graph);
        var components = GraphStronglyConnectedComponents.Find(graph);
        var condensation = GraphCondensation.Build(graph, components);

        Assert.Equal(vertexCount, reachable.Order.Length);
        Assert.Equal(vertexCount, depthFirst.PreOrder.Length);
        Assert.Equal(graph.EdgeCount, depthFirst.EdgeClassifications.Length);
        Assert.Equal(ring ? 1 : vertexCount, components.Components.Length);
        Assert.Equal(ring ? 0 : vertexCount - 1, condensation.EdgeCount);
        if (ring)
        {
            Assert.True(components.IsLoop(new ComponentId(0)));
        }
    }

    private static Graph<int, int> BuildGraph(
        int vertexCount,
        IEnumerable<(int Source, int Target, int Payload)> specs,
        IEqualityComparer<int> comparer)
    {
        var builder = new GraphBuilder<int, int>(comparer);
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            builder.AddVertex(vertex);
        }

        foreach (var (source, target, payload) in specs)
        {
            builder.AddEdge(source, target, payload);
        }

        return builder.Build();
    }

    private static bool[,] TransitiveClosure(
        int vertexCount,
        IEnumerable<(int Source, int Target, int Payload)> specs,
        Func<int, bool> include,
        bool reverse = false)
    {
        var closure = new bool[vertexCount, vertexCount];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            closure[vertex, vertex] = true;
        }

        foreach (var (source, target, payload) in specs)
        {
            if (include(payload))
            {
                closure[reverse ? target : source, reverse ? source : target] = true;
            }
        }

        for (var via = 0; via < vertexCount; via++)
        {
            for (var source = 0; source < vertexCount; source++)
            {
                for (var target = 0; target < vertexCount; target++)
                {
                    closure[source, target] |= closure[source, via] && closure[via, target];
                }
            }
        }

        return closure;
    }

    private static Graph<int, string> BuildReachabilityGraph()
    {
        var builder = new GraphBuilder<int, string>();
        for (var vertex = 0; vertex < 5; vertex++)
        {
            builder.AddVertex(vertex);
        }

        builder.AddEdge(0, 1, "first");
        builder.AddEdge(0, 2, "second");
        builder.AddEdge(1, 3, "route");
        builder.AddEdge(2, 3, "route");
        builder.AddEdge(0, 0, "loop");
        builder.AddEdge(0, 1, "filtered");
        builder.AddEdge(0, 4, "filtered");
        return builder.Build();
    }

    private sealed class ConstantHashComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => x == y;

        public int GetHashCode(int obj) => 0;
    }
}
