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

Recorded on 2026-11-03: Linux Mint 22.3 x64, .NET 10.0.14,
AMD Ryzen 9 7940HS (8 cores / 16 threads), 27 GiB RAM, performance power profile
and CPU governor. Other work was active (load average about 5). Seven samples
follow three warmups; p95 is the maximum of seven, not a high-confidence tail estimate.

| Operation | Median managed bytes | Maximum managed bytes |
| --- | ---: | ---: |
| Fresh full workspace analysis | 37,990,136 (53.51 B/source byte) | 38,246,824 |
| One-file edit workspace analysis | 36,442,944 (51.33 B/source byte) | 36,553,432 |

These allocations use the **process-wide** counter so worker-thread allocations
are included. They are not directly comparable to current-thread-only probes.
Retained syntax was approximately 664 KB, excluding source strings already shared
with the source snapshot; this is not total retained analysis or peak memory.
A deterministic current-thread test guards the synchronous cached analysis core
at 54 B/source byte, with exact parser-invocation and obsolete-snapshot GC tests.
Fresh compiler and analyzer ceilings remain unchanged.

| End-to-end stage | p50 ms | p95 ms |
| --- | ---: | ---: |
| Notification sent to diagnostics flushed and received | 237.92 | 252.86 |
| Trailing debounce | 150.19 | 150.27 |
| Analysis-gate queue | 0.00 | 0.01 |
| Whole-story analysis, with one file parsed | 85.70 | 102.15 |
| Completion-event dispatch | 0.06 | 0.14 |
| Lint and diagnostic conversion | 0.66 | 6.87 |
| Protocol serialization and publication | 0.15 | 0.26 |
| Scheduling, transport and client-receipt residual | 0.70 | 1.70 |

Stage percentiles are independent and must not be added. This uses the real
in-process LSP framing, workspace, linting and publication pipeline, not a mocked
analyzer or an analyzer-only latency. An internal opt-in observer exposes stage
timings to the harness; no telemetry is emitted by the normal server.

**The 50 ms edit-to-diagnostics target is missed, and that is accepted for
now.** File reuse removes repeated parsing, but whole-story analysis alone
remains over budget. The fixed 150 ms quiet window is deliberate: it coalesces
edits arriving less than 150 ms apart into one analysis instead of starting
whole-story work per keystroke. Adaptive or shorter windows are deferred until
authors report on real editing experience. The report does not enable per-scene
reuse (#411) or claim that #392's latency target is satisfied.

The measured workload waits for publication between edits, so each sample is
one isolated edit plus the full quiet window. Tests cover burst coalescing,
cancellation of running analysis, paced edits and latest-only publication.

## Current-text completion and hover

The #410 Release harness publishes a warm story-wide snapshot, then sends a
current-buffer edit before each framed in-process LSP completion or hover
request. The edited file contains one scene; the catalog scenes are spread over
files of at most 100 scenes. It reports seven samples after three warmups, with
nearest-rank p95 (the maximum). The source size includes the manifest and all
Tale files.

Recorded locally on Linux Mint 22.3 x64 with .NET 10.0.14, an AMD Ryzen 9 7940HS
(16 logical processors), 27 GiB RAM, tiered compilation disabled and the
performance governor. Build and test work was not run concurrently; desktop
processes remained active. This is a local observation, not a device-wide
guarantee.

| Cross-file scene symbols | Files | UTF-8 bytes | Completion p50 / p95 ms | Hover p50 / p95 ms | Completion managed bytes p50 / max | Hover managed bytes p50 / max |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 3 | 11,917 | 0.21 / 0.32 | 0.21 / 0.30 | 31,640 / 49,312 | 68,600 / 68,640 |
| 1,000 | 12 | 120,818 | 0.30 / 0.35 | 0.24 / 0.32 | 76,400 / 76,440 | 70,216 / 74,872 |
| 10,000 | 102 | 1,236,819 | 2.36 / 5.03 | 1.11 / 1.48 | 518,432 / 519,320 | 81,848 / 104,296 |

All measured p95 values met the 20 ms request target. Managed bytes use the
process-wide counter, including server-thread allocations; they are neither
retained memory nor peak memory. The increase for completion with 10,000 scene
symbols is visible and is retained as a future optimization signal. The
deterministic concurrency tests separately verify that these requests do not
start or await whole-story analysis while the current buffer is being edited.
