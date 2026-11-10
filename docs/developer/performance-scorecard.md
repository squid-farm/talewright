# Performance scorecard

This page gathers the evidence for how fast stories open, play and restore. It distinguishes measured latency and allocation from live memory and
process peaks. Results are baselines for comparison, not guarantees. Do not
compare rows from different machines or treat a single browser observation as a
percentile.

## Measurement status

| Dimension | Current evidence | Interpretation and remaining work |
|---|---|---|
| Source read and decode | BenchmarkDotNet directory-load cases; fixed-byte file-layout comparison below | Measures the `StorySource` read path, not editor file-watcher latency |
| Compile | BenchmarkDotNet in-memory compile cases | Isolates compile from directory read/decode |
| Authoring analysis | Realistic-story BenchmarkDotNet case and bounded console workloads | Measures full in-memory `StoryAnalyzer.Analyze`, not edit-to-diagnostic latency |
| Runtime start | `StartupAndFirstFrame` BenchmarkDotNet case | Measures engine session start, not full host startup |
| First playable passage | WebAssembly browser integration in Chromium, Firefox, and WebKit | One observation per browser/corpus; includes archive upload, compilation, and rendering |
| Click to painted passage | Browser integration choice timing | One observation per browser/corpus; not a p95 |
| End-cursor and redo-prefix restore | BenchmarkDotNet compares both with the same 10,000-choice save | The prefix case validates all saved choices and returns to cursor 5,000 |
| Back/Forward | BenchmarkDotNet plus fixed-story browser navigation observations below | Browser observations include replay, component updates, DOM work and focus; native replay is measured separately, not subtracted |
| Managed allocation | BenchmarkDotNet `MemoryDiagnoser`; console allocation ceilings for realistic compile and analysis | Cumulative managed bytes per operation, not retained memory |
| Live managed heap after repeated open/close/restore | Not measured | Needs a stable post-operation collection and retention protocol |
| Process peak RSS | Benchmark worker metadata and standalone publication measurements | Worker peak includes setup and harness; it is not an isolated operation or a live heap measurement |
| Browser process memory | Not measured | `navigator.deviceMemory` is only a browser-reported device-capacity hint, not memory usage |
| Allocation stacks | Not available from the local trace | EventPipe capture completed, but Speedscope conversion yielded no allocation profiles or managed allocation stacks; CPU samples must not be reported as allocation attribution |
| Main-thread blocking | Chromium long-task entries in the browser integration | Firefox and WebKit do not report this metric in the current harness |
| Constrained device | Not measured | Available local host and hosted runners are not representative constrained devices |

## Published browser observations

