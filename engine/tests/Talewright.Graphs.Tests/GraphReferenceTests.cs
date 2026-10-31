using Talewright.Graphs;

namespace Talewright.Graphs.Tests;

public sealed class GraphReferenceTests
{
    public static IEnumerable<object[]> Seeds()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            yield return [seed];
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomGraphsMatchReferenceImplementations(int seed)
    {
        var random = new Random(seed);
        var vertexCount = random.Next(0, 10);
        var edgeCount = random.Next(0, Math.Max(1, vertexCount * vertexCount * 2 + 1));
        var edges = new List<(int Source, int Target)>();
        for (var edge = 0; edge < edgeCount && vertexCount > 0; edge++)
        {
            edges.Add((random.Next(vertexCount), random.Next(vertexCount)));
        }

        var included = Enumerable.Range(0, edges.Count)
            .Select(_ => random.Next(4) != 0)
            .ToArray();
        var reference = new ReferenceGraph(vertexCount, edges, included);
        var rootCount = random.Next(0, 4);
        var roots = Enumerable.Range(0, rootCount)
            .Select(_ => random.Next(Math.Max(1, vertexCount)))
            .Where(_ => vertexCount > 0)
            .ToArray();

        AssertMatchesReference(reference, roots.Select(value => new VertexId(value)).ToArray());
    }

