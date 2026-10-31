using System.Reflection;
using Talewright.Graphs;

namespace Talewright.Graphs.Tests;

public sealed class GraphModelTests
{
    [Fact]
    public void EmptyAndUnconnectedGraphsKeepTheirVertices()
    {
        var empty = new GraphBuilder<string, int>().Build();
        Assert.Equal(0, empty.VertexCount);
        Assert.Equal(0, empty.EdgeCount);
        Assert.Empty(GraphStronglyConnectedComponents.Find(empty).Components);
        Assert.Empty(GraphDepthFirstSearch.FromAllVertices(empty).PreOrder);
        Assert.Empty(GraphDepthFirstSearch.FromAllVertices(empty).PostOrder);
        Assert.Empty(GraphDepthFirstSearch.FromAllVertices(empty).EdgeClassifications);
        Assert.Empty(GraphReachability.Find(empty, []).Order);

        var singleBuilder = new GraphBuilder<string, int>();
        singleBuilder.AddVertex("only");
        var single = singleBuilder.Build();
        var singleComponent = GraphStronglyConnectedComponents.Find(single);
        Assert.Equal(1, single.VertexCount);
        Assert.Equal(0, single.EdgeCount);
        Assert.Single(singleComponent.Components);
        Assert.False(singleComponent.IsLoop(new ComponentId(0)));

        var builder = new GraphBuilder<string, int>();
        builder.AddVertex("first");
        builder.AddVertex("second");
        builder.AddVertex("third");
        var graph = builder.Build();

        Assert.Equal(3, graph.VertexCount);
        Assert.Equal(0, graph.EdgeCount);
        Assert.All(Enumerable.Range(0, 3), index =>
        {
            var vertex = new VertexId(index);
            Assert.Empty(graph.OutgoingEdges(vertex).ToArray());
            Assert.Empty(graph.IncomingEdges(vertex).ToArray());
        });
    }

    [Fact]
    public void AddVertexAndAddEdgeReturnSequentialIds()
    {
        var builder = new GraphBuilder<string, int>();
        Assert.Equal(new VertexId(0), builder.AddVertex("same"));
        Assert.Equal(new VertexId(1), builder.AddVertex("same"));
        Assert.Equal(new EdgeId(0), builder.AddEdge(new VertexId(0), new VertexId(1), 5));
        Assert.Equal(new EdgeId(1), builder.AddEdge(new VertexId(1), new VertexId(0), 6));

        var graph = builder.Build();
        Assert.Equal("same", graph.GetVertexValue(new VertexId(0)));
        Assert.Equal("same", graph.GetVertexValue(default));
        Assert.Equal("same", graph.GetVertexValue(new VertexId(1)));
        Assert.Equal(2, graph.VertexCount);
        Assert.Equal(6, graph.GetEdge(new EdgeId(1)).Value);

        var nullableBuilder = new GraphBuilder<string?, int>();
        var nullVertex = nullableBuilder.AddVertex(null);
        var nullableGraph = nullableBuilder.Build();
        Assert.Null(nullableGraph.GetVertexValue(nullVertex));
    }

    [Fact]
    public void AdjacencyUsesStableEdgeInsertionOrderAndKeepsLoopsAndParallelEdges()
    {
        var builder = new GraphBuilder<string, string>();
        var a = builder.AddVertex("a");
        var b = builder.AddVertex("b");
        var c = builder.AddVertex("c");
        var first = builder.AddEdge(a, b, "first");
        var second = builder.AddEdge(c, b, "second");
        var parallel = builder.AddEdge(a, b, "parallel");
        var selfLoop = builder.AddEdge(b, b, "loop");
        var graph = builder.Build();

        Assert.Equal(new[] { first, parallel }, graph.OutgoingEdges(a).ToArray());
        Assert.Equal(new[] { first, second, parallel, selfLoop }, graph.IncomingEdges(b).ToArray());
        Assert.Equal(b, graph.GetEdge(selfLoop).Target);
        Assert.Equal("parallel", graph.GetEdge(parallel).Value);
    }

