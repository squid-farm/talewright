# Performance scorecard

This page gathers the current evidence for opening, playing, and restoring
stories. It distinguishes measured latency and allocation from live memory and
process peaks. Results are baselines for comparison, not guarantees. Do not
compare rows from different machines or treat a single browser observation as a
percentile.

## Measurement status

| Dimension | Current evidence | Interpretation and remaining work |
|---|---|---|
| Source read and decode | BenchmarkDotNet directory-load cases; fixed-byte file-layout comparison below | Measures the `StorySource` read path, not editor file-watcher latency |
| Compile | BenchmarkDotNet in-memory compile cases | Isolates compile from directory read/decode |
| Runtime start | `StartupAndFirstFrame` BenchmarkDotNet case | Measures engine session start, not full host startup |
| First playable passage | WebAssembly browser integration in Chromium, Firefox, and WebKit | One observation per browser/corpus; includes archive upload, compilation, and rendering |
| Click to painted passage | Browser integration choice timing | One observation per browser/corpus; not a p95 |
| End-cursor and redo-prefix restore | BenchmarkDotNet compares both with the same 10,000-choice save | The prefix case validates all saved choices and returns to cursor 5,000 |
| Back/Forward | BenchmarkDotNet at 100, 1,000, and 10,000 choices | Use the 30-iteration suite for comparison; short local runs can be noisy |
| Managed allocation | BenchmarkDotNet `MemoryDiagnoser` | Cumulative managed bytes per operation, not retained memory |
| Live managed heap after repeated open/close/restore | Not measured | Needs a stable post-operation collection and retention protocol |
| Process peak RSS | Benchmark worker metadata and standalone publication measurements | Worker peak includes setup and harness; it is not an isolated operation or a live heap measurement |
| Browser process memory | Not measured | `navigator.deviceMemory` is only a browser-reported device-capacity hint, not memory usage |
| Allocation stacks | Not available from the local trace | EventPipe capture completed, but Speedscope conversion yielded no allocation profiles or managed allocation stacks; CPU samples must not be reported as allocation attribution |
| Main-thread blocking | Chromium long-task entries in the browser integration | Firefox and WebKit do not report this metric in the current harness |
| Constrained device | Not measured | Available local host and hosted runners are not representative constrained devices |

## Published browser observations

