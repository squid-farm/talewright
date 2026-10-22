# Performance

The initial Release benchmark compiles **10,000 generated scenes** with substitutions,
effects and choices, and restores **10,000-choice histories**:

| Operation | Median | Observed p95 |
|---|---:|---:|
| Compile 10,000 scenes | 138 ms | 145 ms |
| Load in-memory story ZIP + compile | 135 ms | 149 ms |
| Restore 10,000 choices | 106 ms | 115 ms |
| Back + Forward at 10,000 choices | 112 ms | 172 ms |
| Serialize a 10,000-choice save | 0.23 ms | 0.24 ms |

Published, trimmed WebAssembly player tests also loaded a generated 10,000-scene
story and reached its first playable passage in **1.44 s in Chromium, 1.86 s in
Firefox and 1.33 s in Playwright WebKit** (one integration measurement per browser,
including archive generation/upload, compilation and rendering; not p95 values).

These are recorded local measurements on **Linux Mint 22.3 x64 / .NET 10.0.13**,
not universal performance guarantees. The .NET table uses warmed execution with
tiered compilation disabled, seven samples per operation, and nearest-rank p95
(the largest sample). The synthetic story has one Tale file of about 1.24 MB;
these numbers do **not** measure cold process/disk startup, complex real-world
stories or maximum file/byte limits. Compile and restore allocated approximately
118 MiB and 81 MiB respectively; those are managed allocations, not peak memory.
Compilation p95 reached 878 ms during a separate run under concurrent build load.

See [methodology, reproduction commands and recorded results](../../tests/README.md#reproducible-performance-budgets).
The broader **10 / 100 / 500 / 1,000 real-file BenchmarkDotNet suite** remains
tracked in [#57](https://github.com/squid-farm/talewright/issues/57); it has not yet
produced results.

