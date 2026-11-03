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
(the largest sample). The synthetic story has one Talemark file of about 1.24 MB;
these numbers do **not** measure cold process/disk startup, complex real-world
stories or maximum file/byte limits. Compile and restore allocated approximately
118 MiB and 81 MiB respectively; those are managed allocations, not peak memory.
Compilation p95 reached 878 ms during a separate run under concurrent build load.

See [methodology, reproduction commands and recorded results](../../tests/README.md#reproducible-performance-budgets).
That section also describes the realistic story benchmark, which compiles a generated story with branches,
hubs, loops, conditions and endings; the corpus tiers above are single-path rings that measure size and limits.

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

## File syntax reuse and edit diagnostics

The #409 measurement uses the deterministic 20-chapter realistic corpus:
22 text files (21 `.tale`), 709,952 UTF-8 source bytes including configuration.
Each warm edit parses one file and rebinds/validates the entire story.

```sh
MSBUILDDISABLENODEREUSE=1 dotnet build benchmarks/Talewright.Benchmarks -c Release -nodeReuse:false -maxcpucount:1
dotnet benchmarks/Talewright.Benchmarks/bin/Release/net10.0/Talewright.Benchmarks.dll --edit-diagnostics
```

Recorded on 2026-11-02: Linux Mint 22.3 x64, .NET 10.0.14,
AMD Ryzen 9 7940HS (8 cores / 16 threads), 27 GiB RAM, performance power profile
and CPU governor. Other work was active (load average about 3). Seven samples
follow three warmups; p95 is the maximum of seven, not a high-confidence tail estimate.

| Operation | Median managed bytes | Maximum managed bytes |
| --- | ---: | ---: |
| Fresh full workspace analysis | 38,237,488 (53.86 B/source byte) | 38,353,928 |
| One-file edit workspace analysis | 36,593,424 (51.54 B/source byte) | 36,809,264 |

These allocations use the **process-wide** counter so worker-thread allocations
are included. They are not directly comparable to current-thread-only probes.
Retained syntax was approximately 664 KB, excluding source strings already shared
with the source snapshot; this is not total retained analysis or peak memory.
A deterministic current-thread test guards the synchronous cached analysis core
at 54 B/source byte, with exact parser-invocation and obsolete-snapshot GC tests.
Fresh compiler and analyzer ceilings remain unchanged.

| End-to-end stage | p50 ms | p95 ms |
| --- | ---: | ---: |
| Notification sent to diagnostics flushed and received | 89.44 | 100.36 |
| Trailing debounce | 10.17 | 10.29 |
| Analysis-gate queue | 0.00 | 0.01 |
| Whole-story analysis, with one file parsed | 77.95 | 88.61 |
| Completion-event dispatch | 0.05 | 0.06 |
| Lint and diagnostic conversion | 0.59 | 3.75 |
| Protocol serialization and publication | 0.12 | 0.15 |
| Scheduling, transport and client-receipt residual | 0.46 | 0.71 |

Stage percentiles are independent and must not be added. This uses the real
in-process LSP framing, workspace, linting and publication pipeline, not a mocked
analyzer or an analyzer-only latency. An internal opt-in observer exposes stage
timings to the harness; no telemetry is emitted by the normal server.

**The 50 ms edit-to-diagnostics target is missed.** File reuse removes repeated
parsing and the fixed 150 ms debounce for isolated edits is gone, but whole-story
analysis alone remains over budget. After a running analysis is cancelled during
typing, the server uses a 150 ms quiet window to coalesce the burst. The report
gates lead review of #411; it does not silently enable per-scene reuse or claim
that #392's latency target is satisfied.

The measured workload waits for publication between edits, so it exercises the
10 ms isolated-edit window. During typing that cancels running analysis, the
server instead waits for 150 ms of quiet until a revision publishes. Tests cover
this backoff, cancellation, paced bursts, latest-only publication and
restoration of the short window; these isolated-edit numbers are not
burst-settling latency.
