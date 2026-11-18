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
| Browser process memory | Not measured | Chromium `performance.memory` JS-heap snapshots are recorded for save scale; they are not process or WebAssembly managed-heap measurements |
| Allocation stacks | Not available from the local trace | EventPipe capture completed, but Speedscope conversion yielded no allocation profiles or managed allocation stacks; CPU samples must not be reported as allocation attribution |
| Main-thread blocking | Chromium long-task entries in the browser integration | Firefox and WebKit do not report this metric in the current harness |
| Constrained device | Not measured | Available local host and hosted runners are not representative constrained devices |

## Save size and continuation

Save, replay-restore and continuation-restore measurements were collected on
Linux Mint 22.3 x64, an AMD Ryzen 9 7940HS (16 logical processors), .NET
10.0.15 / SDK 10.0.115, and BenchmarkDotNet 0.15.2. The independent loop
benchmark used one out-of-process launch, five warmups and thirty measured
iterations. Times are means; allocations are cumulative managed bytes
per operation. These are local baselines, not product budgets.

The independent loop corpus holds the story constant and varies only history
depth. Save sizes are the container length and decoded MessagePack body length.

| Choices | Encoded / decoded | Save mean / allocation | Replay restore mean / allocation | Continuation mean / allocation |
|---:|---:|---:|---:|---:|
| 100 | 2,528 / 10,608 B | 0.177 ms / 0.12 MiB | 1.059 ms / 1.28 MiB | 1.289 ms / 1.45 MiB |
| 1,000 | 7,534 / 93,126 B | 1.779 ms / 0.91 MiB | 14.468 ms / 11.77 MiB | 11.390 ms / 12.47 MiB |
| 5,000 | 28,125 / 461,698 B | 12.106 ms / 4.63 MiB | 95.140 ms / 58.74 MiB | 104.680 ms / 62.07 MiB |
| 10,000 | 53,390 / 932,347 B | 29.479 ms / 9.25 MiB | 168.890 ms / 116.24 MiB | 199.610 ms / 125.10 MiB |

### Replay consolidation: Release comparison and PR gates

The table above is the pre-consolidation 10,000-history baseline. This comparison
uses the same generated `PerformanceCorpus` `history-10000` case, end cursor,
BenchmarkDotNet 0.15.2 out-of-process job, one launch, five warmups and thirty
iterations. Allocations are BDN managed bytes (its `MB` display uses 1,024-based
units). The save is 53,390 encoded / 932,347 decoded bytes. The historical
baseline was measured before replay consolidation on `f690ee5a`; the
after measurements used the replay changes based on that revision, before the
subsequent allocation reductions measured by the PR gate below.

| `history-10000` operation | Before mean / allocation | After mean / allocation |
|---|---:|---:|
| Save | 29.479 ms / 9.25 MiB | 22.328 ms / 9.25 MiB |
| Exact restore | 168.890 ms / 116.24 MiB | 116.310 ms / 78.26 MiB |
| Changed-story continuation | 199.610 ms / 125.10 MiB | 130.852 ms / 85.21 MiB |

The deterministic CI gate enforces the issue's 64 MiB ceiling on the historical
`PerformanceCorpus` `history-10000` fixture, including its 100 scenes, prose,
effects and hub offers. The fixture makes 10,000 player choices and stores
10,000 transitions. On the implementation based on `757aa17c`, plus the
subsequent allocation changes, its warmed Release observations were:

| Historical independent operation | Allocation / time | Ceiling |
|---|---:|---:|
| Exact restore, end / redo cursor 5,000 | 53.36 / 49.76 MiB; 127.7 / 96.2 ms | 64 MiB |
| Changed-story continuation, end / redo cursor 5,000 | 58.22 / 52.20 MiB; 116.3 / 156.5 ms | 64 MiB |

The largest row has 5.78 MiB (9.0%) of headroom. The gate checks restored
variable state, cursor and playthrough length, forward availability for redo,
and redo discard on continuation. Before these allocation reductions, the same
new gate failed at 79.57 MiB for exact restore. Temporary per-stage
current-thread counters in that build attributed 53.55 MiB to scene entry,
including 12.74 MiB for prose rendering, 11.67 MiB for choice processing and
11.02 MiB for menu construction; these nested figures overlap. Save decoding
accounted for 12.17 MiB. The changes reduce repeated allocation in rendering,
silent-event narration, triggers, menus, replay evidence and transcript decoding
without changing the persisted evidence. These figures are allocation
attribution by instrumented stages, not allocation stacks or peak memory.

