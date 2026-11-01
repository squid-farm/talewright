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

## Parser comment-line materialization experiment

`TaleParser` no longer normalizes the whole source into a second string and
splits it into an array containing a string for every line. It scans line
ranges in the immutable source and skips comment lines before creating a
string. Non-comment lines still become strings for the existing parser APIs.
The line scanner preserves CR, LF, CRLF, NEL, line-separator and
paragraph-separator handling, including an empty line after a terminal
newline.

The `CompileLoadedSource` benchmarks hold 100 files, 100 scenes and history
depth 100 constant; the synthetic corpora are padded primarily with long
comments. Runs used local Release builds on AC power, alternating unchanged
and changed states, with 3 warmups, 10 measurements and 500 ms per measurement.

| Source size | Unchanged allocated/op | Changed allocated/op | Allocation change | Unchanged mean | Changed mean | Mean-time change |
|---|---:|---:|---:|---:|---:|---:|
| 5 MiB | 21.66 MiB | 1.68 MiB | −92.3% | 11.69 ms | 5.12 ms | −56.2% |
| 50 MiB | 201.65 MiB | 1.68 MiB | −99.2% | 71.16 ms | 41.86 ms | −41.2% |

Each state is the average of two runs; the timing results were consistent
between the alternating runs. The synthetic workload intentionally stresses
comment padding, so these results demonstrate parser churn on that workload,
not the same percentage gain on typical hand-written stories. They measure
compile time and managed allocation, not live heap, peak process memory or
editor responsiveness.

The existing source-map, parser/compiler, implicit text-variable and full
non-E2E test suites passed, including diagnostic/forward-inference cases.
No grammar, lexer, escape or diagnostic behavior was intentionally changed;
lexer/escape work remains separate in #184.

```bash
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.CompileLoadedSource*bytes-5MiB*' \
  --warmupCount 3 --iterationCount 10 --iterationTime 500 \
  --artifacts artifacts/parser-materialization
```

## Long-history navigation investigation (#146)

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

On 2026-10-25, all nine browser/depth cases passed on Linux x64, AMD Ryzen 9
7940HS (16 logical CPUs), .NET 10.0.13. Versions were Chromium 153.0.8010.12,
Firefox 155.0 and WebKit 26.6. Power state and background activity were not
reliably captured for this cross-browser run, so these are exploratory local
observations, not controlled device comparisons or percentiles. The base was
`fc70bb171e3df506ba99f6e957c05bb2a3e1ae47` plus the measurement test.

All values below are medians of five samples in milliseconds. Each cell lists
Back / Forward, and all directions are exercised at the newest saved choice.

| Browser | Choices | DOM observed | Focus observed | Paint opportunity | Largest animation-frame gap |
|---|---:|---:|---:|---:|---:|
| Chromium | 100 | 14.9 / 13.9 | 14.9 / 13.9 | 29.6 / 29.7 | 16.3 / 15.6 |
| Chromium | 1,000 | 67.0 / 68.9 | 67.0 / 68.9 | 68.6 / 78.7 | 67.6 / 69.5 |
| Chromium | 10,000 | 482.8 / 479.4 | 482.8 / 479.4 | 491.1 / 487.4 | 484.4 / 481.2 |
| Firefox | 100 | 18 / 15 | 18 / 15 | 30 / 32 | 19 / 18 |
| Firefox | 1,000 | 87 / 76 | 87 / 76 | 95 / 82 | 88 / 76 |
| Firefox | 10,000 | 692 / 619 | 692 / 619 | 732 / 668 | 692 / 620 |
| WebKit | 100 | 20 / 17 | 20 / 17 | 28 / 28 | 20 / 17 |
| WebKit | 1,000 | 74 / 54 | 74 / 54 | 90 / 69 | 75 / 55 |
| WebKit | 10,000 | 480 / 483 | 480 / 483 | 569 / 563 | 480 / 487 |

The test also measures native `StoryHistory.Back` and `Forward` on the same
fixture before browser setup, with current-thread managed allocation counters.
At 10,000 choices, the native medians across these runs were 87.7–104.4 ms for
Back and 93.8–97.8 ms for Forward, allocating about 35.53 MiB per direction.
These are **not WASM replay timings**: subtracting them from browser completion
would not isolate UI cost. Native assertions check the cursor and transcript
length after each action, and exact saved bytes and deterministic event
sequence after every pair. Browser assertions check passage text, transcript
length, focus and the disabled Forward control at the timeline end.

### Bounded next decision

There is a concrete long-history navigation problem: the 10,000-choice browser
actions have roughly 0.5–0.7 second paint-opportunity medians and similarly large
animation-frame gaps. Native replay independently allocates and executes work
proportional to history depth. This is a baseline, not an optimization result.
It does not yet attribute WASM replay, transcript enumeration, component
updates, layout, or scrolling separately, and does not complete #146's
attribution/design acceptance.

Before adopting checkpoints, the next implementation experiment should test
stable transcript views and unchanged-passage rendering boundaries against this
baseline. The current transcript property creates a read-only wrapper on each
access and the player walks every passage during a render; those are concrete
code paths to investigate, not proof that rendering dominates. Keep one
bounded experiment: retain it only at the #141 improvement threshold, with no
correctness or material regression, otherwise discard it and record that
outcome. Preserve all transcript entries, selection, focus and scroll behavior.
Do not mix in checkpoints, virtualization, threading or save-format changes.