    [Fact]
    public void EveryThreeVertexGraphMatchesReferenceImplementations()
    {
        const int vertexCount = 3;
        var possibleEdges = (
            from source in Enumerable.Range(0, vertexCount)
            from target in Enumerable.Range(0, vertexCount)
            select (Source: source, Target: target)).ToArray();

        for (var mask = 0; mask < 1 << possibleEdges.Length; mask++)
        {
            try
            {
                var edges = possibleEdges
                    .Where((_, index) => (mask & 1 << index) != 0)
                    .ToArray();
                var included = Enumerable.Range(0, edges.Length)
                    .Select(index => index % 3 != 2)
                    .ToArray();
                var reference = new ReferenceGraph(vertexCount, edges, included);
                foreach (var roots in new[] { Array.Empty<int>(), [0], [2, 0, 2] })
                {
                    AssertMatchesReference(
                        reference,
                        roots.Select(value => new VertexId(value)).ToArray());
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Three-vertex graph mask {mask}: {exception.Message}", exception);
            }
        }
    }

    private static void AssertMatchesReference(ReferenceGraph reference, IReadOnlyList<VertexId> rootIds)
    {
        var graph = reference.Build();
        var roots = rootIds.Select(root => root.Value).ToArray();

        foreach (var direction in new[] { GraphDirection.Outgoing, GraphDirection.Incoming })
        {
            foreach (var useFilter in new[] { false, true })
            {
                Func<GraphEdge<int>, bool>? edgeFilter = useFilter
                    ? edge => reference.Included[edge.Id.Value]
                    : null;
                var expectedReachability = reference.Reachability(roots, direction, useFilter);
                var expectedDistances = reference.Distances(roots, direction, useFilter);
                var actualReachability = GraphReachability.Find(graph, rootIds, direction, edgeFilter);

                Assert.Equal(
                    expectedReachability.Order.Select(value => new VertexId(value)),
                    actualReachability.Order);
                for (var vertex = 0; vertex < reference.VertexCount; vertex++)
                {
                    Assert.Equal(
                        expectedDistances[vertex].HasValue,
                        actualReachability.IsReached(new VertexId(vertex)));
                    Assert.Equal(
                        expectedReachability.FirstEdge[vertex] is { } firstEdge
                            ? new EdgeId?(new EdgeId(firstEdge))
                            : null,
                        actualReachability.FirstEdgeTo(new VertexId(vertex)));
                }

                AssertWitnesses(
                    reference,
                    graph,
                    rootIds,
                    direction,
                    useFilter,
                    actualReachability,
                    expectedDistances);

                var rootedExpected = reference.DepthFirst(roots, direction, useFilter);
                var rootedActual = GraphDepthFirstSearch.FromRoots(graph, rootIds, direction, edgeFilter);
                AssertDepthFirstMatches(
                    reference,
                    direction,
                    rootedExpected,
                    rootedActual,
                    $"rooted DFS direction={direction}, filter={useFilter}, roots=[{string.Join(',', roots)}]");
                AssertTopologicalWhenAcyclic(reference, direction, rootedActual);

                var allRoots = Enumerable.Range(0, reference.VertexCount).ToArray();
                var allExpected = reference.DepthFirst(allRoots, direction, useFilter);
                var allActual = GraphDepthFirstSearch.FromAllVertices(graph, direction, edgeFilter);
                AssertDepthFirstMatches(
                    reference,
                    direction,
                    allExpected,
                    allActual,
                    $"full DFS direction={direction}, filter={useFilter}");
                AssertTopologicalWhenAcyclic(reference, direction, allActual);

                var closure = reference.Closure(direction, useFilter);
                var components = GraphStronglyConnectedComponents.Find(graph, direction, edgeFilter);
                var repeatedComponents = GraphStronglyConnectedComponents.Find(graph, direction, edgeFilter);
                Assert.Equal(
                    FormatComponents(components),
                    FormatComponents(repeatedComponents));
                Assert.Equal(
                    Enumerable.Range(0, reference.VertexCount)
                        .Select(vertex => components.GetComponentOf(new VertexId(vertex))),
                    Enumerable.Range(0, reference.VertexCount)
                        .Select(vertex => repeatedComponents.GetComponentOf(new VertexId(vertex))));

                var membershipCount = new int[reference.VertexCount];
                for (var componentIndex = 0; componentIndex < components.Components.Length; componentIndex++)
                {
                    var componentId = new ComponentId(componentIndex);
                    var members = components.Components[componentIndex];
                    Assert.Equal(members.OrderBy(vertex => vertex.Value), members);
                    foreach (var member in members)
                    {
                        membershipCount[member.Value]++;
                        Assert.Equal(componentId, components.GetComponentOf(member));
                    }

                    var hasSelfLoop = reference.Edges
                        .Select((edge, index) => (edge, index))
                        .Any(item =>
                            (!useFilter || reference.Included[item.index])
                            && item.edge.Source == item.edge.Target
                            && components.GetComponentOf(new VertexId(item.edge.Source)) == componentId);
                    Assert.Equal(members.Length > 1 || hasSelfLoop, components.IsLoop(componentId));
                }

                Assert.All(membershipCount, count => Assert.Equal(1, count));
                for (var left = 0; left < reference.VertexCount; left++)
                {
                    for (var right = 0; right < reference.VertexCount; right++)
                    {
                        var sameComponent = components.GetComponentOf(new VertexId(left))
                            == components.GetComponentOf(new VertexId(right));
                        Assert.Equal(closure[left, right] && closure[right, left], sameComponent);
                    }
                }

                var oriented = reference.Oriented(direction);
                for (var edge = 0; edge < oriented.Length; edge++)
                {
                    if (useFilter && !reference.Included[edge])
                    {
                        continue;
                    }

                    var (from, to) = oriented[edge];
                    var fromComponent = components.GetComponentOf(new VertexId(from));
                    var toComponent = components.GetComponentOf(new VertexId(to));
                    if (fromComponent != toComponent)
                    {
                        Assert.True(fromComponent.Value < toComponent.Value);
                    }
                }

                var condensation = GraphCondensation.Build(graph, components);
                var repeatedCondensation = GraphCondensation.Build(graph, repeatedComponents);
                Assert.Equal(condensation.VertexCount, components.Components.Length);
                Assert.Equal(
                    Enumerable.Range(0, condensation.VertexCount)
                        .Select(index => new ComponentId(index)),
                    Enumerable.Range(0, condensation.VertexCount)
                        .Select(index => condensation.GetVertexValue(new VertexId(index))));
                Assert.Equal(
                    CondensationEdges(condensation),
                    CondensationEdges(repeatedCondensation));

                var expectedCrossingEdges = Enumerable.Range(0, oriented.Length)
                    .Where(edge => (!useFilter || reference.Included[edge])
                        && components.GetComponentOf(new VertexId(oriented[edge].From))
                            != components.GetComponentOf(new VertexId(oriented[edge].To)))
                    .ToArray();
                Assert.Equal(expectedCrossingEdges, CondensationEdges(condensation).Select(edge => edge.Payload.Value));
                foreach (var edge in CondensationEdges(condensation))
                {
                    var original = oriented[edge.Payload.Value];
                    Assert.Equal(
                        components.GetComponentOf(new VertexId(original.From)).Value,
                        edge.Source);
                    Assert.Equal(
                        components.GetComponentOf(new VertexId(original.To)).Value,
                        edge.Target);
                    Assert.True(edge.Source < edge.Target);
                }

                Assert.DoesNotContain(
                    GraphDepthFirstSearch.FromAllVertices(condensation).EdgeClassifications,
                    item => item.Kind == DepthFirstEdgeKind.Back);

                var repeatedReachability = GraphReachability.Find(graph, rootIds, direction, edgeFilter);
                Assert.Equal(actualReachability.Order.ToArray(), repeatedReachability.Order.ToArray());
                for (var vertex = 0; vertex < reference.VertexCount; vertex++)
                {
                    var id = new VertexId(vertex);
                    Assert.Equal(actualReachability.IsReached(id), repeatedReachability.IsReached(id));
                    Assert.Equal(actualReachability.FirstEdgeTo(id), repeatedReachability.FirstEdgeTo(id));
                }

                var repeatedRootedDfs = GraphDepthFirstSearch.FromRoots(graph, rootIds, direction, edgeFilter);
                var repeatedAllDfs = GraphDepthFirstSearch.FromAllVertices(graph, direction, edgeFilter);
                Assert.Equal(rootedActual.PreOrder.ToArray(), repeatedRootedDfs.PreOrder.ToArray());
                Assert.Equal(rootedActual.PostOrder.ToArray(), repeatedRootedDfs.PostOrder.ToArray());
                Assert.Equal(rootedActual.EdgeClassifications.ToArray(), repeatedRootedDfs.EdgeClassifications.ToArray());
                Assert.Equal(allActual.PreOrder.ToArray(), repeatedAllDfs.PreOrder.ToArray());
                Assert.Equal(allActual.PostOrder.ToArray(), repeatedAllDfs.PostOrder.ToArray());
                Assert.Equal(allActual.EdgeClassifications.ToArray(), repeatedAllDfs.EdgeClassifications.ToArray());
            }
        }
    }

    private static void AssertWitnesses(
        ReferenceGraph reference,
        Graph<int, int> graph,
        IReadOnlyList<VertexId> roots,
        GraphDirection direction,
        bool useFilter,
        ReachabilityResult actual,
        IReadOnlyList<int?> expectedDistances)
    {
        var rootSet = roots.Select(root => root.Value).ToHashSet();
        for (var vertex = 0; vertex < reference.VertexCount; vertex++)
        {
            if (expectedDistances[vertex] is not { } expectedDistance)
            {
                continue;
            }

            var current = vertex;
            var steps = 0;
            while (!rootSet.Contains(current))
            {
                var firstEdge = actual.FirstEdgeTo(new VertexId(current));
                Assert.True(firstEdge.HasValue, $"Vertex {current} has no witness before reaching a root.");
                var edgeId = firstEdge!.Value;
                var edge = graph.GetEdge(edgeId);
                if (useFilter)
                {
                    Assert.True(reference.Included[edgeId.Value]);
                }

                var from = direction == GraphDirection.Outgoing ? edge.Source.Value : edge.Target.Value;
                var to = direction == GraphDirection.Outgoing ? edge.Target.Value : edge.Source.Value;
                Assert.Equal(current, to);
                current = from;
                steps++;
                Assert.True(steps <= reference.VertexCount, "Witness contains a cycle.");
            }

            Assert.Equal(expectedDistance, steps);
        }
    }

    private static void AssertDepthFirstMatches(
        ReferenceGraph reference,
        GraphDirection direction,
        (List<int> PreOrder, List<int> PostOrder, (int Edge, DepthFirstEdgeKind Kind)[] Classifications) expected,
        DepthFirstResult actual,
        string context)
    {
        Assert.True(
            expected.PreOrder.Select(value => new VertexId(value)).SequenceEqual(actual.PreOrder),
            $"{context}: pre-order differs");
        Assert.True(
            expected.PostOrder.Select(value => new VertexId(value)).SequenceEqual(actual.PostOrder),
            $"{context}: post-order differs");
        Assert.True(
            expected.Classifications.Select(item =>
                new DepthFirstEdgeClassification(new EdgeId(item.Edge), item.Kind))
                .SequenceEqual(actual.EdgeClassifications),
            $"{context}: expected [{string.Join(',', expected.Classifications)}], actual [{string.Join(',', actual.EdgeClassifications)}]");
        Assert.All(actual.EdgeClassifications, item =>
            Assert.InRange(item.Edge.Value, 0, reference.Edges.Count - 1));

        var visited = actual.PreOrder.Select((vertex, index) => (vertex.Value, index))
            .ToDictionary(item => item.Value, item => item.index);
        var postOrderIndex = actual.PostOrder.Select((vertex, index) => (vertex.Value, index))
            .ToDictionary(item => item.Value, item => item.index);
        var oriented = reference.Oriented(direction);
        Assert.All(actual.EdgeClassifications, item =>
        {
            var (from, to) = oriented[item.Edge.Value];
            Assert.Contains(new VertexId(from), actual.PreOrder);
            Assert.Contains(new VertexId(to), actual.PreOrder);
        });
        Assert.Equal(visited.Count, actual.PreOrder.Length);
        Assert.Equal(postOrderIndex.Count, actual.PostOrder.Length);
    }

    private static void AssertTopologicalWhenAcyclic(
        ReferenceGraph reference,
        GraphDirection direction,
        DepthFirstResult result)
    {
        if (result.EdgeClassifications.Any(item => item.Kind == DepthFirstEdgeKind.Back))
        {
            return;
        }

        var reversePostOrder = result.PostOrder.Reverse()
            .Select((vertex, index) => (vertex.Value, index))
            .ToDictionary(item => item.Value, item => item.index);
        var oriented = reference.Oriented(direction);
        foreach (var classification in result.EdgeClassifications)
        {
            var (from, to) = oriented[classification.Edge.Value];
            Assert.True(reversePostOrder[from] < reversePostOrder[to]);
        }
    }

    private static string[] FormatComponents<TVertex, TEdge>(
        StronglyConnectedComponentsResult<TVertex, TEdge> components) =>
        components.Components
            .Select(component => string.Join(',', component.Select(vertex => vertex.Value)))
            .ToArray();

    private static (int Source, int Target, EdgeId Payload)[] CondensationEdges(
        Graph<ComponentId, EdgeId> graph) =>
        Enumerable.Range(0, graph.EdgeCount)
            .Select(index =>
            {
                var edge = graph.GetEdge(new EdgeId(index));
                return (edge.Source.Value, edge.Target.Value, edge.Value);
            })
            .ToArray();
}