The gate also uses a smaller two-choice independent loop to cover legacy saves
and continuation-base restore. Its warmed Release measurements were:

| Operation | Measured allocation / time | Ceiling |
|---|---:|---:|
| Independent save, end / redo cursor 5,000 | 5.29 / 3.17 MiB; 19.7 / 10.5 ms | 12 MiB |
| Independent restore, end / redo cursor 5,000 | 25.35 / 19.45 MiB; 108.7 / 86.7 ms | 64 MiB |
| Independent changed-story continuation fallback | 31.20 MiB; 176.3 ms | 64 MiB |
| Independent continuation-base restore | 7.74 MiB; 22.3 ms | 64 MiB |
| Ordinary next choice at depth 9,999 | <0.01 MiB; 0.3 ms | Report-only |

Both fixture tests warm once and construct fixtures outside the measurement.
The save ceiling allows about 2.3x the independent end-save allocation.

The realistic gate uses `RealisticStory` with 20 chapters (946 scenes, 22 files,
about 693 KB of source), 10,000 requested actions and 10,000 saved transitions;
the benchmark stops the route after 6,665 completed actions for comparability.
It contains automatic transitions and checks both end and redo cursors:

| Operation | End allocation / time | Redo allocation / time | Ceiling |
|---|---:|---:|---:|
| Save | 23.26 MiB / 99.1 ms | 12.27 MiB / 40.3 ms | 32 MiB |
| Exact restore | 606.24 MiB / 823.7 ms | 459.76 MiB / 448.2 ms | 896 MiB |
| Changed-story continuation | 615.98 MiB / 699.4 ms | — | 768 MiB |

The realistic save ceiling is 38% above the measured end save; exact restore
and continuation ceilings are about 48% and 25% above their measured rows.
The 10,000-action save is 65,365 encoded / 7,033,682 decoded bytes. These are
per-operation cumulative managed allocations, not retained heap or peak RSS.
The focused gate's wall times are single warmed observations, not timing
budgets. The BenchmarkDotNet comparison above is the timing evidence for the
independent history fixture.

Temporary stage counters on the realistic end restore before the final
hub-selection optimization attributed 756.07 MiB of a 1,000.36 MiB instrumented
operation to hub menu construction. These inclusive counters overlap other
stages and are not allocation stacks. Avoiding unobserved exclusion lists and
per-scene tag-matching iterators reduced the uninstrumented end restore from
999.34 to 606.24 MiB. Hub selection still scans the story and constructs its
menu; no browser or retained-memory inference follows from these totals.

The same BenchmarkDotNet job measured the realistic 20-chapter fixture at
10,000 requested actions (6,665 completed; 10,000 saved choices) after
consolidation:

| Operation | Before mean / allocation | After mean / allocation |
|---|---:|---:|
| Save | 83.626 ms / 23.31 MiB | 68.488 ms / 23.31 MiB |
| Exact restore | 1,600.231 ms / 2,071.45 MiB | 705.675 ms / 1,021.2 MiB |
| Changed-story continuation | 720.166 ms / 1,078.12 MiB | 864.774 ms / 1.01 GiB |

Exact replay allocation fell by about half. This continuation timing did not
improve in the recorded run: its P95 was 1,024.7 ms, with several high outliers.
The managed allocation was about 1.01 GiB, slightly below the historical
1,078.12 MiB. This noisy result is reported rather than presented as a speedup.

Measurements used Linux Mint 22.3 x64, AMD Ryzen 9 7940HS (16 logical / 8
physical cores), .NET 10.0.15 / SDK 10.0.115, BenchmarkDotNet 0.15.2 and
Concurrent Workstation GC. The ordinary next-choice observation is separate
from save/restore. Allocation stacks and live/peak memory are not measured;
browser/WebAssembly headroom remains unknown. Full-history per-choice autosave
is still cumulative quadratic work and is not changed here.

### 10,000 and 100,000-transition scale comparison

