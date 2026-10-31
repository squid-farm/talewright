using Talewright.Graphs;

namespace Talewright.Graphs.Tests;

internal sealed record ReferenceGraph(
    int VertexCount,
    IReadOnlyList<(int Source, int Target)> Edges,
    IReadOnlyList<bool> Included)
{
    public (int From, int To)[] Oriented(GraphDirection direction) =>
        Edges.Select(edge => direction == GraphDirection.Outgoing
            ? (edge.Source, edge.Target)
            : (edge.Target, edge.Source)).ToArray();

    public Graph<int, int> Build()
    {
        var builder = new GraphBuilder<int, int>();
        for (var vertex = 0; vertex < VertexCount; vertex++)
        {
            builder.AddVertex(vertex);
        }

        for (var edge = 0; edge < Edges.Count; edge++)
        {
            var (source, target) = Edges[edge];
            builder.AddEdge(new VertexId(source), new VertexId(target), edge);
        }

        return builder.Build();
    }

    public (List<int> Order, int?[] FirstEdge) Reachability(
        IReadOnlyList<int> roots,
        GraphDirection direction,
        bool useFilter)
    {
        var oriented = Oriented(direction);
        var reached = new bool[VertexCount];
        var firstEdge = new int?[VertexCount];
        var order = new List<int>();
        var layer = new List<int>();

        foreach (var root in roots)
        {
            if (reached[root])
            {
                continue;
            }

            reached[root] = true;
            order.Add(root);
            layer.Add(root);
        }

        while (layer.Count > 0)
        {
            var nextLayer = new List<int>();
            foreach (var vertex in layer)
            {
                for (var edge = 0; edge < oriented.Length; edge++)
                {
                    if (oriented[edge].From != vertex
                        || useFilter && !Included[edge]
                        || reached[oriented[edge].To])
                    {
                        continue;
                    }

                    var target = oriented[edge].To;
                    reached[target] = true;
                    firstEdge[target] = edge;
                    order.Add(target);
                    nextLayer.Add(target);
                }
            }

            layer = nextLayer;
        }

        return (order, firstEdge);
    }

    public int?[] Distances(
        IReadOnlyList<int> roots,
        GraphDirection direction,
        bool useFilter)
    {
        const int unreachable = int.MaxValue / 2;
        var oriented = Oriented(direction);
        var distances = new int[VertexCount, VertexCount];
        for (var source = 0; source < VertexCount; source++)
        {
            for (var target = 0; target < VertexCount; target++)
            {
                distances[source, target] = source == target ? 0 : unreachable;
            }
        }

        for (var edge = 0; edge < oriented.Length; edge++)
        {
            if (!useFilter || Included[edge])
            {
                var from = oriented[edge].From;
                var to = oriented[edge].To;
                distances[from, to] = Math.Min(distances[from, to], 1);
            }
        }

        for (var via = 0; via < VertexCount; via++)
        {
            for (var source = 0; source < VertexCount; source++)
            {
                for (var target = 0; target < VertexCount; target++)
                {
                    distances[source, target] = Math.Min(
                        distances[source, target],
                        distances[source, via] + distances[via, target]);
                }
            }
        }

        var fromRoots = new int[VertexCount];
        Array.Fill(fromRoots, unreachable);
        foreach (var root in roots)
        {
            for (var target = 0; target < VertexCount; target++)
            {
                fromRoots[target] = Math.Min(fromRoots[target], distances[root, target]);
            }
        }

        return fromRoots.Select(distance => distance == unreachable ? (int?)null : distance).ToArray();
    }

    public (List<int> PreOrder, List<int> PostOrder, (int Edge, DepthFirstEdgeKind Kind)[] Classifications)
        DepthFirst(
            IReadOnlyList<int> roots,
            GraphDirection direction,
            bool useFilter)
    {
        var oriented = Oriented(direction);
        var state = new byte[VertexCount];
        var discoveredAt = new int[VertexCount];
        var finishedAt = new int[VertexCount];
        var kinds = new DepthFirstEdgeKind?[Edges.Count];
        var pointsToFinishedVertex = new bool[Edges.Count];
        var preOrder = new List<int>();
        var postOrder = new List<int>();
        var clock = 0;

        void Visit(int vertex)
        {
            state[vertex] = 1;
            discoveredAt[vertex] = ++clock;
            preOrder.Add(vertex);

            for (var edge = 0; edge < oriented.Length; edge++)
            {
                if (oriented[edge].From != vertex || useFilter && !Included[edge])
                {
                    continue;
                }

                var target = oriented[edge].To;
                if (state[target] == 0)
                {
                    kinds[edge] = DepthFirstEdgeKind.Tree;
                    Visit(target);
                }
                else if (state[target] == 1)
                {
                    kinds[edge] = DepthFirstEdgeKind.Back;
                }
                else
                {
                    pointsToFinishedVertex[edge] = true;
                }
            }

            state[vertex] = 2;
            finishedAt[vertex] = ++clock;
            postOrder.Add(vertex);
        }

        foreach (var root in roots)
        {
            if (state[root] == 0)
            {
                Visit(root);
            }
        }

        for (var edge = 0; edge < kinds.Length; edge++)
        {
            if (!pointsToFinishedVertex[edge])
            {
                continue;
            }

            var (from, to) = oriented[edge];
            kinds[edge] = discoveredAt[from] < discoveredAt[to]
                && finishedAt[to] < finishedAt[from]
                    ? DepthFirstEdgeKind.Forward
                    : DepthFirstEdgeKind.Cross;
        }

        var classifications = kinds
            .Select((kind, edge) => (edge, kind))
            .Where(item => item.kind.HasValue)
            .Select(item => (item.edge, item.kind!.Value))
            .ToArray();
        return (preOrder, postOrder, classifications);
    }

    public bool[,] Closure(GraphDirection direction, bool useFilter)
    {
        var oriented = Oriented(direction);
        var reachable = new bool[VertexCount, VertexCount];
        for (var vertex = 0; vertex < VertexCount; vertex++)
        {
            reachable[vertex, vertex] = true;
        }

        for (var edge = 0; edge < oriented.Length; edge++)
        {
            if (!useFilter || Included[edge])
            {
                reachable[oriented[edge].From, oriented[edge].To] = true;
            }
        }

        for (var via = 0; via < VertexCount; via++)
        {
            for (var source = 0; source < VertexCount; source++)
            {
                for (var target = 0; target < VertexCount; target++)
                {
                    reachable[source, target] |= reachable[source, via] && reachable[via, target];
                }
            }
        }

        return reachable;
    }
}