    [Fact]
    public void AddEdgeRejectsVerticesNotYetAdded()
    {
        var builder = new GraphBuilder<int, int>();
        builder.AddVertex(0);
        builder.AddVertex(1);

        var sourceException = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddEdge(new VertexId(-1), new VertexId(0), 0));
        Assert.Equal("source", sourceException.ParamName);
        var targetException = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddEdge(new VertexId(0), new VertexId(2), 0));
        Assert.Equal("target", targetException.ParamName);

        builder.AddVertex(2);
        Assert.Equal(new EdgeId(0), builder.AddEdge(new VertexId(0), new VertexId(2), 0));
        Assert.Equal(1, builder.Build().EdgeCount);
    }

    [Fact]
    public void BuiltGraphIsAnIndependentSnapshot()
    {
        var builder = new GraphBuilder<int, string>();
        var first = builder.AddVertex(10);
        var graph = builder.Build();
        var second = builder.AddVertex(20);
        builder.AddEdge(first, second, "later");

        Assert.Equal(1, graph.VertexCount);
        Assert.Equal(0, graph.EdgeCount);
    }

    [Fact]
    public void GraphAndAlgorithmsRejectInvalidArguments()
    {
        var builder = new GraphBuilder<int, int>();
        var first = builder.AddVertex(0);
        var second = builder.AddVertex(1);
        builder.AddEdge(first, second, 0);
        var graph = builder.Build();
        var components = GraphStronglyConnectedComponents.Find(graph);
        var reachability = GraphReachability.Find(graph, [first]);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetVertexValue(new VertexId(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetVertexValue(new VertexId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.OutgoingEdges(new VertexId(2)).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.IncomingEdges(new VertexId(-1)).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetEdge(new EdgeId(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphReachability.Find(graph, [new VertexId(2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDepthFirstSearch.FromRoots(graph, [new VertexId(-1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => components.GetComponentOf(new VertexId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => components.IsLoop(new ComponentId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => reachability.IsReached(new VertexId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => reachability.FirstEdgeTo(new VertexId(-1)));

        var invalidDirection = (GraphDirection)2;
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphReachability.Find(graph, [first], invalidDirection));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDepthFirstSearch.FromRoots(graph, [first], invalidDirection));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDepthFirstSearch.FromAllVertices(graph, invalidDirection));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphStronglyConnectedComponents.Find(graph, invalidDirection));

        Assert.Throws<ArgumentNullException>(() => GraphReachability.Find<int, int>(null!, []));
        Assert.Throws<ArgumentNullException>(() => GraphReachability.Find(graph, null!));
        Assert.Throws<ArgumentNullException>(() => GraphDepthFirstSearch.FromRoots<int, int>(null!, []));
        Assert.Throws<ArgumentNullException>(() => GraphDepthFirstSearch.FromRoots(graph, null!));
        Assert.Throws<ArgumentNullException>(() => GraphDepthFirstSearch.FromAllVertices<int, int>(null!));
        Assert.Throws<ArgumentNullException>(() => GraphStronglyConnectedComponents.Find<int, int>(null!));
        Assert.Throws<ArgumentNullException>(() => GraphCondensation.Build<int, int>(null!, components));
        Assert.Throws<ArgumentNullException>(() => GraphCondensation.Build(graph, null!));

        var otherGraphBuilder = new GraphBuilder<int, int>();
        otherGraphBuilder.AddVertex(1);
        Assert.Throws<ArgumentException>(() => GraphCondensation.Build(otherGraphBuilder.Build(), components));
    }

    [Fact]
    public void LibraryNeverComparesHashesOrFormatsValues()
    {
        var values = Enumerable.Range(0, 3).Select(_ => new PoisonValue()).ToArray();
        var payloads = Enumerable.Range(0, 5).Select(_ => new PoisonValue()).ToArray();
        var builder = new GraphBuilder<PoisonValue, PoisonValue>();
        var vertices = values.Select(builder.AddVertex).ToArray();
        var edges = new[]
        {
            builder.AddEdge(vertices[0], vertices[1], payloads[0]),
            builder.AddEdge(vertices[0], vertices[1], payloads[1]),
            builder.AddEdge(vertices[1], vertices[1], payloads[2]),
            builder.AddEdge(vertices[1], vertices[2], payloads[3]),
            builder.AddEdge(vertices[2], vertices[0], payloads[4])
        };
        var graph = builder.Build();

        Assert.Same(values[0], graph.GetVertexValue(vertices[0]));
        Assert.Same(values[1], graph.GetVertexValue(vertices[1]));
        Assert.Same(values[2], graph.GetVertexValue(vertices[2]));
        for (var index = 0; index < edges.Length; index++)
        {
            Assert.Same(payloads[index], graph.GetEdge(edges[index]).Value);
        }

        var firstEdge = graph.GetEdge(edges[0]);
        Assert.True(firstEdge.Equals(graph.GetEdge(edges[0])));
        Assert.False(firstEdge.Equals(graph.GetEdge(edges[1])));
        _ = firstEdge.GetHashCode();
        Assert.Equal("GraphEdge { Id = EdgeId { Value = 0 } }", firstEdge.ToString());

        GraphReachability.Find(graph, [vertices[0]]);
        GraphReachability.Find(graph, [vertices[2]], GraphDirection.Incoming);
        GraphDepthFirstSearch.FromRoots(graph, [vertices[0]]);
        GraphDepthFirstSearch.FromAllVertices(graph);
        var components = GraphStronglyConnectedComponents.Find(graph);
        GraphCondensation.Build(graph, components);
    }

    [Fact]
    public void GraphAssemblyReferencesOnlyTheDotNetFramework()
    {
        var assembly = typeof(GraphBuilder<,>).Assembly;
        Assert.NotEmpty(assembly.GetReferencedAssemblies());
        Assert.Empty(NonFrameworkReferences(assembly));
    }

    [Fact]
    public void FrameworkReferenceCheckFlagsNonFrameworkAssemblies()
    {
        var flagged = NonFrameworkReferences(typeof(GraphModelTests).Assembly);
        Assert.Contains("Talewright.Graphs", flagged);
        Assert.Contains("xunit.assert", flagged, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> NonFrameworkReferences(Assembly assembly)
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => !File.Exists(Path.Combine(frameworkDirectory, name + ".dll")))
            .ToArray();
    }

    private sealed class PoisonValue
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException();

        public override int GetHashCode() => throw new InvalidOperationException();

        public override string ToString() => throw new InvalidOperationException();
    }
}