The first successful [real-file performance workflow](https://github.com/squid-farm/talewright/actions/runs/39902796074)
ran on 2026-10-23. The workflow collected one timed integration per browser and
corpus, not repeated samples. “First playable” includes uploading the already
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
10,045 ms on Gigantic. The current harness did not collect long-task support in
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
These exploratory local results are useful for reproduction and comparison, not
as a stable device budget. Benchmark metadata marked the working tree dirty, so
the figures are not an immutable upstream-commit baseline.

### Fixed-byte, fixed-scene file layout

The two deterministic corpora both contain 100 scenes, 110 choices, and exactly
5 MiB expanded source. One has two story files (one large Tale file); the other
has 100 story files (99 small Tale files). The benchmark reports mean, observed
p95, standard deviation, and allocated bytes.

| Operation | Layout | Mean | p95 | Std. dev. | Allocated |
|---|---|---:|---:|---:|---:|
| Read/decode directory | 2 files | 11.63 ms | 12.49 ms | 0.49 ms | 30.15 MiB |
| Read/decode directory | 100 files | 9.76 ms | 10.37 ms | 0.47 ms | 33.92 MiB |
| Compile loaded source | 2 files | 16.65 ms | 17.59 ms | 0.60 ms | 26.58 MiB |
| Compile loaded source | 100 files | 11.94 ms | 12.62 ms | 0.37 ms | 26.66 MiB |
| Read/decode and compile | 2 files | 26.77 ms | 28.16 ms | 0.91 ms | 56.72 MiB |
| Read/decode and compile | 100 files | 27.33 ms | 32.66 ms | 2.98 ms | 60.56 MiB |

The fixed-input comparison isolates file layout better than the existing
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

## Fingerprint allocation experiment

A local experiment on base commit `bcffef9` replaces full-file UTF-8 arrays
used for replay fingerprints with a 4 KiB encoding buffer. It preserves the
length-prefixed SHA-256 input, including Unicode and invalid-surrogate
replacement; saves must not acquire a different fingerprint.

The independent `CompileLoadedSource` byte-volume cases hold file count (100),
scene count (100), and history depth (100) constant. Changed-code screening
came first; only after allocation savings looked promising was the production
change temporarily removed for unchanged-code comparison. The test additions
remained in place for both states.

| Source size | Unchanged allocated/op | Changed allocated/op | Reduction | Unchanged mean | Changed mean |
|---|---:|---:|---:|---:|---:|
| 5 MiB | 26.65 MiB | 21.65 MiB | 18.8% | 11.65 ms | 11.25 ms |
| 50 MiB | 251.65 MiB | 201.64 MiB | 19.9% | 82.73 ms | 77.02 ms |

Means are averages of two runs per state, ordered unchanged → changed, then
changed → unchanged, to offset drift. Runs were local Release builds on Linux
Mint 22.3, Ryzen 9 7940HS, .NET 10.0.13, on AC power with the CPU governor and
power profile set to `performance`, and no concurrent tests or benchmarks
(an idle browser remained open). The 5 MiB case used five warmups and 20
measurements; the 50 MiB case used three warmups and 15 measurements of about
one second.

Allocation savings were stable across runs. The 50 MiB timing improvement
(about 7%) was consistent in both orders; the 5 MiB timing difference (about
3%) is within run-to-run noise. Earlier quick runs that showed a 20–25% timing
improvement were taken on battery with concurrent E2E load and are discarded.
Gen0/Gen1/Gen2 collections per 1,000 operations at 50 MiB fell from 24/22/22
to 19/17/17. These figures are not retained-heap or isolated process-memory
measurements.

Reproduce either size with the same command on unchanged and changed code:

```bash
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.CompileLoadedSource*bytes-50MiB*' \
  --warmupCount 3 --iterationCount 15 --iterationTime 1000 \
  --artifacts artifacts/fingerprint-experiment
```

## Single-replay restore experiment

Loading a save used to replay every saved choice once to validate the save,
discard the result, start a throwaway session, then replay the choices again
up to the save point. Restore now keeps the validated replay when the save
point is the newest choice, which is the normal case. When the player had
pressed Back before saving, every choice, including redo choices, is still
validated. The visible prefix is then rebuilt, but the throwaway session is
skipped.

| Case | Unchanged mean | Changed mean | Time | Unchanged allocated/op | Changed allocated/op | Allocation |
|---|---:|---:|---:|---:|---:|---:|
| Newest choice, 1,000 choices | 6.36 ms | 3.68 ms | −42% | 9.19 MiB | 4.68 MiB | −49% |
| Newest choice, 10,000 choices | 156.29 ms | 86.22 ms | −45% | 91.73 MiB | 46.58 MiB | −49% |
| Earlier save point, Medium tier | 6.32 ms | 5.63 ms | −11% | 7.53 MiB | 7.28 MiB | −3% |

The first two rows use `IndependentDimensionBenchmarks.RestoreAtConfiguredDepth`
(history-1000 and history-10000), averaged over two runs per state in
alternating order with 3 warmups and 15 measurements. The earlier-save-point
row uses `StoryBenchmarks.RestoreAtRedoPrefix` (Medium tier); it is averaged
over a second pair of alternating runs with 30 measurements, because the first
pair was too noisy to read. Runs were local Release builds on AC power with the
`performance` governor and no concurrent tests or benchmarks. These measure
managed allocation and time, not retained heap or peak process memory.

```bash
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.RestoreAtConfiguredDepth*history-1000*' \
  '*StoryBenchmarks.RestoreAtRedoPrefix*Medium*' \
  --warmupCount 3 --iterationCount 15 --iterationTime 500 \
  --artifacts artifacts/restore-experiment
```

## Source-loading allocation experiment

`StorySource.ReadText` now decodes directly from the input stream through a
bounded `StreamReader` instead of copying every file into a growing
`MemoryStream` first. The 4 KiB reader buffer and byte limits preserve
`StreamReader`'s BOM detection and replacement behavior for malformed input.
This avoids the 81,920-byte per-file buffer and intermediate full-file copy;
the reader still has per-file internal buffers, and no pooled/shared buffer
was introduced.

The `ReadAndDecodeDirectory` cases use the same 5 MiB aggregate input, scene
count and history depth, varying only the layout: two large files versus 100
smaller files. Local Release runs were on AC power; unchanged and changed
states were alternated. Both 100-file runs used 3 warmups and 15 measurements
of 500 ms. The few-file timing was noisy, so it was repeated with 5 warmups and
30 measurements of one second.

| Layout | Unchanged allocated/op | Changed allocated/op | Allocation change | Unchanged mean | Changed mean | Timing change |
|---|---:|---:|---:|---:|---:|---:|
| 2 files | 30.15 MiB | 20.10 MiB | −33.3% | 12.13 ms | 12.67 ms | +4.4% (within noise) |
| 100 files | 33.92 MiB | 21.96 MiB | −35.2% | 10.87 ms | 10.38 ms | −4.4% (within noise) |

The longer 2-file comparison's mean was 12.13 ms unchanged and 12.67 ms
changed; its median moved by +6.4%. These differences are smaller than the
run-to-run spread and do not establish a timing regression or speedup. The
allocation reduction is consistent across both layouts. This is a warm local
directory-read benchmark, not cold-open latency, UI responsiveness, retained
heap or peak process memory. The latter measurements remain deferred to #212.

```bash
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.ReadAndDecodeDirectory*layout-*' \
  --warmupCount 3 --iterationCount 15 --iterationTime 500 \
  --artifacts artifacts/source-loading-experiment
```

## Budget status

The issue's candidate goals—ordinary choice p95 below 50 ms and Back/Forward
p95 below 100 ms—remain **proposals for review**, not accepted budgets or CI
gates. The hosted Typical choice observations range from 56 to 103 ms, but each
is a single sample. Existing local 10,000-choice Back/Forward measurements also
show why history depth and runtime configuration must be stated alongside any
target. The evidence does not yet support universal browser, tier, or
constrained-device guarantees.

Before adopting tier/device-specific budgets, collect repeated, comparable runs
on the agreed reference devices, record variance and main-thread blocking, and
review the targets with the project maintainers. Keep correctness and output
invariants as hard gates; use controlled trend comparisons rather than flaky
shared-CI timing thresholds. Existing smoke-test limits in the
[test guide](../../tests/README.md#reproducible-performance-budgets) are broad
regression envelopes, not these product budgets.

## Reproduction

From the repository root:

```bash
# Verify all deterministic corpus tiers and replay histories.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- --verify-corpora

# Compare source read/decode, compile, and combined work for the fixed-byte layouts.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks*layout-*' --artifacts artifacts/issue142/layout

# Compare end-cursor and redo-prefix restore on the 10,000-choice corpus.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RestoreAt*Gigantic*' --artifacts artifacts/issue142/restore

# Run the full Chromium, Firefox, and WebKit real-corpus integration matrix.
python3 player/tests/run_e2e.py web --filter FullyQualifiedName~RealCorpusBrowserPerformanceTests
```

The published workflow remains the durable cross-run trend source. Keep raw
benchmark artifacts and runtime metadata with each comparison; do not compare
different tiers, machines, runtime versions, or single browser observations as
if they were repeated p95 samples.
