# Talewright.Graphs

`Talewright.Graphs` is a dependency-free directed multigraph and a small set of deterministic
analysis algorithms. Its types contain no Talewright story concepts. The caller chooses vertex
keys and edge payloads.

The project targets .NET 8, 9, and 10. It references no project or package and enables trim/AOT
analysis. The library uses only framework assemblies and can run in environments such as
Blazor WebAssembly.

## Model

Create a `GraphBuilder<TVertex, TEdge>`, add all vertex keys, then add edges by their endpoint
keys. Duplicate keys and unknown endpoints throw. `Build()` returns an immutable snapshot; later
builder changes do not affect it.

The graph is a **directed multigraph**. Parallel edges, self-loops, and vertices without edges
are retained. `TEdge` is opaque to the library and can carry any caller-owned data. The graph
does not deep-copy or freeze mutable payload objects.

Vertex and edge ids are distinct value types, dense from zero in insertion order, and belong only
to the graph that created them. Do not persist or transmit these ids; keep durable keys and
payloads in the caller's data. Adjacency methods return edge ids in insertion order and do not
allocate. A neighbouring vertex can occur more than once when parallel edges exist.

The builder creates compressed sparse row (CSR) arrays for outgoing and incoming adjacency with
stable counting sorts. Graph arrays are private; callers read vertex keys, edges, and read-only
adjacency spans through the graph API.

## Algorithms

Each algorithm is a side-effect-free static operation returning an immutable result. Algorithms
work on ids and follow either outgoing or incoming adjacency, without copying a reversed graph.
Optional edge filters select which edges participate. They use explicit stacks or queues, never
recursion. Every id supplied to an algorithm is range-checked.

| Operation | Time | Extra space | Ordering |
| --- | --- | --- | --- |
| Build graph | O(V + E) | O(V + E) | Vertex and edge insertion order |
| Read vertex or edge | O(1) | O(1) | By graph id |
| Read adjacency | O(degree) | O(1) | Edge insertion order |
| Breadth-first reachability | O(V + E) | O(V) | Roots, then adjacency order |
| Depth-first search | O(V + E) | O(V + E) | Roots, remaining vertex ids, then adjacency order |
| Strongly connected components | O(V + E) | O(V + E) | Deterministic topological component order |
| Condensation | O(V + E) | O(V + E) | Original edge insertion order |

Reachability returns vertices in discovery order and the edge first used to reach each non-root
vertex. Breadth-first search finds paths with the fewest edges; ties follow root and adjacency
order. Duplicate roots are ignored.

Depth-first search visits requested roots first, then any unvisited vertices in id order, so every
accepted edge receives one classification. A tree edge discovers a vertex, a back edge points to
the active stack, a forward edge points to a finished descendant, and a cross edge points to any
other finished vertex. Classifications refer to the selected traversal direction; filtered-out
edges do not appear in the result. With no back edges, reverse post-order is a topological order.

Strongly connected components include every vertex, including isolated vertices. Components are
numbered so every selected edge between components goes from a lower number to a higher number in
the selected direction. A component is a loop when it has multiple vertices or a selected
self-loop. The condensation inherits the component analysis direction and filter, keeps one edge
for each selected original edge that crosses components, and stores its original edge id as the
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
