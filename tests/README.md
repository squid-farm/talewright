# Testing the engine and player

This is the repository-wide verification guide. Product suites live alongside
their areas in `engine/tests`, `player/tests`, and `tooling/*/tests`; root `tests`
contains Python CI/Nx contracts. Performance workloads and report tools live in
`benchmarks`.

## Bounded transcript and History evidence

`TranscriptHistoryTests` covers bounded main/History mounts, non-actionable
recorded choices, unchanged save identity, keyboard focus, shared image URLs,
opt-in acquisition and page/close during transfer or decode. Delayed-media tests
insert a gate in the existing browser module after actual stream reading or image
load; the normal module is used by the remaining cases. Native accessibility
coverage is in `NativeTranscriptHistoryTests`. Existing Back/Forward, choice-limit,
toolbar, image-lease and save tests remain independent regressions.

The opt-in `RealCorpusTranscriptPresentationTests` follows 120 choices through
four paragraphs per passage, both text-only and with distinct image paths holding
real camp artwork resized to 320x180 JPEGs. Run with:

```bash
python3 player/tests/run_e2e.py web --browser chromium --filter 'FullyQualifiedName~RealCorpusTranscriptPresentationTests'
```

The initial Chromium 153.0.8010.12 baseline on main `31e4d30`, using the standard
tracing harness and reduced motion, mounted 21/51/101/121 passages at the sampled
depths. Illustrated URL creation was 21/51/101/121 with zero revocations. Text
choice-to-DOM samples were 52.5-60.0 ms; illustrated samples were 67.3-81.4 ms.
These are four single-run observations, not percentiles or device budgets.

The initial internal window is 20 prior passages plus the current one (21),
matching the smallest sampled window rather than retaining the entire transcript.
History pages use the same bound: at most 42 passage components coexist, including
the hidden main-play window. In the first after run, mounts stayed at 21; text
samples were 37.7-51.8 ms and illustrated samples 46.8-64.5 ms. At the final sampled
transition 120 URLs had been created and 100 revoked, before the newest picture's
explicit decode wait. The same-environment comparison target is no more than twice
the baseline's worst observed choice-to-DOM sample (162.9 ms), alongside the exact
mount/lifecycle bounds. This is a local regression target, not a universal SLA.
The final same-environment check observed 54.5-65.8 ms for text and 54.9-64.0 ms
for illustrated play, all below that target. After the last explicit image decode,
21 passages remained mounted with 121 URLs created and 100 revoked (21 retained).

The reported lifecycle counts do not measure retained browser memory or prove
immediate bitmap reclamation. This work does not introduce runtime checkpoints,
change save/replay, or activate the broader deferred attribution/device work.

## Generating test stories

`Talewright.TestStories` contains reusable `IfFaker`, `ChoiceFaker`,
`SceneFaker`, `DivertFaker`, `ExpressionFaker`, `EffectFaker`,
`SubstitutionFaker`, `ImageFaker`, markup and skill-check helpers. They
construct deterministic Talemark fragments; the seeded `TaleFaker` composes
them into scenes and prose. `ManifestFaker` builds manifest YAML and returns
references for declared variables, flags, characters, items and factions. It
also supports text variables, triggers, creation configuration and other game
metadata. Its sections follow the order in which each kind of declaration is
first added:

```csharp
var manifest = new ManifestFaker("test-story", "Test story", "start", 42);
var count = manifest.Number("count", 0);
var ready = manifest.Flag("ready");
manifest.Character("player", "Tester");

var tale = string.Join('\n',
    SceneFaker.Header("start"),
    IfFaker.Block(ready, SubstitutionFaker.Variable(count)),
    ChoiceFaker.Create("END", "Leave"));
var source = new StorySource(new Dictionary<string, string>
{
    [StorySource.ManifestFileName] = manifest.Build(),
    ["main.tale"] = tale,
});
```

Fragments such as `IfFaker.Open` and `MarkupFaker.LinkOpen` can also form
deliberately incomplete inputs. Keep literals when the exact syntax, diagnostic
range or malformed YAML is the behavior under test. `RealisticStory` and
`PerformanceCorpus` use these helpers while preserving their reference corpus bytes.
The console benchmark's scene loops and slow-input ladder also use the shared
fakers; fingerprint tests protect their reference inputs byte for byte.

The generated-fixture migration covers shared compiler/runtime stories,
Talemark compilation and reachability fixtures, runtime session/authoring,
creation, history, hub and image-rendering stories, and generated setup in the
player, authoring, CLI and language-server tests. In particular, the shared
baseline manifests in `StoryAnalysisTests` and the language-server `ServerTests`
are built by `ManifestFaker` without changing their line layout. The shared
player image fixture uses syntax fakers for image, condition, speaker, choice,
markup and scene constructs.

Intentional literal exceptions remain in parser/compiler tests that assert exact
Talemark syntax, malformed source, diagnostics or source ranges; YAML/schema
tests that exercise authored YAML syntax; and editor/LSP tests that mutate
precise source text or assert its positions. The checked-in story corpus and
byte-sensitive performance fixtures remain byte-stable. Supporting YAML files
in tests for split configuration remain explicit where the file boundary and
fragment placement are the behavior under test. Shared narrative examples remain
authored source where their prose or feature combinations are the scenario;
native debugger source/map tests retain literals when displayed source lines
are asserted. JavaScript schema/VS Code tests keep their own static YAML
fixtures because they cannot consume the .NET faker library.

## Fast .NET tests

```bash
dotnet test
```

For an explicit Release invocation, use `dotnet test Talewright.slnx -c Release` and verify
the output reports actual test counts.

The root solution includes compiler, runtime, rules, narrative, and core tests. It does **not** discover UI
tests or require downloaded browsers.
[Talewright.E2E.slnx](../player/tests/Talewright.E2E.slnx) is a separate,
opt-in solution under `player/tests`.

Save coverage includes optional metadata, unsupported-format rejection and canonical replay identity in the runtime
suite, plus modal focus/escape, narrow light/dark layout, summaries, redo-aware discard protection,
failed writes, failed loads and reload/process-restart persistence in the player suite.
Both IndexedDB and native Linux file storage are exercised; save lists never replay histories.
Quick Save tests cover repeated replacement, names colliding with the checkpoint's display label,
story isolation, redo and process/reload persistence, created identity, quota/transaction-abort
and native I/O failures. Sticky-toolbar tests check Quick Save during actual scrolling.

