# Talewright.Graphs

`Talewright.Graphs` is a dependency-free directed multigraph and a small set of deterministic
analysis algorithms. Its types contain no Talewright story concepts. The caller decides what
vertices and edges mean.

The project targets .NET 8, 9, and 10. It references no project or package and enables trim/AOT
analysis. The library uses only framework assemblies and can run in environments such as
Blazor WebAssembly.

## Model

Create a `GraphBuilder<TVertex, TEdge>`. `AddVertex(value)` returns the new vertex's `VertexId`.
Pass ids returned by that builder to `AddEdge(source, target, value)`, which returns the new
`EdgeId`. The first vertex added is `VertexId(0)`, the next `VertexId(1)`, and so on; edges are
numbered the same way. `AddEdge` throws `ArgumentOutOfRangeException` if either id's numeric value
is outside the builder's vertex range. Ids carry no builder identity, so an in-range id from a
different builder cannot be distinguished. `Build()` returns an immutable snapshot; later builder
changes do not affect it.

The library stores vertex and edge values but never compares, hashes, or formats them. Adding an
equal value twice creates two vertices, and null values are allowed. The caller owns any lookup
from its own keys, such as scene names, to vertex ids, including duplicate detection. The graph
does not deep-copy or freeze mutable values.

`GraphEdge<TEdge>` equality and hashing use only its `EdgeId`; its string representation also
omits the payload. Edge ids are graph-local, so compare edge records only within the graph that
created them.

The graph is a **directed multigraph**. Parallel edges, self-loops, and vertices without edges
are retained.

Vertex and edge ids are distinct value types and belong only to the graph that created them. Do
not persist or transmit them; map them back to caller-owned keys or values. The library rejects
ids outside a graph's range, but cannot detect an in-range id from a different graph.
`default(VertexId)` is vertex 0, which is valid in any non-empty graph, so handle a failed key
lookup explicitly instead of falling back to a default id.

Adjacency methods return edge ids in insertion order and do not allocate. A neighbouring vertex
can occur more than once when parallel edges exist.

The builder creates compressed sparse row (CSR) arrays for outgoing and incoming adjacency with
stable counting sorts. Graph arrays are private; callers read vertex values, edges, and read-only
adjacency spans through the graph API.

## Algorithms

Each algorithm is a side-effect-free static operation returning an immutable result. Algorithms
work on ids and follow either outgoing or incoming edges, without copying a reversed graph.
Optional edge filters select which edges participate. They use explicit stacks or queues, never
recursion. Every id and direction supplied to an algorithm is validated, and root sequences are
enumerated once.

In the table, V is the number of vertices, E the number of edges, and R the number of roots
supplied, counting duplicates. Bounds are worst case and assume that a supplied edge filter runs in
constant time.

| Operation | Time | Extra space | Ordering |
| --- | --- | --- | --- |
| Build graph | O(V + E) | O(V + E) | Vertex and edge insertion order |
| Read vertex or edge | O(1) | O(1) | By graph id |
| Read adjacency | O(degree) | O(1) | Edge insertion order |
| Breadth-first reachability | O(V + E + R) | O(V) | Roots, then adjacency order |
| Depth-first search from roots | O(V + E + R) | O(V + E) | Roots, then adjacency order |
| Depth-first search from all vertices | O(V + E) | O(V + E) | Vertex ids, then adjacency order |
| Strongly connected components | O(V + E) | O(V + E) | Topological; see below |
| Condensation | O(V + E) | O(V + E) | Original edge insertion order |

Reachability returns vertices in discovery order and the edge first used to reach each non-root
vertex. Breadth-first search finds paths with the fewest edges; ties follow root and adjacency
order. Duplicate roots are ignored. The filter is called at most once per edge.

`GraphDepthFirstSearch.FromRoots` visits only vertices reachable from the supplied roots, in root
order. Duplicate roots are ignored, and no roots means nothing is visited. Only edges leaving
visited vertices are classified. `GraphDepthFirstSearch.FromAllVertices` starts from every
unvisited vertex in id order, so every accepted edge is classified. A tree edge discovers a vertex,
a back edge points to the active stack (a self-loop is a back edge), a forward edge points to a
finished descendant, and a cross edge points to any other finished vertex. Classifications refer
to the selected traversal direction; filtered-out edges do not appear. With no back edges, reverse
post-order is a topological order of the visited vertices.

Strongly connected components include every vertex, including isolated vertices. Components are
numbered in a topological order: every selected edge between two components goes from the lower
number to the higher number in the selected direction. When no edge orders two components, the
numbering is deterministic but follows the traversal, not vertex ids: the analysis runs a
depth-first search from all vertices in id order, then numbers components in reverse post-order of
that search. For example, three isolated vertices 0, 1 and 2 become components 0, 1 and 2
containing vertices 2, 1 and 0. Sort components yourself if you need a presentation order.
Vertices within a component are listed in ascending id order. A component is a loop when it has
multiple vertices or a selected self-loop. The filter is called exactly once per edge.

The condensation inherits the component analysis direction and filter. Its vertex `i` is component
`i`, and that vertex's value is the `ComponentId`. It keeps one edge for each selected original
edge that crosses components, in original edge order, and stores the original edge id as the
payload. Edges inside a component are omitted.

## Interpreting analysis

The story-map caller builds its graph from every divert and hub offer and ignores conditions.
That graph includes every transition play can take, plus some transitions play may not be able to
take:

- **Never** answers are reliable: a scene is unreachable, cannot reach END, or cannot leave a
  loop.
- **Possible** answers are only possible: a path exists or a scene can reach END. Conditions may
  prevent play from following that path.
- Filtering edges out preserves reliable **never** answers only if the removed edges truly cannot
  be taken.
- Leaving out a kind of transition makes even **never** answers unreliable.

Proving that a route is playable requires the engine's story tests, not this graph library.

## Adding an algorithm

Add a separate static algorithm type and source file. Operate on the built graph and typed ids;
return an immutable result without changing the graph model. Define direction, filtering,
preconditions, ordering and tie-breaking explicitly. Document time and space complexity. Use an
explicit stack for depth-first work, test adversarial graph shapes and deterministic order, and
avoid references to caller-specific types. Algorithms that enumerate potentially exponential
numbers of paths or cycles must also accept explicit output limits and a `CancellationToken`.