These figures are the hosted baseline from the
[Real-file performance workflow](../../.github/workflows/performance.yml) run of 2026-10-23. Later runs
are in the [durable performance history](https://github.com/squid-farm/talewright/blob/performance-results/index.md).
The workflow collects one timed integration per browser and corpus, not repeated samples. “First playable” includes uploading the already
generated archive, browser compilation, and passage rendering. Choice measures
the click through painted passage; save and restore exercise browser storage.
All values are milliseconds.

| Browser | Corpus | First playable | Choice | IndexedDB save | History restore |
|---|---|---:|---:|---:|---:|
| Chromium | Typical | 367 | 56 | 10 | 265 |
| Firefox | Typical | 682 | 103 | 24 | 419 |
| WebKit | Typical | 352 | 57 | 12 | 248 |
| Chromium | Medium | 837 | 61 | 11 | 633 |
| Firefox | Medium | 1,577 | 90 | 25 | 1,181 |
| WebKit | Medium | 790 | 90 | 15 | 660 |
| Chromium | Large | 2,607 | 38 | 14 | 1,900 |
| Firefox | Large | 6,155 | 93 | 23 | 3,943 |
| WebKit | Large | 2,457 | 68 | 13 | 1,693 |
| Chromium | Gigantic | 7,456 | 48 | 16 | 3,444 |
| Firefox | Gigantic | 23,437 | 91 | 32 | 8,313 |
| WebKit | Gigantic | 6,853 | 83 | 16 | 3,298 |

The hosted runner was Ubuntu 24.04.5 with a four-core AMD EPYC 7763. The browser
reported four hardware threads; only Chromium exposed `navigator.deviceMemory`
(16 GB). The browser user agent is not a reliable substitute for the runner OS.
Chromium reported six long tasks totaling 739 ms on Typical and six totaling
10,045 ms on Gigantic. The harness does not collect long tasks in
Firefox or WebKit. Low-compressibility cases passed playability checks, but their
timing and resource fields were not recorded.

The [full run report](https://github.com/squid-farm/talewright/blob/performance-results/reports/39902796074-1.md)
and [structured run data](https://github.com/squid-farm/talewright/blob/performance-results/runs/39902796074-1.json)
contain runner metadata, all 106 BenchmarkDotNet rows, the complete browser
observations, and three publication workloads. Browser memory use and
device-specific percentiles are not present in that data.

## Isolated local comparisons

Two focused BenchmarkDotNet runs were made on Linux Mint 22.3 x64 with .NET
10.0.13, BenchmarkDotNet 0.15.2, and an AMD Ryzen 9 7940HS (16 logical
processors). Each case used one launch, five warmups, and thirty measured
iterations. BenchmarkDotNet could not elevate process priority in this
environment, and some cases reported multimodal distributions or outliers.
These local results are for reproduction and comparison, not a stable device
budget. They were taken before the source-loading, fingerprint, parser and
single-replay optimizations below, so current code allocates less.

### Fixed-byte, fixed-scene file layout

The two deterministic corpora both contain 100 scenes, 110 choices, and exactly
5 MiB expanded source. One has two story files (one large Talemark file); the other
has 100 story files (99 small Talemark files). The benchmark reports mean, observed
p95, standard deviation, and allocated bytes.

| Operation | Layout | Mean | p95 | Std. dev. | Allocated |
|---|---|---:|---:|---:|---:|
| Read/decode directory | 2 files | 11.63 ms | 12.49 ms | 0.49 ms | 30.15 MiB |
| Read/decode directory | 100 files | 9.76 ms | 10.37 ms | 0.47 ms | 33.92 MiB |
| Compile loaded source | 2 files | 16.65 ms | 17.59 ms | 0.60 ms | 26.58 MiB |
| Compile loaded source | 100 files | 11.94 ms | 12.62 ms | 0.37 ms | 26.66 MiB |
| Read/decode and compile | 2 files | 26.77 ms | 28.16 ms | 0.91 ms | 56.72 MiB |
| Read/decode and compile | 100 files | 27.33 ms | 32.66 ms | 2.98 ms | 60.56 MiB |

The fixed-input comparison isolates file layout better than the
independent file-count series, but this single local run is not enough to infer
that either layout is generally faster.

### Restore at end and before the saved timeline end

Both measurements use the same 10,000-choice Gigantic save. The second save has
its cursor at 5,000 and retains 5,000 choices to redo; restore validates the
entire saved timeline before returning to that cursor.

| Operation | Mean | p95 | Std. dev. | Allocated |
|---|---:|---:|---:|---:|
| Restore at end cursor (10,000) | 213.3 ms | 262.3 ms | 26.73 ms | 123.06 MiB |
| Restore with redo prefix (cursor 5,000 of 10,000) | 173.1 ms | 182.5 ms | 5.86 ms | 101.51 MiB |

The BenchmarkDotNet worker peak was about 918 MiB for this run and includes the
corpus, setup, and harness. It is not an isolated process peak for restore.

The Typical 100-choice local smoke measured `StartupAndFirstFrame` at 19.38 μs
p95 and Back/Forward at 3.10 ms p95. Both had multimodal-distribution warnings;
the Back/Forward iteration was below BenchmarkDotNet's recommended duration.
Do not use those short observations to set a product budget.

## Optimizations in place

Each of these was kept because it cut managed allocation without changing output. The
figures are local before/after means (Linux Mint 22.3, Ryzen 9 7940HS, .NET 10.0.13,
Release, AC power), not guarantees. Use the matching filter to re-measure.

| Change | Effect | Benchmark filter |
|---|---|---|
| `StorySource.ReadText` decodes through a bounded 4 KiB `StreamReader` instead of copying each file into a `MemoryStream` | About 35% less allocation reading a 5 MiB story; timing within noise | `*IndependentDimensionBenchmarks.ReadAndDecodeDirectory*layout-*` |
| Replay fingerprints encode through a 4 KiB buffer instead of full-file UTF-8 arrays; the hashed input is unchanged | About 19% less compile allocation at 5 and 50 MiB | `*IndependentDimensionBenchmarks.CompileLoadedSource*bytes-50MiB*` |
| `TaleBinder` scans line ranges and skips comment lines before creating strings | Large savings on comment-heavy synthetic corpora (up to 99% less allocation); typical stories gain less | `*IndependentDimensionBenchmarks.CompileLoadedSource*bytes-5MiB*` |
| Restoring a save at its newest choice keeps the validating replay instead of replaying twice | About 45% faster and 49% less allocation at 1,000 and 10,000 choices; saves with redo choices still validate everything | `*IndependentDimensionBenchmarks.RestoreAtConfiguredDepth*`, `*StoryBenchmarks.RestoreAtRedoPrefix*` |
| The player reads the transcript view once per render, and `StoryPassage` skips re-rendering a passage whose presentation is unchanged | Back/Forward at 10,000 choices about 30–40% faster in all three browsers | `HistoryNavigationPerformanceTests` (see below) |

Run BenchmarkDotNet filters with:

```bash
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter 'FILTER' --warmupCount 3 --iterationCount 15 --iterationTime 500 \
  --artifacts artifacts/scorecard
```

Compare unchanged and changed code in alternating order to offset drift.

## Long-history navigation

Back and Forward still replay the whole history, so they slow down as history grows.
Investigation continues in [#146](https://github.com/squid-farm/talewright/issues/146).

The opt-in `HistoryNavigationPerformanceTests` test holds a one-scene looping
story constant and varies only history depth: 100, 1,000 and 10,000 choices.
A prepared portable save seeds each browser through the existing IndexedDB
save/load path; archive upload, compilation and save loading are outside the
navigation measurements. After one warmup Back/Forward pair, it records five
pairs per depth. Back and Forward are measured separately.

Browser timing starts in a capturing listener on the actual Playwright click,
not before Playwright's actionability checks. A mutation observer records when
the expected transcript length and current passage text are present. Animation
frames check focus on the current passage and record the largest frame gap
after the click. Completion is the second animation frame after DOM and focus
readiness: a **paint opportunity**, not proof of pixels reaching the display or
completion of smooth scrolling. Standard E2E tracing remains enabled.
Focus is normalized after closing the setup save dialog, outside measurement;
the probe does not validate save-dialog focus restoration.

Current local medians (paint opportunity, milliseconds, Back / Forward, ten pooled
samples at the newest saved choice) on Linux x64, AMD Ryzen 9 7940HS, with tracing
enabled and variable machine load. These are exploratory observations, not
controlled device comparisons or percentiles.

| Browser | 100 choices | 1,000 choices | 10,000 choices |
|---|---:|---:|---:|
| Chromium | 30.5 / 29.9 | 63.5 / 64.0 | 328.2 / 349.4 |
| Firefox | 30.0 / 30.5 | 82.5 / 72.5 | 535.0 / 520.0 |
| WebKit | 34.5 / 32.5 | 67.0 / 70.5 | 449.5 / 473.0 |

The test also measures native `StoryHistory.Back` and `Forward` on the same
fixture before browser setup, with current-thread managed allocation counters.
At 10,000 choices, the native medians across these runs were 87.7–104.4 ms for
Back and 93.8–97.8 ms for Forward, allocating about 35.53 MiB per direction.
These are **not WASM replay timings**: subtracting them from browser completion
would not isolate UI cost. Native assertions check the cursor and transcript
length after each action, and exact saved bytes and deterministic event
sequence after every pair. Browser assertions check passage text, transcript
length, focus and the disabled Forward control at the timeline end.

```bash
python3 player/tests/run_e2e.py web --browser all \
  --filter FullyQualifiedName~HistoryNavigationPerformanceTests
```

The filter runs three cases per browser. Each TRX stdout contains raw
per-action JSON under `nativeReplay` and `browserNavigation`; normal E2E
artifacts also contain browser traces. There are no CI timing gates.

Live and peak memory are not measured.

Checkpoints are not adopted. If replay attribution later justifies them, review a
separate design first. It must specify a measured retained-memory cap,
construction/pause cost, spacing, eager versus lazy creation, invalidation on
fork/restart/load and eviction fallback to full replay. Snapshots must include
trigger edge state, RNG/roll position, event boundaries, character identity and
presentation state, not only `GameState`. They stay internal accelerators,
never trusted persisted state.

## Budget status

The candidate goals (ordinary choice p95 below 50 ms, Back/Forward p95 below
100 ms) are **proposals for review**, not accepted budgets or CI
gates. The hosted Typical choice observations range from 56 to 103 ms, but each
is a single sample. Existing local 10,000-choice Back/Forward measurements also
show why history depth and runtime configuration must be stated alongside any
target. The evidence does not support universal browser, tier, or
constrained-device guarantees. Process memory, allocation stacks and device
samples are tracked in [#212](https://github.com/squid-farm/talewright/issues/212).

Before adopting tier/device-specific budgets, collect repeated, comparable runs
on the agreed reference devices, record variance and main-thread blocking, and
review the targets with the project maintainers. Keep correctness and output
invariants as hard gates; use controlled trend comparisons rather than flaky
shared-CI timing thresholds. The smoke-test limits in the
[test guide](../../tests/README.md#reproducible-performance-budgets) are broad
regression envelopes, not these product budgets.

## Reproduction

From the repository root:

```bash
# Verify all deterministic corpus tiers and replay histories.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- --verify-corpora

# Compare source read/decode, compile, and combined work for the fixed-byte layouts.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks*layout-*' --artifacts artifacts/scorecard/layout

# Compare end-cursor and redo-prefix restore on the 10,000-choice corpus.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RestoreAt*Gigantic*' --artifacts artifacts/scorecard/restore

# Run the full Chromium, Firefox, and WebKit real-corpus integration matrix.
python3 player/tests/run_e2e.py web --filter FullyQualifiedName~RealCorpusBrowserPerformanceTests
```

The hosted workflow is the durable cross-run trend source. Keep raw
benchmark artifacts and runtime metadata with each comparison; do not compare
different tiers, machines, runtime versions, or single browser observations as
if they were repeated p95 samples.
