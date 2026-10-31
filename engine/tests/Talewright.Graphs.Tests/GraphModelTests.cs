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
    public void AdjacencyUsesStableEdgeInsertionOrderAndKeepsLoopsAndParallelEdges()
    {
        var builder = new GraphBuilder<string, string>();
        builder.AddVertex("a");
        builder.AddVertex("b");
        builder.AddVertex("c");
        var first = builder.AddEdge("a", "b", "first");
        var second = builder.AddEdge("c", "b", "second");
        var parallel = builder.AddEdge("a", "b", "parallel");
        var selfLoop = builder.AddEdge("b", "b", "loop");
        var graph = builder.Build();

        Assert.Equal(new[] { first, parallel }, graph.OutgoingEdges(new VertexId(0)).ToArray());
        Assert.Equal(new[] { first, second, parallel, selfLoop }, graph.IncomingEdges(new VertexId(1)).ToArray());
        Assert.Equal(new VertexId(1), graph.GetEdge(selfLoop).Target);
        Assert.Equal("parallel", graph.GetEdge(parallel).Value);
    }

    [Fact]
    public void DuplicateKeysAndUnknownEndpointsThrow()
    {
        var builder = new GraphBuilder<string, int>();
        builder.AddVertex("known");

        Assert.Throws<ArgumentException>(() => builder.AddVertex("known"));
        Assert.Throws<KeyNotFoundException>(() => builder.AddEdge("missing", "known", 1));
        Assert.Throws<KeyNotFoundException>(() => builder.AddEdge("known", "missing", 1));
    }

    [Fact]
    public void BuiltGraphIsAnIndependentSnapshot()
    {
        var builder = new GraphBuilder<int, string>();
        builder.AddVertex(10);
        var graph = builder.Build();
        builder.AddVertex(20);
        builder.AddEdge(10, 20, "later");

        Assert.Equal(1, graph.VertexCount);
        Assert.Equal(0, graph.EdgeCount);
    }

    [Fact]
    public void GraphAndAlgorithmsRejectOutOfRangeIds()
    {
        var builder = new GraphBuilder<int, int>();
        builder.AddVertex(0);
        builder.AddVertex(1);
        builder.AddEdge(0, 1, 0);
        var graph = builder.Build();
        var components = GraphStronglyConnectedComponents.Find(graph);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetVertexKey(new VertexId(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetVertexKey(new VertexId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.OutgoingEdges(new VertexId(2)).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.IncomingEdges(new VertexId(-1)).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.GetEdge(new EdgeId(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphReachability.Find(graph, [new VertexId(2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDepthFirstSearch.Run(graph, [new VertexId(-1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => components.GetComponentOf(new VertexId(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => components.IsLoop(new ComponentId(2)));
    }

    [Fact]
    public void GraphAssemblyReferencesOnlyTheDotNetFramework()
    {
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => AssemblyName.GetAssemblyName(path).Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = typeof(GraphBuilder<,>).Assembly.GetReferencedAssemblies();

        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.Contains(reference.Name!, trustedPlatformAssemblies));
    }
}