The following Release probes use #768's merged cap removal. Before is `eebfbfba`
(the #768 head); after is the replay implementation at `32054029`, integrated
with #768 and the trigger-session fix. The 100,000 realistic save, end-restore,
end-continuation and next-choice rows were measured before the subsequent
automatic-cursor fix. Its redo restore and continuation rows were remeasured
after that fix; other after rows also include it.

Each BenchmarkDotNet operation ran in a separate process with one launch, one
warmup, three measured iterations and one invocation per iteration. Rows are
mean milliseconds / cumulative managed MiB. These short probes have wide
timing uncertainty, especially for sub-millisecond choices; they are scale
observations, not latency budgets. They ran sequentially under a 6 GiB
process-group memory limit on the host described above.

The independent case is the generated `PerformanceCorpus` history dimension:
10 files, 100 scenes and 512 KiB of source. Each requested action saves one
transition. The realistic case is the 20-chapter fixture described above:
10,000 / 100,000 requested actions produced 6,665 / 66,665 completed actions
and 10,000 / 100,000 transitions, respectively, without restarting. The
100,000-transition saves were 509,202 encoded / 9,454,983 decoded bytes for
the independent case and 599,314 / 70,394,211 bytes for the realistic case.
Redo saves retain the full timeline with cursors 5,000 or 50,000; they were
constructed by restoring a legacy cursor save and saving it again, so their
evidence and continuation checkpoints match the cursor. Changed-story
continuation discards redo and resumes from that checkpoint.

| Independent operation | 10k before | 10k after | 100k before | 100k after |
|---|---:|---:|---:|---:|
| Save, end | 37.7 / 9.25 | 50.8 / 9.25 | 554.1 / 90.22 | 362.0 / 90.22 |
| Save, redo | 19.5 / 5.15 | 22.8 / 5.15 | 194.6 / 50.30 | 175.9 / 50.30 |
| Exact restore, end | 322.3 / 117.84 | 273.7 / 78.74 | 2,315.3 / 1,177.54 | 2,401.4 / 796.63 |
| Exact restore, redo | 366.7 / 166.14 | 266.8 / 75.30 | 2,961.2 / 1,668.21 | 1,701.4 / 744.35 |
| Continuation, end | 305.6 / 125.78 | 339.5 / 85.70 | 2,383.3 / 1,251.75 | 2,770.6 / 864.69 |
| Continuation, redo | 352.4 / 169.86 | 290.4 / 78.36 | 2,603.8 / 1,696.69 | 1,380.5 / 778.36 |
| Next choice near end | 0.377 / 0.009 | 0.374 / 0.007 | 0.304 / 0.011 | 0.199 / 0.008 |

| Realistic operation | 10k before | 10k after | 100k before | 100k after |
|---|---:|---:|---:|---:|
| Save, end | 93.0 / 23.31 | 93.2 / 23.31 | 881.4 / 295.15 | 1,092.5 / 295.15 |
| Save, redo | 47.1 / 12.30 | 47.7 / 12.30 | 668.0 / 149.41 | 475.9 / 149.41 |
| Exact restore, end | 2,252.2 / 2,071.45 | 1,100.8 / 1,021.26 | 16,535.9 / 20,817.37 | 8,700.2 / 10,312.20 |
| Exact restore, redo | 4,051.9 / 3,052.75 | 1,372.6 / 994.88 | 25,623.3 / 30,583.56 | 11,068.4 / 10,000.09 |
| Continuation, end | 1,124.6 / 1,078.00 | 1,018.6 / 1,035.47 | 9,388.6 / 10,875.14 | 7,795.9 / 10,449.76 |
| Continuation, redo | 1,123.3 / 1,548.25 | 877.0 / 1,001.92 | 14,681.8 / 15,532.04 | 8,526.3 / 10,068.42 |
| Next choice near end | 0.499 / 0.25 | 0.397 / 0.25 | 0.408 / 0.25 | 0.483 / 0.25 |

The next-choice benchmark advances the live history on each invocation, so
measurements are near, not exactly at, the listed depth. BenchmarkDotNet warned
that these short iterations were below its recommended duration. Restore
allocation grows roughly tenfold for ten times as many transitions in both
fixtures. Save allocation grows somewhat more (the realistic end row grows
12.7x). Save builds collection copies and serialized/compressed buffers, but
allocation-stack attribution for that increase is not available. This is not
an incremental save algorithm. Wall-time ratios are too noisy to establish
precise scaling from three iterations. The earlier realistic 100,000-transition
restore allocated about 10 GiB per operation, before the later hub-selection
optimization; the final implementation's 100,000-transition allocation was
not remeasured. Saving still reconstructs boundary state and
serializes the full history, making it a separate hotspot.

Commands (run once on the baseline and once on the implementation, selecting
the exact desired parameter; `10000)` avoids matching `100000)`). On this host,
each command was also run inside a `systemd-run --user --scope -p MemoryMax=6G
-p MemorySwapMax=0` scope:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.*At*history-10000*' \
  --warmupCount 1 --iterationCount 3 --invocationCount 1
MSBUILDDISABLENODEREUSE=1 dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RealisticStoryBenchmarksSaves*10000)' \
  --warmupCount 1 --iterationCount 3 --invocationCount 1
