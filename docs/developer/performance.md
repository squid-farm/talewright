# Performance

The Release benchmark compiles **10,000 generated scenes** with substitutions,
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

See [methodology and reproduction commands](../../tests/README.md#reproducible-performance-budgets).
That section also describes the realistic story benchmark, which compiles a generated story with branches,
hubs, loops, conditions and endings; the corpus tiers above are single-path rings that measure size and limits.

## GitHub-hosted real-file measurements

The [Real-file performance workflow](../../.github/workflows/performance.yml) runs a BenchmarkDotNet
suite on a GitHub-hosted `ubuntu-24.04` runner. It covers generated file-count, byte-volume,
scene-count and history-depth corpora, plus browser, Linux Photino and publication workloads.
Each run's report, with runner metadata, is kept in the
[durable performance history](https://github.com/squid-farm/talewright/blob/performance-results/index.md).
Runner power and filesystem cache are uncontrolled, so a single hosted run is a baseline, not a
guarantee or budget. Managed allocations are not peak or retained memory.

See the [performance scorecard](performance-scorecard.md) for a cross-host view
of browser observations, fixed-byte file-layout measurements, end-cursor versus
redo-prefix restore, metric limitations, and the status of proposed budgets.

## Player image warming

The browser regression and measurement test is opt-in with the player E2E suite:

```bash
python3 player/tests/run_e2e.py web --browser chromium --filter FullyQualifiedName~Destination_image_warming_is_reused_and_offscreen_images_remain_lazy
```

One local observation used Linux Mint 22.3 x64, AMD Ryzen 9 7940HS (16 logical CPUs),
.NET 10.0.15 and Chromium 153.0.8010.12. The fixture used a 53,477,376-byte JPEG
(the real image followed by zero padding) in a local uploaded ZIP. Image bytes came
from the archive on the loopback test host; there were no HTTP image requests or
HTTP-cache reuse. Each case used a distinct story owner and Blob URL. The settled
case allowed a 500 ms reading interval after warming; the immediate case navigated
with warming incomplete. Timings stop after scrolling the image into view and
observing a decoded `naturalWidth`.

| Case | Image visible | `Finish` choice displacement |
|---|---:|---:|
| Cold named-save restore into the current scene | 1,544 ms | 473.2 px |
| Immediate navigation with the image transfer held pending until viewport entry | 861 ms | 473.2 px |
| Settled direct-neighbor warming (500 ms reading interval) | 450 ms | 473.2 px |

On the same Linux Mint host, a Linux Photino run used a local folder story and the
899,911-byte source image. With a 500 ms reading interval, choice-to-decoded-image
after scrolling measured **678 ms**. Its method and source differ from the browser
cases, so it is a local-host observation, not a direct cross-host comparison.

The immediate case uses a test-only gate on the browser stream read to make the
in-flight handoff deterministic; it is not a natural-network timing sample. The cold
and warmed cases enter the image through different actions (save restore versus a
choice), so these single observations are diagnostic, not a causal before/after
claim or a performance target. Choice displacement is the document-Y difference
before and after image decode; the identical values show that warming does not reserve
image layout space. The 53 MiB image exercises a large expanded image, but browser
peak/retained memory were not measured.

### Image transfer concurrency observation

A separate Chromium 153 / .NET 10.0.15 local probe used the published WebAssembly
player, an uploaded ZIP containing 10 direct neighbors with two distinct paths each,
and the same 899,911-byte JPEG at each path. The ZIP was about 17.95 MB. The
one-transfer-per-neighbor scheduler issued the work; a test-only JavaScript semaphore
then limited concurrent stream reads to 1, 3, 5 or 10. Each setting ran three times.
Elapsed time starts at the first stream call and ends at Blob creation; it does not
include image decoding or measure navigation latency.

| Artificial delay per read | Read limit | First 10 requests, median (range) | All 20 requests, median (range) |
|---|---:|---:|---:|
| 0 ms | 1 | 456 ms (337–515) | 501 ms (380–565) |
| 0 ms | 3 | 348 ms (319–366) | 392 ms (361–410) |
| 0 ms | 5 | 320 ms (298–368) | 370 ms (343–424) |
| 0 ms | 10 | 298 ms (281–300) | 331 ms (314–342) |
| 50 ms | 1 | 603 ms (597–605) | 1,134 ms (1,128–1,134) |
| 50 ms | 3 | 291 ms (287–301) | 469 ms (462–476) |
| 50 ms | 5 | 278 ms (272–300) | 385 ms (378–406) |
| 50 ms | 10 | 321 ms (252–332) | 375 ms (306–389) |

This compares a semaphore inside the browser stream bridge, not different scheduler
implementations: all ten first-neighbor calls are already issued in every case. The
50 ms delay is synthetic, and the local ZIP does not exercise browser HTTP connection
limits. The higher limit helped the undelayed local workload, while the delayed
5-versus-10 results overlap; three samples do not establish an ideal concurrency.
Peak browser memory and other devices or network conditions were not measured.

## File syntax reuse and edit diagnostics

This measurement uses the deterministic 20-chapter realistic corpus:
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

**The 50 ms edit-to-diagnostics target is missed, and that is accepted.** File reuse removes repeated parsing, but whole-story analysis alone
remains over budget. The fixed 150 ms quiet window is deliberate: it coalesces
edits arriving less than 150 ms apart into one analysis instead of starting
whole-story work per keystroke. Adaptive or shorter windows are deferred until
authors report on real editing experience. Reusing unchanged scenes, rather than whole files, is
tracked in [#411](https://github.com/squid-farm/talewright/issues/411).

The measured workload waits for publication between edits, so each sample is
one isolated edit plus the full quiet window. Tests cover burst coalescing,
cancellation of running analysis, paced edits and latest-only publication.

## Current-text completion and hover

The Release harness publishes a warm story-wide snapshot, then sends a
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
retained memory nor peak memory. Completion allocations still grow with the
story-wide symbol count (about 76 KB per request at 1,000 scene symbols and
about 518 KB at 10,000), while hover stays roughly flat. That growth is a
known optimization target. The deterministic concurrency tests
separately verify that these requests do not start or await whole-story
analysis while the current buffer is being edited.