If replay attribution later justifies checkpoints, review a separate design
before implementation. It must specify a measured retained-memory cap,
construction/pause cost, spacing, eager versus lazy creation, invalidation on
fork/restart/load and eviction fallback to full replay. Snapshots must include
trigger edge state, RNG/roll position, event boundaries, character identity and
presentation state, not only `GameState`. They remain internal accelerators,
never trusted persisted state. Live/peak memory remains unmeasured; #212 is
not closed by allocation counters or this navigation probe.

```bash
python3 player/tests/run_e2e.py web --browser all \
  --filter FullyQualifiedName~HistoryNavigationPerformanceTests
```

The exact filter runs three cases per browser. Each TRX stdout contains raw
per-action JSON under `nativeReplay` and `browserNavigation`; normal E2E
artifacts also contain browser traces. There are no new CI timing gates.

### Transcript-update experiment

The bounded candidate reads the transcript view once per player render rather
than creating a wrapper on every loop condition/index access. `StoryPassage`
also skips descendant rendering when the displayed title, document blocks,
styled inline values and chosen label match its last rendered presentation.
Replay still runs in full. The comparison is structural, not a hash or a
plain-text comparison: styling, speaker identity/name, note kind, line breaks
and media values participate. Unrecognized block types render conservatively.
All transcript entries remain present; there is no cache of extra histories,
checkpoint, virtualization or change to portable saves.

Matched Release publications used the same test assembly against unchanged and
changed web roots, with two rounds per browser. The first round ran unchanged
before changed, and the second reversed the states. Each direction below pools
ten measured samples (five per round), not ten independent browser sessions.
Both states ran the same transcript-length, passage-text, chosen-label-count,
focus and retained text-selection assertions. All 36 browser/depth/state/round
cases passed. Final player behavior, presentation and save-history validation,
including the additional focused fork regression, passed 57 cases per browser
in Chromium, Firefox and WebKit (171 passed, none failed or skipped).

| Browser | Choices | Unchanged paint opportunity, Back / Forward (ms) | Changed, Back / Forward (ms) | Reduction, Back / Forward |
|---|---:|---:|---:|---:|
| Chromium | 100 | 31.5 / 29.3 | 30.5 / 29.9 | 3.2% / -1.9% |
| Chromium | 1,000 | 90.1 / 70.2 | 63.5 / 64.0 | 29.4% / 8.8% |
| Chromium | 10,000 | 553.9 / 539.0 | 328.2 / 349.4 | 40.8% / 35.2% |
| Firefox | 100 | 29.5 / 30.0 | 30.0 / 30.5 | -1.7% / -1.7% |
| Firefox | 1,000 | 114.0 / 102.0 | 82.5 / 72.5 | 27.6% / 28.9% |
| Firefox | 10,000 | 790.0 / 818.5 | 535.0 / 520.0 | 32.3% / 36.5% |
| WebKit | 100 | 34.0 / 35.0 | 34.5 / 32.5 | -1.5% / 7.1% |
| WebKit | 1,000 | 95.5 / 88.5 | 67.0 / 70.5 | 29.8% / 20.3% |
| WebKit | 10,000 | 662.5 / 673.5 | 449.5 / 473.0 | 32.2% / 29.8% |

At 10,000 choices, pooled median maximum animation-frame gaps fell 36–42%
across the six browser/direction cases. At 100 choices, Firefox's gap medians
increased from 22 to 24.5 ms for Back and 20 to 22 ms for Forward, while its
completion medians changed by only 0.5 ms. Do not hide those small absolute
regressions behind the larger long-history gains.
The second round also had a Chromium 1,000-choice regression: Back rose
from 85.2 to 169.8 ms and Forward from 67.5 to 83.3 ms, despite pooled
improvements at that depth. A final Chromium-only check used unchanged,
changed, changed, unchanged order with the same publications and no rebuilds.
All twelve cases passed. Pooling ten samples per direction/state in that
check, 1,000-choice Back improved from 80.95 to 51.25 ms (36.7%) and Forward
from 71.35 to 46.8 ms (34.4%). At 100 choices, completion remained approximately
flat; at 10,000 it improved 47.0% / 38.7%. The earlier regression did not
reproduce, but remains part of the evidence rather than being discarded.

These remain exploratory measurements on a synthetic looping story, with
tracing enabled and variable machine load. The available second-round changed
environment logs confirm AC power but show load averages of 4.75–7.42 and
substantial I/O wait. Environment capture was incomplete for other states;
the comparison is not a controlled idle-host experiment or a user-story
percentile. The >10% target improvement appears consistently at 10,000 choices
in both rounds, supporting retaining this candidate for review, not claiming
that every navigation becomes 30–41% faster. Native replay is unchanged and
live/peak memory was not measured.

The experiment stops here rather than layering checkpoints onto this change.
Navigation still has significant replay and browser work; detailed WASM/DOM
attribution and any checkpoint design remain unresolved under #146. A focused
formatted-history regression additionally exercises Back, Forward, a changed
chosen label after a fork, and restart; it passed in all three browsers.
No performance CI gates were added.

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