MSBUILDDISABLENODEREUSE=1 dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RealisticStoryBenchmarksSaves*100000*' \
  --warmupCount 1 --iterationCount 3 --invocationCount 1
```

The realistic desktop benchmark uses a generated 20-chapter story. Requested
actions and saved choices differ because automatic choices also count as saved
choices. The 10,000-action case stops at exactly 10,000 saved choices without
restarting; playthroughs have no cap, and the stop keeps rows comparable.

| Requested / completed actions | Saved choices | Encoded / decoded | Save mean / allocation | Replay restore mean / allocation | Continuation mean / allocation |
|---:|---:|---:|---:|---:|---:|
| 1,000 / 1,000 | 1,502 | 13,177 / 1,056,290 B | 8.175 ms / 3.61 MiB | 299.577 ms / 312.55 MiB | 174.748 ms / 163.54 MiB |
| 5,000 / 5,000 | 7,502 | 50,085 / 5,276,456 B | 56.835 ms / 17.66 MiB | 1,605.091 ms / 1,557.53 MiB | 888.634 ms / 812.33 MiB |
| 10,000 / 6,665 | 10,000 | 65,365 / 7,033,682 B | 83.626 ms / 23.31 MiB | 1,600.231 ms / 2,071.45 MiB | 720.166 ms / 1,078.12 MiB |

Browser save/load times are the test's click-to-status observations, including
the player UI and IndexedDB path. Autosave timings are 50 per-choice samples;
the p50/p95 below describe these local samples, not a population-wide
percentile. Sizes are the stored bytes observed by the test.

| Browser | Choices | Replay save ms / bytes | Replay load ms | Continuation save ms / bytes | Continuation load ms |
|---|---:|---:|---:|---:|---:|
| Chromium | 1,000 | 151.8 / 7,560 | 590.8 | 104.7 / 6,744 | 223.9 |
| Chromium | 5,000 | 275.3 / 28,222 | 1,732.2 | 191.4 / 26,814 | 586.1 |
| Chromium | 10,000 | 420.1 / 53,498 | 2,979.7 | 321.0 / 51,599 | 1,012.8 |
| Firefox | 1,000 | 193.5 / 7,560 | 652.4 | 144.4 / 6,745 | 270.8 |
| Firefox | 5,000 | 361.2 / 28,222 | 2,646.7 | 266.7 / 26,818 | 714.0 |
| Firefox | 10,000 | 561.9 / 53,499 | 4,360.0 | 404.3 / 51,601 | 1,269.6 |
| WebKit | 1,000 | 144.2 / 7,560 | 634.3 | 114.9 / 6,744 | 227.7 |
| WebKit | 5,000 | 291.2 / 28,220 | 1,855.6 | 195.1 / 26,817 | 624.0 |
| WebKit | 10,000 | 439.5 / 53,498 | 3,294.1 | 334.9 / 51,604 | 1,088.0 |

| Browser | Choices | Replay autosave p50 / p95 ms | Continuation autosave p50 / p95 ms | Autosave bytes, replay / continuation |
|---|---:|---:|---:|---:|
| Chromium | 1,000 | 24.2 / 37.3 | 19.7 / 24.8 | 7,850 / 7,079 |
| Chromium | 5,000 | 72.4 / 78.6 | 59.3 / 68.8 | 28,502 / 26,979 |
| Chromium | 10,000 | 131.3 / 145.7 | 104.0 / 113.5 | 53,498 / 51,615 |
| Firefox | 1,000 | 32 / 50 | 27 / 64 | 7,849 / 7,081 |
| Firefox | 5,000 | 88 / 118 | 72 / 83 | 28,501 / 26,981 |
| Firefox | 10,000 | 160 / 181 | 151 / 187 | 53,498 / 51,616 |
| WebKit | 1,000 | 29 / 42 | 26 / 32 | 7,849 / 7,078 |
| WebKit | 5,000 | 77 / 88 | 63 / 73 | 28,501 / 26,980 |
| WebKit | 10,000 | 136 / 152 | 107 / 119 | 53,497 / 51,618 |

At 10,000 choices, autosave sampling starts at 9,950 because the playthrough
cap prevents another 50 choices. Chromium's observed p95 is above the
candidate 50 ms target for both replay and continuation. Recommend that #756
consider incremental autosave; this measurement issue does not change autosave
behavior or the choice cap.

At 10,000 choices, Chromium's non-standard `performance.memory` reported
11.9 MB used, 17.1 MB total and a 3.76 GB V8 JavaScript heap limit. The 16 MiB
decoded-body limit is below that reported JS-heap ceiling, but these are
JavaScript-heap observations, not process memory or the WebAssembly managed
heap. Firefox and WebKit expose no equivalent metric in this harness, so
browser-specific WebAssembly headroom remains unmeasured.

Story source size is not limited by the save limits: saves contain replay data
and continuation state, not story source. The largest measured save is the
realistic 10,000-choice save: 65,365 encoded
bytes and 7,033,682 decoded bytes. Twice the decoded size is 14,067,364 bytes
(13.42 MiB), so the selected 16 MiB decoded-body ceiling clears that minimum.
The 10 MiB compressed ceiling is more than eight times the largest measured
compressed save. Four times the decoded ceiling is 64 MiB; the independent
10,000-choice replay and continuation restores allocate 116.24 MiB and
125.10 MiB. This bound is not met, so allocation reduction is tracked as a
separate follow-up rather than addressed by raising the save limits.

The browser runs used Chromium 153.0.8010.12, Firefox 155 and WebKit 26.6 on
the same host, with .NET 10.0.15. They are single local runs with 50 autosave
samples per path; there are no constrained-device measurements. The quota
regression test also passed with a prepared largest measured realistic save in
all three browsers, preserving the existing slot when a new save fails.
The combined real-corpus acceptance filter was also attempted. Its Chromium run
had four unrelated failures at
[`RealCorpusPerformanceTests.cs:80`](../../player/tests/Talewright.Player.E2E.Tests/RealCorpusPerformanceTests.cs):
the visit-count expectations were 201, 2,001, 10,001 and 20,001, while the
observed values were 199, 1,999, 10,000 and 20,000. The four `SaveScalePerformanceTests`
and the low-compressibility real-corpus test passed in that Chromium run. The
combined run was stopped after Chromium; the SaveScale measurement and quota
tests were separately run and passed in all three browsers.

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
# Save, replay-restore and continuation-restore at each independent history depth.
MSBUILDDISABLENODEREUSE=1 dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks.*AtConfiguredDepth*history-*' --artifacts artifacts/scorecard/saves

# Normal-PR work-count and warmed Release allocation gates.
MSBUILDDISABLENODEREUSE=1 dotnet test engine/tests/Talewright.Runtime.Tests -c Release \
  --filter FullyQualifiedName~ReplayAllocationTests

# All realistic-story benchmarks, including 20-chapter save, replay and continuation cases.
MSBUILDDISABLENODEREUSE=1 dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RealisticStoryBenchmarks*' --artifacts artifacts/scorecard/saves-realistic

# Browser real-corpus and save-scale measurements, including Chromium, Firefox and WebKit.
python3 player/tests/run_e2e.py web \
  --filter "FullyQualifiedName~RealCorpusBrowserPerformanceTests|FullyQualifiedName~SaveScalePerformanceTests"

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