### Reproducible performance budgets

```bash
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/Talewright.Benchmarks -c Release

# Opt in to the 10,000-element slow-input extension; this may take several minutes.
DOTNET_TieredCompilation=0 dotnet run --project benchmarks/Talewright.Benchmarks -c Release -- --slow-input-stress

python3 player/tests/run_e2e.py web --filter FullyQualifiedName~PerformanceTests
```

The console workload generates 100/1,000/10,000-scene stories with substitutions,
effects and choices. It measures in-memory compilation and authoring analysis,
ZIP load+compile, startup, save serialization, full restore and Back+Forward at
100/1,000/10,000 choices (the save format's upper limit). Restore verifies cursor
and event counts. It also compiles and analyzes a 20-chapter `RealisticStory`.

Each operation warms once, then reports median, nearest-rank p95 (the maximum
of seven samples), and median and maximum current-thread managed allocations.
Compilation and analysis rows also print the source-byte denominator and median
and maximum allocated bytes per source byte. The denominator is the sum of
`Encoding.UTF8.GetByteCount` over every text in `StorySource.Files`: it includes
the manifest and Talemark files, but not paths, image data or an added UTF-8 BOM.
For compilation and analysis, source generation, byte counting and extra
result validation are outside the measured region. Current-thread allocation is
valid because these operations are synchronous on the measured thread; use a
correctly scoped total-allocation measurement if that changes.

The existing time-budgeted rows still fail on p95 over budget. Analysis, the
realistic compile row, and slow-input timings are report-only: they add no hard
wall-clock CI gates. The realistic compile and analysis rows instead fail if
**any** measured allocation exceeds their respective byte-per-source-byte
ceilings. First compile includes compiler JIT cost; later sizes share a warmed
process. Timings are not cold OS/process startup measurements, peak memory or
guarantees for arbitrary author code. For comparisons, record the runtime,
corpus, CPU, power source and power mode, and run without other build/test work.

The default slow-input ladder analyzes paragraphs with substitutions and inline
conditions, single-line substitutions, nested block conditions, unmatched
emphasis, unclosed links, and both plain choices and choices with requirements
and effects at 1, 5, 10, 20, 50, 100, 500 and 1,000 elements. The opt-in
extension adds 10,000 for each shape. Reusable generators
also cover deeply nested parentheses and unary prefixes, but the harness does
not parse those crash-prone shapes until #396. These timing reports are not
proof of linear CPU work; fixing issues should add deterministic work or
structural assertions and allocation bounds where applicable.

One post-#416 `--slow-input-stress` run on the calibration machine described
below reported these median / p95 times (milliseconds) and median allocations:

| Shape | 1,000 elements ms | 10,000 elements ms | 10,000 allocated MiB |
|---|---:|---:|---:|
| Paragraph lines | 27.10 / 28.77 | 1,913.86 / 1,991.37 | 118.07 |
| Inline substitutions | 2.68 / 4.96 | 72.61 / 89.40 | 50.73 |
| Nested conditions | 84.31 / 88.55 | 16,763.70 / 21,888.45 | 2,368.15 |
| Unmatched emphasis | 3.78 / 4.32 | 397.96 / 404.11 | 6.59 |
| Unclosed links | 4.14 / 4.52 | 384.24 / 400.72 | 6.61 |
| Choices | 3.06 / 4.49 | 72.60 / 74.79 | 39.99 |
| Choices with expressions | 32.62 / 34.63 | 382.72 / 402.56 | 241.60 |

This is one local run, not a CI timing gate; its p95 is the maximum of seven
samples. The harness took 189.83 seconds, and `time` reported a maximum process
RSS of about 852 MiB. Per-operation allocations are cumulative and must not be
confused with RSS.

#### Realistic allocation ceilings and ratchet

The reference is `RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource()`
with the default seed 57057. At this baseline it has 946 scenes and 22 source
files, totaling **709,952 UTF-8 bytes including the manifest**. The harness
prints these counts each run so corpus changes are visible.

Four post-#416 calibration runs on 2026-11-01 used .NET 10.0.14, Release,
`DOTNET_TieredCompilation=0`, Linux Mint 22.3 x64, an AMD Ryzen 9 7940HS,
AC power and the performance CPU governor. Two ran the already-built harness
directly; two used the Nx budget target with one Nx worker and
`DOTNET_PROCESSOR_COUNT=1` to bound build resources. Each run warmed once and
measured seven iterations; the maximum below is the largest sample across all
four runs.

| Workload | Median allocated bytes, across runs | Maximum allocated bytes | Maximum B/source byte | Ceiling B/source byte |
|---|---:|---:|---:|---:|
| Compile | 34,781,064–34,795,208 | 34,795,216 | 49.01 | 55 |
| Analyze | 60,046,632–60,073,688 | 60,073,984 | 84.62 | 95 |

For the rounded historical #393 figures of 33.5/57.4 “MB”, the same denominator
would yield approximately 47.19/80.85 B/source byte if MB meant decimal
megabytes, or 49.48/84.78 if it meant MiB. The issue does not record the raw
values or unit interpretation, so these are not an exact before/after comparison.

Any later PR in #392 that lowers a workload's allocation must lower its ceiling
to the new measured maximum plus 10% (rounding up as needed). No PR may raise a
ceiling without the lead's approval.

On the calibration machine, two already-built runs of the current main harness
took 7.34–7.86 seconds, and two runs of the updated default took 15.66–18.32
seconds, an additional 7.8–11.0 seconds locally. These wall times include the
`dotnet run` host, not a build; the harness also prints its own total excluding
build for comparison in CI. The local delta is not a measurement of hosted CI.
The target is about 30 seconds or less of additional default CI work.

Budgets deliberately allow slower CI/author machines: compile 100/1,000 scenes
500 ms and 10,000 scenes 2.5 s; ZIP load+compile 750 ms / 3 s; startup 250 ms;
save 500 ms; restore and Back+Forward 2 s. Browser tests independently enforce
upload+WASM compile+first playable render within 5 s (1,000 scenes) and 15 s
(10,000 scenes), then actually activate the next choice. They include ZIP
generation/transport, not engine compilation alone. They run with the ordinary
three-browser suite against trimmed Release output.

M2 local baseline (.NET 10.0.13, Linux Mint 22.3 x64, tiered compilation disabled):
10,000-scene compile median 138 ms/p95 145 ms, ZIP load+compile 135/149 ms;
10,000-choice restore 106/115 ms, Back+Forward 112/172 ms; save 0.23/0.24 ms.
Managed allocations were about 118 MiB for compile and 81 MiB for restore,
not retained/peak memory. Under concurrent build load, compile p95 rose to 878 ms,
illustrating why runtime configuration and machine load matter. Browser
1,000/10,000-scene upload+compile+playable-render measurements were
416/1,444 ms (Chromium), 572/1,856 ms (Firefox), 505/1,335 ms (WebKit).
These bounded source workloads
do not currently justify a second compiled-bundle format, serializer or compatibility
surface (#26); revisit with real complex stories and constrained-device profiling.

### Real-file BenchmarkDotNet suite

Issue #57 adds a separate, out-of-process BenchmarkDotNet suite. The
seven-sample `Talewright.Benchmarks` runner above remains the fast correctness/budget
check; it is not presented as BenchmarkDotNet evidence.

The deterministic corpus generator counts `story.talewright.yaml` within the file total and
creates exact expanded-byte totals. The four named tiers are 10 / 100 / 500 / 1,000
files, 100 / 1,000 / 10,000 / 50,000 scenes, 0.5 / 5 / 25 / 50 MiB, and
100 / 1,000 / 5,000 / 10,000 choice-history depth. Scene IDs and references are
unique/valid; generated story content includes substitutions, effects, conditions,
tags, priorities and bounded hub selection. Only the generator is stored in git.
It records the seed, counts, byte totals, archive size and a SHA-256 content
checksum. Repeated comment padding makes the primary ZIPs highly compressible;
a separate deterministic low-compressibility case exercises a larger upload.

```bash
# Generate every tier, compile each from disk and ZIP, then play/save/restore its history.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- --verify-corpora

# Full core suite, with five warmups and thirty measured iterations per case.
TALEWRIGHT_BENCHMARK_OUTPUT="$PWD/artifacts/performance/metadata" \
  dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*StoryBenchmarks*' --artifacts artifacts/performance/core

# Focused independent file-count, byte-volume, scene-count and history-depth cases.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*IndependentDimensionBenchmarks*' --artifacts artifacts/performance/dimensions

# Realistically shaped stories of about 1,000 and 10,000 scenes.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*RealisticStoryBenchmarks*' --artifacts artifacts/performance/realistic

# Exact and one-over directory/ZIP file-size, aggregate-byte, file-count and depth checks.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*BoundaryInputBenchmarks*' --artifacts artifacts/performance/boundaries

# Generate a corpus outside the BenchmarkDotNet lifecycle, for integration workloads.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --generate-corpus Gigantic artifacts/performance/gigantic-story

# Full web, single-RID and universal desktop publication (45-minute bound each).
python3 benchmarks/performance/run_publication.py \
  --story artifacts/performance/gigantic-story \
  --cli tooling/cli/src/Talewright.Cli/bin/Release/net10.0/Talewright.Cli.dll \
  --output artifacts/performance/publication

# Run one method or tier by matching its BenchmarkDotNet name/parameter.
dotnet run --project benchmarks/Talewright.BenchmarkDotNet -c Release -- \
  --filter '*LoadAndCompileDirectory*Large*' --artifacts artifacts/performance/large
```

The default job uses one out-of-process launch, five warmups and thirty measured
iterations so the reported p95 has more than a handful of samples. The reports
include BenchmarkDotNet full JSON and CSV, GitHub-flavored Markdown,
P95/mean/dispersion, GC counts and managed allocations. A metadata JSON sidecar
records the commit and dirty-worktree flag, corpus seed/checksum/dimensions,
BenchmarkDotNet and .NET runtime versions, OS, CPU, architecture, physical and
runtime-available memory, and the configured power/cache note. The recorded worker
peak working set is process-wide (including setup/harness), not per-operation or
retained memory. Set `TALEWRIGHT_BENCHMARK_POWER_CACHE` to describe local power/cache
conditions; GitHub-hosted runner cache and power settings are not controlled.

The suite also measures a fresh CLI process validating the corpus, warmed runtime
startup/first frame, directory and ZIP loading, compilation, choices, serialization,
real `FileSaveStore` write/read, restore, Back/Forward and story-only CLI publication.
The warmed hub-choice case separately measures selection from the generated bounded
hub, which offers at most 20 storylets. `BoundaryInputBenchmarks` times exact and
immediately-over former directory ceilings (now accepted) and retained ZIP text file-size,
expanded-size and file-count defenses, plus directory nesting depth. The runtime suite also verifies a valid save
at exactly 2 MiB with legal 512-character choice keys; the first additional choice
crosses the byte cap. Existing compiler/runtime/CLI/player tests provide matching
ZIP, UTF-16/BOM, upload, save-store, 10,000-choice and failed-publication safety
checks. Those hard correctness/limit assertions remain separate from noisy
performance comparisons.

The corpus tiers measure size and limits, not story structure: each scene has one real
destination, the scenes form a single ring with no endings, and one hub matches every scene.
`RealisticStoryBenchmarks` compiles a story generated by `RealisticStory`, shaped like a real story:
- chapters of about 50 scenes, with branches that merge at bottlenecks and paths that sometimes cross;
- one hub per chapter matching 10–15 storylets plus shared act rumors, with storylets looping back;
- skill check retry loops and occasional deaths;
- conditions, effects, items and substitutions;
- a finale with five endings, and one unreachable cut scene every ten chapters.

`RealisticStoryProfile` holds its ranges and weights; `RealisticStory` only decides structure. `TaleFaker`
(in `Talewright.TestStories`) writes each scene with one call: titles, labels, prose with substitutions,
speaker lines, inline conditions and choices, all declared by a generated cast's manifest. Other generated
tests can use it directly, for example `fake.Scene("camp", fake.Choice("END"))`. Bogus (pinned) supplies the
seeded randomness, names and text. Each chapter, act and the finale has its own seed, so changing one part
leaves the other files unchanged, and the same profile and seed produce the same files within a Bogus
major version. Upgrading
Bogus to a new major version may change the generated story, so treat benchmark results from before and
after such an upgrade as separate baselines. `RealisticStoryTests` keeps the generated story's shape
within realistic ranges.

The full browser and native integration workloads are opt-in because they compile
the 50 MiB / 50,000-scene tier. The manual/scheduled `Real-file performance`
workflow runs them separately from ordinary per-PR browser and Linux UI tests:

```bash
python3 player/tests/run_e2e.py web --filter 'FullyQualifiedName~RealCorpusBrowserPerformanceTests'
python3 player/tests/run_e2e.py linux --filter 'FullyQualifiedName~RealCorpusNativePerformanceTests'
```

Browser integration records archive upload through first playable passage, a choice,
IndexedDB write/restore, browser identity and main-thread long tasks. The Linux
Photino case records fresh application open through first playable passage and a
choice. These are single end-to-end observations, not percentiles. The scheduled
workflow separately times real CLI web, single-RID desktop and universal desktop
publication. Each publication is individually bounded at 45 minutes and gets a
JSON result row (`succeeded`, `failed`, `timed_out`, or `runner_failed`), captured
stdout/stderr, exit code, output size, elapsed time and `/usr/bin/time` peak RSS
when available. Later workloads still run after an earlier failure or timeout.
The internal `dotnet publish`/SDK work is process-level integration time, not
engine compilation time. No performance budgets are inferred from one
machine or made a noisy per-PR gate; establish and compare target-specific budgets
after collecting stable baselines.

Every workflow run also publishes a Markdown summary in the Actions run and
stores compact, structured measurements in the `performance-results` branch.
Its `index.md` provides the cross-run overview; `reports/` contains a readable
report and `runs/` contains one JSON record per run attempt, including
BenchmarkDotNet means/p95/allocations, browser/Photino observations,
publication timings, corpus/runtime metadata, and workflow job outcomes. Raw
artifacts remain available from each Actions run for 30 days; the compact trend
records persist on the branch.
The index summarizes only the current run shape. Unsupported archived records remain linked as
original JSON, without format-specific interpretation or invented measurements.

Published workload labels preserve the CSV's `TierName`, `CorpusCase`, and
`Boundary` parameters. Player observations also retain the test outcome, so
measurements emitted before a later test failure are not presented as passing
tests. Browser evidence captures the viewport rather than the full document;
long histories can exceed browser screenshot size limits. This does not change
the player's transcript presentation (tracked separately in #23 and #70).

Benchmark setup/build/worker failures produce a nonzero runner exit code.
The bounded-hub case times entering the hub, including generated storylet
selection, rather than choosing from an already populated hub frame. Player
choice timings wait for changed passage content, and restore timings wait for
the loaded-slot confirmation and expected transcript depth. Failed real-corpus
tests remain in published reports even when they emit no measurement JSON.
Publication results completed before interruption are retained from individual
workload files when no aggregate summary exists.

Results recorded before these operation-completion fixes do not validate
hub-selection, browser choice, or IndexedDB restore latency: the old hub case
left the hub, and the old player assertions could match unchanged content.
Retain those historical artifacts as evidence, but do not use those particular
timings as baselines for the corrected workloads.

The weekly schedule runs on the repository's default branch. To measure a
different branch such as M2 on demand, open **Actions → Real-file performance
→ Run workflow** and select that branch; the run report records the selected
branch and commit. Keep branch, runtime, and workload comparable when
interpreting changes, since hosted runner hardware and load can vary.

## One test-authoring surface

All player E2E cases and assertions live in **C#/xUnit** in `Talewright.Player.E2E.Tests`.
Shared behavior tests use `IPlayerDriver`: Playwright drives Chromium, Firefox, and WebKit, while a
thin Python AT-SPI adapter drives the actual Linux Photino window. The adapter contains native UI
actions, observations, process cleanup, and evidence capture only: no test discovery, story scenarios,
expected values, assertions, or separate test-report format.

Use the same xUnit assertions, fixture definitions, and `dotnet test --filter` syntax for both hosts.
DOM/CSS/media checks are explicitly marked `Host=Browser`; CLI/host checks are marked `Host=Linux`.
The runner excludes the other host's cases and combines that scope with your filter. Shared cases have
neither host trait and run on both. Native execution initializes neither Playwright nor a web server.
Browser observations read the DOM in one synchronous snapshot so an upload or library transition cannot
remove controls between a presence check and a text read. A driver regression exercises rapid replacements.

## CI and deployment checks

The Build workflow calls the reusable Tests workflow once per run. Tests retains
unit, Chromium, Firefox, WebKit, and real Linux Photino jobs on Ubuntu 24.04.
Each job uploads its reports/evidence even on failure. Both Build (including
package verification) and Tests must pass before any publishing job.

Test projects retain their original project references and suite boundaries.
The `packaging/Talewright/Talewright.csproj` packaging shell bundles the six headless
assemblies without moving their source or merging their projects. The engine package has no Blazor
dependency. CI packs the `Talewright` and `Talewright.Player` libraries and the `Talewright.Cli` tool, then restores
and builds isolated engine-only and Razor consumers using a fresh package cache.
This checks real package references, the Player's transitive engine dependency,
and its CSS/JavaScript assets, not just project-reference builds. CI also packs and installs
the separate `Talewright.Cli` tool, running JSON validation and story/standalone
publication outside the checkout.
The same isolated package check builds the C# authoring example's web and desktop
hosts, then injects invalid Talemark into its temporary copy and requires the build
to fail with the exact file/line and stable compiler code.

```bash
python3 -m unittest discover -s tests -p 'test_ci.py' -v
dotnet build Talewright.slnx -c Release -p:Version=0.1.0-preview.1
dotnet pack packaging/Talewright -c Release --no-build -p:Version=0.1.0-preview.1 -o artifacts/packages
dotnet pack player/src/Talewright.Player -c Release --no-build -p:Version=0.1.0-preview.1 -o artifacts/packages
dotnet pack tooling/cli/src/Talewright.Cli -c Release --no-build -p:Version=0.1.0-preview.1 -o artifacts/packages
python3 .github/scripts/verify_packages.py artifacts/packages 0.1.0-preview.1 --smoke
```

Use a fresh output directory when changing package versions: verification rejects
unexpected/duplicate packages instead of accidentally publishing stale files.
Pack the three projects sequentially: solution-wide packing can evaluate the
CLI's publish graph concurrently with RCL packing and omit static assets.

Pages publishing also checks the real static output at the site root, where
`play.trytalewright.com` serves it. Use `--base-path /` and
`TALEWRIGHT_WEB_BASE_PATH=/` to reproduce CI exactly; the commands below check a
project-subpath deployment after installing Chromium:

```bash
python3 player/tests/run_e2e.py install-browsers --browser chromium
python3 .github/scripts/publish_samples.py --base-path /talewright --output artifacts/pages
BROWSER=chromium TALEWRIGHT_WEB_ROOT="$PWD/artifacts/pages/wwwroot" \
  TALEWRIGHT_WEB_BASE_PATH=/talewright TALEWRIGHT_TEST_RESULTS="$PWD/artifacts/test-results/pages" \
  dotnet test player/tests/Talewright.Player.E2E.Tests -c Release --no-build \
  --filter FullyQualifiedName~Bundled_sample_assets_work_when_served_at_the_selected_base_path
```

The fixture serves only the selected path, rejecting root requests outside it.
The check opens a bundled sample, verifies Player styling, and activates a choice;
resource/console errors fail it. No sample-specific plot text or route is asserted.
Without `TALEWRIGHT_WEB_BASE_PATH`, ordinary root-hosted browser tests are unchanged.

## Browser player tests

Requires the .NET 10 SDK and Python 3. No Node/npm project or PowerShell installation is needed.

```bash
# Once, and again when the pinned Microsoft.Playwright package changes.
# --with-deps may require sudo to install browser system libraries.
python3 player/tests/run_e2e.py install-browsers

# Publishes the real web host in Release, serves it on an owned loopback port,
# and runs the same functional cases in Chromium, Firefox, and WebKit.
python3 player/tests/run_e2e.py web

python3 player/tests/run_e2e.py web --browser firefox
python3 player/tests/run_e2e.py web --browser chromium --filter FullyQualifiedName~StoryLoadingTests
```

GitHub Actions browser and Linux native UI jobs use a prebuilt versioned GHCR image
(`ghcr.io/squid-farm/talewright-ui-tests:pw1.63.0-r1`) instead of reinstalling browser/native dependencies
every run. Those jobs call `.github/docker/ui-tests/verify.py` before test execution to enforce
Playwright/.NET compatibility with the checked-out repository pins and to require non-root execution.

The helper uses a temporary publish directory and removes it after the run. The .NET suite owns its Kestrel
static server and browser contexts, waits for readiness, and disposes them without touching other servers.
There are no application test switches and no fake web host.

To run directly against an existing published output:

```bash
dotnet publish apps/web -c Release -o artifacts/web
TALEWRIGHT_WEB_ROOT="$PWD/artifacts/web/wwwroot" BROWSER=webkit \
  dotnet test player/tests/Talewright.Player.E2E.Tests --filter 'Host!=Linux'
```

Use the helper for the normal path; direct invocation requires the matching browser binaries to be installed.

### Standalone publisher artifacts

`ToolbarTests` measures real passage movement while scrolling down and up, then
checks that the primary controls remain in the browser/native window at narrow
and wide widths. Browser coverage also checks focused-choice/passage clearance,
light/dark backgrounds, history boundaries, disclosure keyboard behavior, modal
Restart confirmation, cancellation, and focus restoration. Restart and Library
are reached through **More actions**; Restart requires confirmation.
Toolbar regressions also delay the JavaScript module response to check initialization
gating, exercise save refresh after tabbing out of the disclosure, and verify the
open popup stays inside a viewport narrow enough to wrap the primary controls.

CI also publishes a selected story with no development samples and tests the
extracted ZIP. Standalone cases are explicitly skipped in normal development-host
runs; opt in with `TALEWRIGHT_STANDALONE=1` and `Artifact=Standalone`.
These checks verify auto-start, title, play, save/load across a host restart,
history, Restart, absence of Library/upload controls during creation and play,
and (in browsers) real styling, JS and network errors.

```bash
dotnet run --project tooling/cli/src/Talewright.Cli -c Release -- \
  publish samples/LostRelic -a --target web --output artifacts/game-web.zip
unzip artifacts/game-web.zip -d artifacts/game-web
TALEWRIGHT_STANDALONE=1 TALEWRIGHT_WEB_ROOT="$PWD/artifacts/game-web" \
  TALEWRIGHT_STANDALONE_CREATION=1 TALEWRIGHT_PUBLISHED_CHARACTER=Mara \
  TALEWRIGHT_WEB_BASE_PATH=/games/published BROWSER=chromium \
  dotnet test player/tests/Talewright.Player.E2E.Tests -c Release --filter Artifact=Standalone

dotnet run --project tooling/cli/src/Talewright.Cli -c Release -- \
  publish samples/LostRelic -a --target desktop --output artifacts/game-desktop.zip
unzip artifacts/game-desktop.zip -d artifacts/game-desktop
TALEWRIGHT_STANDALONE=1 TALEWRIGHT_DESKTOP_EXECUTABLE="$PWD/artifacts/game-desktop/start.sh" \
  TALEWRIGHT_STANDALONE_CREATION=1 TALEWRIGHT_PUBLISHED_CHARACTER=Mara \
  python3 player/tests/run_e2e.py linux --filter Artifact=Standalone
```

The second command publishes all six OS/CPU builds; the native test launches the
universal ZIP's actual launcher and selected self-contained Linux binary.
`--runtime linux-x64` instead creates a flat single-runtime archive; point
`TALEWRIGHT_DESKTOP_EXECUTABLE` at its `Talewright.Host.Desktop` binary.
`TALEWRIGHT_PUBLISHED_TITLE` overrides the expected title for another story.
The Lost Relic case also requires a decoded camp image with its effective alt
(native geometry/browser natural dimensions), and uses Tomas from supporting configuration.
Repeat browser runs with `BROWSER=firefox` and `BROWSER=webkit`. Do not use the web
runner for extracted artifacts: it republishes the development host instead.

Additional illustrated-artifact cases use the same opt-in harness:

- Publish a folder matching [ImageStoryFiles](../player/tests/Talewright.Player.E2E.Tests/ImageStoryFiles.cs),
  with its split configuration and JPEG/PNG/WebP files. Set `TALEWRIGHT_PUBLISHED_TITLE="Folder images"`
  and filter `FullyQualifiedName~PublishedImageTests`. The case checks source-fragment text,
  actual decoding across narrative surfaces, keyboard choice/history, and browser Blob sharing
  and narrow layout. It runs on browser and native hosts.
  For external-layout artifacts, also set `TALEWRIGHT_PUBLISHED_EXTERNAL_STORY=1`;
  the browser case inspects resource timing, requires versioned image requests
  under the page's `story/images/` path, and rejects fetching the catalogued `unvisited.png`.
- For `FullyQualifiedName~PublishedImageFailureTests`, publish a story titled `Broken images`
  whose first passage contains `Before`, an undecodable PNG with alt `Broken map`, and a valid
  large JPEG with alt `Working camp`. A `Continue` choice must lead to a passage containing
  `Next passage.`. The test requires visible accessible failure, successful decoding of the
  other image and continued narrative play. Invalid image *references* and unsafe files are
  rejected before host build by the CLI publication tests.
- Publish a two-passage text-only story with a choice in each passage and run
  `StandaloneArtifactTests`, setting its title and leaving creation disabled. This retains the
  play/save/reload/Back/Restart contract without requiring artwork.

Publish each fixture afresh for web and `--runtime linux-x64`, extract each ZIP and point
the harness at that output, not at a source folder or previously published development host.
The illustrated-format and failure tests are explicitly skipped unless their matching title
is supplied; a skipped case is not acceptance evidence.

For a story with opt-in character creation, set `TALEWRIGHT_STANDALONE_CREATION=1`;
the same extracted-artifact test must complete creation before initial play and
after process/browser restart. Its initial passage must contain
`TALEWRIGHT_PUBLISHED_CHARACTER` (default `Tester`), proving identity substitution
survives trimming. All normal title, choices, save/restore, Back and library
assertions remain enabled.
Windows/macOS/ARM64 build verification is not a substitute for runtime testing.

### M2 verified matrix and remaining release checks

#### Post-feedback integration evidence (2026-10-23)

After the Saves dialog follow-up in [PR #140](https://github.com/squid-farm/talewright/pull/140),
main commit `ad68d16fa663856fd65712ea253f105d0173fd43` passed
[Build run 39979206122](https://github.com/squid-farm/talewright/actions/runs/39979206122).
Both verification aggregates and sample build/deployment completed successfully.
This includes the merged character-creation fixes, published-game navigation,
sticky toolbar, saves dialog, and Quick Save work tracked by
[#81](https://github.com/squid-farm/talewright/issues/81),
[#82](https://github.com/squid-farm/talewright/issues/82),
[#78](https://github.com/squid-farm/talewright/issues/78),
[#79](https://github.com/squid-farm/talewright/issues/79),
[#76](https://github.com/squid-farm/talewright/issues/76), and
[#83](https://github.com/squid-farm/talewright/issues/83).

| Surface | Retained CI evidence |
|---|---|
| Headless Release | `unit-results`: 615 passed across seven projects (Core 19, Narrative 12, Rules 32, Runtime 243, Tale 95, Authoring 55, CLI 159) |
| Player in browsers | `player-chromium`, `player-firefox`, `player-webkit`: 106 passed per browser in the ordinary Release-host regression runs |
| Native Linux player | `player-linux-desktop/linux-regression.trx`: 77 passed against actual Photino/WebKitGTK; native probe also passed |
| Extracted standalone web ZIP | `standalone-chromium.trx`, `standalone-firefox.trx`, `standalone-webkit.trx`: one pass each against published Lost Relic with creation enabled, served beneath `/games/published` |
| Extracted standalone desktop ZIP | `linux-standalone.trx`: one pass through the universal launcher's self-contained Linux x64 payload, with creation enabled |
| Installed packages | Build job passed `package-check`, including both fresh library consumers and installed CLI validation/publication/C# consumers; packages retained in `nuget-packages` |
| CI/docs contracts | Checks job passed 22 and 29 Python contract tests and the documentation check |
| Bounded performance | Checks job passed the existing compile/load/startup/save/restore/history budgets; browser performance cases passed in all three engines |
| Graph selection and integration | `nx-ci-plan` records full verification, including both ordinary and published-artifact UI targets; test and CI aggregates passed, followed by sample build and deployment |

The standalone case is intentionally not executed against ordinary development
hosts: each ordinary browser/native TRX reports that one case as `NotExecuted`.
The separate standalone TRX files above prove it ran and passed against extracted
publisher output; these are not waived or retried failures. Standalone coverage
includes creation, play, save, browser reload/native process restart, load,
Back, Restart, and absence of Library/upload navigation.

The checks job's .NET 10.0.13 / Ubuntu 24.04.5 bounded workload reported these
p95 values (seven measured samples after warmup, not a new BenchmarkDotNet study):

| Workload | p95 | Existing budget |
|---|---:|---:|
| Compile 10,000 scenes | 221.92 ms | 2,500 ms |
| ZIP load + compile 10,000 scenes | 211.09 ms | 3,000 ms |
| Startup at 10,000 scenes | 0.02 ms | 250 ms |
| Save 10,000 choices | 0.48 ms | 500 ms |
| Restore 10,000 choices | 184.44 ms | 2,000 ms |
| Back + Forward at 10,000 choices | 179.61 ms | 2,000 ms |

The original cancelled #75 merge run is also resolved:
[run 39827756885, attempt 2](https://github.com/squid-farm/talewright/actions/runs/39827756885/attempts/2)
completed successfully on `60ea1be`. Its first attempt exhausted the native
Linux job's 20-minute limit; the earlier Firefox timeout was a separate apt-mirror
stall. The diagnosis is recorded on [#33](https://github.com/squid-farm/talewright/issues/33).
Current UI jobs use the pinned prebuilt test image rather than repeating that
dependency installation. No assertion timeout changes or failure waivers are
part of this closeout.

Windows, macOS and ARM64 native execution, and a clean-install Ubuntu prerequisite
check ([#85](https://github.com/squid-farm/talewright/issues/85)), remain unverified.
These results do not claim signing, notarization, installers, save migration, or
pre-1.0 backward-compatibility guarantees. The project lead decides when to close
the M2 milestone.

#### Original local delivery evidence

The following records the earlier Linux x64 delivery gates (.NET 10.0.13), not
the test counts or package version of the post-feedback CI run above:

| Surface | Evidence |
|---|---|
| Headless Release | Build with no warnings; 248 tests passed across six projects |
| Player in browsers | 64 passed each in Chromium, Firefox and Playwright WebKit; standalone case intentionally skipped in ordinary runs |
| Native Linux player | 52 passed against the actual Photino window; separate native preflight passed |
| Extracted standalone web ZIPs | Lost Relic and a creation-enabled story passed in all three browsers beneath `/games/final`; real styles/modules, title, choices, save/reload/Back, the then-current one-story library, no JS/network errors |
| Extracted standalone desktop ZIPs | Universal launcher's Linux x64 selection and a creation-enabled single-RID binary passed play/save/process-restart/load/Back |
| Desktop payloads | Linux/Windows/macOS x64+ARM64 self-contained builds, Photino libraries, .NET payload, executable modes and launchers verified |
| Installed packages | Three `0.1.0-m2.7` local packages passed fresh-cache consumers, installed CLI validate/story/web/universal publication, C# web/desktop builds and intentional invalid-Tale build failure |
| CI scripts | 12 Python contract tests passed locally; GitHub Actions had not yet run at that delivery checkpoint |

This matrix is local evidence, not an assertion that a release has been published.
Playwright WebKit is not macOS WKWebView. Windows, macOS and ARM64 native
execution remain unverified; signing, notarization, installers and macOS `.app`
bundles are not supplied. Before distributing a target platform, extract the ZIP
on that machine, run its launcher and verify title/play, manual save, process
restart/load, Back/Forward and library. Windows requires WebView2, Linux
WebKitGTK; macOS uses its system webview. A build-only payload check cannot replace
this execution matrix.

Each case records a screenshot, DOM, trace, and browser-error list under
`artifacts/test-results/<browser>/<case-id>/`. The xUnit output associates the case with that directory.
Evidence is retained for successful cases too, making a later visual comparison possible without rerunning.
The helper also writes one TRX report per browser. Open traces with the matching Playwright trace viewer
locally; they can contain story text and should not be uploaded to public trace-viewing services.

Console errors, uncaught JavaScript exceptions, failed requests, HTTP error responses, and the Blazor error
banner fail a test. Expected bad-story errors must appear in the player UI, not in an ignored console log.

## Real Linux desktop tests

This suite launches **Photino itself** and drives the installed WebKitGTK accessibility tree, not a browser
substitute. The same C#/xUnit project uses the distro's Python AT-SPI bindings as a command adapter;
dogtail and Selenium are not needed.

The current target is the Linux development machine: Ubuntu 24.04, distro WebKitGTK 4.1, .NET 10,
system Python 3, GTK 3, Xvfb, and AT-SPI. Windows is tested manually; cross-platform native automation
is not a prerequisite. GitHub Actions integration is separate from this test-authoring retrofit.
The distro's security-updated WebKitGTK is intentionally not frozen at a vulnerable package patch version;
the runner prints the installed versions for diagnosis.

```bash
sudo apt-get update
sudo apt-get install -y libwebkit2gtk-4.1-0 libnotify4 libgtk-3-0t64 \
  xvfb xauth dbus-x11 at-spi2-core libatk-adaptor python3-pyatspi python3-gi gir1.2-gtk-3.0 xfwm4 wmctrl

# Run as your ordinary user, not root.
python3 player/tests/run_e2e.py linux --probe
python3 player/tests/run_e2e.py linux
python3 player/tests/run_e2e.py linux --filter FullyQualifiedName~PlayerBehaviorTests
python3 player/tests/run_e2e.py linux --filter 'FullyQualifiedName~Explicit_story_scenario'
```

The runner builds the production desktop host and the same C# test project, and starts
a private Xvfb/D-Bus/accessibility session and Xfwm4 window manager, with isolated XDG
configuration/cache/data/runtime directories.
GSettings use the isolated keyfile backend with the GTK file chooser pinned to `startup-mode='cwd'`.
Distributions differ here (Linux Mint overrides it to `cwd`; Ubuntu keeps the upstream `recent` default),
and in Recent mode GTK ignores a typed location when Open is activated.
It uses `/usr/bin/python3` so a Python virtualenv does not hide the distro's GI/AT-SPI bindings.
Each case generates its own ZIP bytes or CLI directory directly from the C# fixtures, starts a fresh
Photino process through a JSON-lines adapter, and removes its temporary story files on disposal.
Tests locate controls by accessible roles/names and activate actions or actual keyboard events; no OCR or
coordinate-based clicking is used. Archive selection invokes the GTK location entry's own accessible
activation action (rather than sending Enter to the globally focused window) and waits
for the chooser to close; accepting an AT-SPI action alone does not mean GTK opened the selected file.
All native cases run serially. App logs, accessibility-tree dumps,
screenshots, and adapter logs are retained under `artifacts/test-results/linux/<case-id>/`.
Both hosts produce xUnit results in TRX: `artifacts/test-results/linux.trx` for the native run.
The xUnit output associates each result with its evidence directory. Old Python unittest filter names
are no longer supported; use the same .NET filter syntax shown above.

The feasibility probe checks bundled-story opening, choice activation, latest-passage focus, character-sheet
exposure, and the real native file chooser. Missing prerequisites, inaccessible controls, or unmatched
native test filters fail explicitly. Neither the native runner nor the browser suite silently skips a
missing engine. The WebKit sandbox is not disabled.
CI runs this probe before the full native suite so a broken chooser fails once instead of timing out
separately in every upload-dependent scenario.

## Stable, programmatically supplied stories

`Talewright.TestStories` contains named C# raw-string YAML and `.tale` definitions, rather than checked-in
loose archives/story directories. The existing compiler already accepts a dictionary of logical filenames
and contents:

```csharp
var source = new StorySource(new Dictionary<string, string>
{
    ["story.talewright.yaml"] = yaml,
    ["chapters/main.tale"] = tale,
});
var result = StoryCompiler.Compile(source);
```

Shared helpers produce that dictionary, in-memory ZIP bytes, or a temporary folder. Browser tests upload
the ZIP through the normal file input; native tests open the same bytes/folders using CLI arguments or the
native chooser; runtime tests compile the source directly. Product assemblies never reference this project.

`PlayerScenario` describes a short sequence of user choices with **explicit**, independently written expected
passage text, choices, sheet content, and terminal state. One C# test executes those steps against either
driver. Host-specific cases and all expectations stay in that same xUnit project rather than extending
this into a general testing DSL or introducing Python tests.

To inspect generated files deliberately:

```bash
dotnet run --project engine/tests/Talewright.TestStories -- /tmp/my-talewright-test-stories
```

That command leaves the requested directory for inspection; the normal E2E helpers create and clean their
own temporary files directly. Do not commit generated files. For deterministic checks, prefer `randomness: off`
and deliberate DCs. A dedicated seeded fixture tests replay; no test retries until a preferred roll occurs.

## Coverage and boundaries

| Behavior | Runtime/compiler | Three browser engines | Actual Linux Photino |
| --- | --- | --- | --- |
| Explicit branching, gates, hubs/once/priority, nested conditional arms | Yes | Shared scenarios | Same shared scenarios |
| Choice/entry checks, player/NPC, success/failure, effects | Yes | Shared scenarios | Same shared scenarios |
| Items, modifiers, quantities, faction triggers, progression | Yes | Shared scenarios and exact sheet assertions | Shared visible-state assertions |
| Transcript history, restart/replay, library/resume/replacement | Engine state only | Yes | Yes |
| Strong/emphasis/code/line breaks, speech, non-ASCII, inert HTML/media placeholders | Compiler/renderer | DOM assertions | Rendered text scenarios; not DOM-style assertions |
| Multi-file/root/wrapped ZIPs and errors/recovery | Yes | Yes | Generated fixtures and native chooser/CLI |
| 20 MiB upload acceptance/rejection | N/A | Exact boundary and +1 byte | Same boundary and invalid uploads |
| ZIP 1,000-file / 5 MiB-file / 50 MiB-total limits | Exact boundaries and rejection | Visible rejection/recovery | Visible rejection/recovery |
| Folder above former count/file/total ceilings | CLI validates and publishes | Not a browser folder source | Compiles and plays |
| ZIP image above former combined 50 MiB budget | Declared-size reads, repeat reads, cancellation | Real 51 MiB JPEG decodes and plays | Real 51 MiB JPEG decodes and plays |
| Keyboard, latest-frame focus, long-transcript scroll | N/A | Yes | Yes |
| Responsive layout | N/A | Narrow and breakpoint-adjacent viewports | Actual native window resize |
| Light/dark palette and reduced-motion preference | N/A | Browser media emulation | Not automatically asserted; OS-specific preference integration remains a native manual check |
| CLI auto-start, multiple paths, missing/bad paths, Unicode/spaces | Source loading only | N/A | Yes |
| Shipped sample integration | Compilation only | Bundled open/first frame only | Bundled open/first frame only; probe interactions use fixtures |

The sample smoke guards the real authored files and embedded-resource packaging. Detailed assertions do
not depend on its plot, wording, branch count, or route to an ending. The old sample-specific scripted and
random-playthrough assertions are replaced by deterministic fixture assertions; focused existing unit tests
remain independent.

The suite does not claim every possible story-path permutation, exact visual pixel equivalence, screen-reader
speech correctness, or coverage of unimplemented saves, character creation, images, or settings. Only library
states reachable in the production hosts are included: there is no test host to fabricate empty/loading/failing
library implementations.

## Other desktop platforms

- **Windows / WebView2 (manual for now):** Playwright supports attachment through CDP. A future Windows fixture can launch
  Photino with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<owned-port>` and an isolated
  `WEBVIEW2_USER_DATA_FOLDER`, then use `Chromium.ConnectOverCDPAsync`. Verify Photino startup/readiness,
  profile isolation, native chooser coverage, and process cleanup on Windows before claiming this coverage.
  Never enable remote debugging in the normal application.
- **Linux / WebKitWebDriver:** viable only after integrating WebKitGTK's automation-enabled context and
  session/view-creation hooks in the native host. Installing the driver alone does not automate stock Photino.
  This uses the WebDriver protocol with a client such as Selenium, not Playwright's WebKit protocol.
  Playwright has no supported direct attachment to the embedded WebKitGTK view; that is not a limitation
  on automating the Linux app itself. The current AT-SPI suite already does that with distro accessibility
  dependencies and needs no native fork or remote-debugging endpoint.
- **macOS / WKWebView:** Playwright cannot directly attach to an arbitrary WKWebView. Investigate native
  accessibility/XCTest automation on a macOS runner. SafariDriver drives Safari, not the Photino window.
- **Playwright WebKit is not Safari or the installed system webview.** It provides valuable engine coverage
  but cannot replace these native integration checks.

References: [Playwright .NET runners](https://playwright.dev/dotnet/docs/test-runners),
[WebView2/CDP](https://playwright.dev/docs/webview2),
[WebKit versions](https://playwright.dev/docs/browsers#webkit),
[WebKitGTK automation context](https://webkitgtk.org/reference/webkit2gtk/stable/method.WebContext.set_automation_allowed.html),
[automation-controlled views](https://webkitgtk.org/reference/webkit2gtk/stable/property.WebView.is-controlled-by-automation.html).
# Author Debugger checks

`dotnet test tooling/cli/tests/Talewright.Cli.Tests -c Release` covers CLI argument handling,
scene search over 1,000 scenes, all prerequisite types, character creation,
validation and failed-reload preservation. Reload tests also cover explicit YAML
gating, invalidated setup IDs, stale generations, concurrent play/compile,
atomic-save/rename/delete/burst patterns, watcher retry and shutdown.
Runtime author-reload tests verify pre-entry visits/effects/RNG/trigger parity,
changed current-entry behavior, schema/choice-meaning rejection and failed
candidate cascades without touching the good run.
Scenario tests cover character/setup/choice/edit/jump replay, cursor and redo, exact identities,
explicit cross-source adoption, normal-save forks, typed values and byte/field/command limits.
File/model checks verify one-shot overwrite confirmation, atomic replacement, rejected imports,
source-map retention, protected export paths (including parent symlinks), and unchanged input saves.

`python3 player/tests/run_e2e.py linux --filter FullyQualifiedName~DebuggerTests`
builds and exercises the actual native workbench: repair/reload, late-scene
search, setup, seed, choices, recorded explanations, watched Talemark adoption,
YAML restart gating, incompatible-history preservation and graceful window close.
The portable-scenario case performs setup at scene1000, explains a branch, reloads Talemark, edits state,
rolls dice, exports with pending redo, closes the native process and imports in another process.
Re-exported bytes must match before Forward resumes the retained redo.
It uses the same isolated
Xvfb/D-Bus/AT-SPI infrastructure as the player; assertions remain in C#.
Set `TALEWRIGHT_DEBUGGER_EXECUTABLE` to an installed tool's
`Talewright.Debugger.dll` to check packaged assets outside the checkout.
Package verification checks debugger/native assets, headless launch errors and
exclusion from Player packages and published games.
For a copyable fixture and beginner-oriented manual release checks, use the
[debugger test plan](../docs/guide/debugger-test-plan.md).
