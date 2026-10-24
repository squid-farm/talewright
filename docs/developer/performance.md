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

## First GitHub-hosted real-file baseline

The first successful [Real-file performance workflow run](https://github.com/squid-farm/talewright/actions/runs/39902796074)
completed on 2026-10-23. Its BenchmarkDotNet suite measured 106 cases, including generated file-count,
byte-volume, scene-count, and history-depth corpora. Two representative
independent-dimension results are:

| Operation | Mean | p95 | Managed allocations per operation |
|---|---:|---:|---:|
| Load and compile generated 10,000-scene corpus from disk | 398.3 ms | 418.5 ms | 719.87 MB |
| Restore generated 10,000-choice history | 132.2 ms | 139.8 ms | 91.73 MB |

These are results from one GitHub-hosted `ubuntu-24.04` run (.NET 10.0.13,
Ubuntu 24.04.5, four-core AMD EPYC 7763 runner), with five warmups and thirty
measured iterations per BenchmarkDotNet case. Runner power and filesystem cache
are uncontrolled. Managed allocations are not peak or retained memory, and a
single hosted run is a recorded baseline—not a performance guarantee or budget.

The browser and Linux Photino integration jobs passed, and all three publication
workloads completed. See the [full run report](https://github.com/squid-farm/talewright/blob/performance-results/reports/39902796074-1.md)
for all 106 benchmark cases, player observations, publication timings and runner
metadata, or browse the [durable performance history](https://github.com/squid-farm/talewright/blob/performance-results/index.md).
The full deterministic corpus, BenchmarkDotNet output and integration evidence
are available as [GitHub Actions artifacts](https://github.com/squid-farm/talewright/actions/runs/39902796074)
(retained for 30 days).

See the [performance scorecard](performance-scorecard.md) for a cross-host view
of browser observations, fixed-byte file-layout measurements, end-cursor versus
redo-prefix restore, metric limitations, and the status of proposed budgets.
