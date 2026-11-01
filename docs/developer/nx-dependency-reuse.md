# Nx dependency-download reuse experiment

Follow-up to [#259](https://github.com/squid-farm/talewright/issues/259), measured on
2026-10-26 UTC. The original Nx epic and affected-routing follow-ups are already delivered.
This experiment does not implement #258's general Actions summaries.

## Decision

**Do not add new dependency caches on this evidence.** Local download reuse saves seconds,
not minutes, per installation; transfer and save overhead can consume that benefit.
Keep the existing editor npm cache. Leave browser/native and package verification uncached.
No workflow, wrapper installation, task-result caching, selection, concurrency, or coverage
behavior changes are needed for this result.

The substantial savings already come from affected routing: approximately **166 runner-minutes
avoided across seven successful PR runs** in the observed window, including two deliberate
acceptance probes. Excluding those probes, the estimate is **82 minutes across five runs**.
These are counterfactual skipped-job estimates, not measured before/after speedups.

## Historical baseline

Use the last meaningful Nx research, not a month-long lookback:

- No-impact probe [40054444717](https://github.com/squid-farm/talewright/actions/runs/40054444717)
  and infrastructure probe [40054508516](https://github.com/squid-farm/talewright/actions/runs/40054508516).
- Fixed collection window: `2026-10-24T02:30:00Z` through `2026-10-26T04:16:08Z`.
- 67 Build runs, one complete API page: 49 successful, 11 failed, five cancelled, two in progress.
  Successful runs comprise 33 PR runs and 16 main pushes.
- All 65 completed runs have attempt/job evidence: 65 attempts, 902 job records, no reruns.
  In-progress runs are explicitly unmeasured, not zero-duration successes.
- All historical job-duration costs below exclude publication. Failures/cancellations are retained
  in the inventory but excluded from the successful full-run cost reference.

Collect with `gh api` using the Build workflow's `runs` endpoint, the explicit `created` interval,
`per_page=100`, and pagination until exhausted. For every completed run collect
`actions/runs/{id}/attempts/{attempt}/jobs`, also paginated. Preserve timestamps, conclusions,
steps, event, SHA, runner labels, and the fixed cutoff. Job durations are
`completed_at - started_at`; never multiply workflow elapsed time by job count.

### Existing affected-routing savings

For each successful PR run with skipped verification jobs, cost only those skipped jobs against
each of the 16 successful full main runs in the window. Historical Tests workflows at the
respective SHAs all reference the same `pw1.63.0-r1` UI image. Expand a skipped browser group
into its three browser jobs; likewise map the reusable editor group to its leaf job.
Take the median **sum per full run**, rather than summing independent per-job medians.
Keep observed selected-job costs unchanged; do not invent per-task savings within selected jobs.

| PR run | Observed verification runner-minutes | Estimated avoided runner-minutes, median (reference range) |
|---|---:|---:|
| [40054444717](https://github.com/squid-farm/talewright/actions/runs/40054444717), no-impact probe | 0.73 | 43.73 (39.57-51.48) |
| [40054508516](https://github.com/squid-farm/talewright/actions/runs/40054508516), infrastructure probe | 3.58 | 40.48 (36.02-47.78) |
| [40120874604](https://github.com/squid-farm/talewright/actions/runs/40120874604) | 3.20 | 36.27 (31.93-43.12) |
| [40232193186](https://github.com/squid-farm/talewright/actions/runs/40232193186) | 43.33 | 1.61 (1.23-2.08) |
| [40233903441](https://github.com/squid-farm/talewright/actions/runs/40233903441) | 44.15 | 1.61 (1.23-2.08) |
| [40252587046](https://github.com/squid-farm/talewright/actions/runs/40252587046) | 3.63 | 40.48 (36.02-47.78) |
| [40258392879](https://github.com/squid-farm/talewright/actions/runs/40258392879) | 42.57 | 1.61 (1.23-2.08) |

The median estimates sum to **165.78 runner-minutes**, with summed reference extrema of
147.23-196.42 minutes. That range is **not a confidence interval**. The two synthetic probes
account for 84.21 estimated minutes; do not extrapolate their frequency to ordinary development.
The remaining 26 successful PR runs skipped none of these job groups; savings within their
selected groups are not estimated. Same image reference is only partial comparability:
different code, test counts, task subsets, mutable image tags, and runner load remain confounders.

The no-impact probe's 54-second workflow elapsed time is distinct from its **44 summed runner
seconds** across selection/planning/verification jobs. Neither quantity establishes a before/after
speedup against the earlier cancelled run.

## Local method

Use [measure_dependency_reuse.py](../../.github/scripts/measure_dependency_reuse.py):

```sh
python3 .github/scripts/measure_dependency_reuse.py \
  --candidate all --trials 3 --output /absolute/path/to/new-evidence-directory
python3 -m unittest discover -s tests -p test_nx_dependency_reuse.py -v
```

Requires Python 3.12+ for safe tar extraction, Git, Node/npm, and the repository .NET SDK.
Output must not already exist. The script measures committed **HEAD**, not uncommitted source.
It records commands, exit codes, monotonic durations, logs, sizes, and paired statistics.
Failures persist as failed records and stop the experiment; they are not fast successful samples.

Baseline source: `517bd12e54d9af1430a25e42736284ec069d29c6`; local tools:
Node `22.13.1`, npm `10.9.2`, .NET `10.0.113`, Python `3.13.2`, Linux.
Each condition gets a fresh `git archive HEAD` checkout, without generated Nx installations,
node_modules, bin/obj, or task outputs. Each pair has its own initially empty dependency store.
The cold condition seeds only the warm condition's download/package store.
NuGet HTTP-cache paths are distinct; `--force --no-http-cache` avoids timing a no-op restore.
The same fresh build/test commands run in both conditions; .NET builds are serialized.

Nx measures first-use wrapper installation with `./nx --version`; editor measures `npm ci`
followed by Node tests. NuGet measures restore of the Talemark test project followed by its Release
build/tests. This is a representative ordinary-job sample, not an exhaustive solution restore.
The initial NuGet pilot ran all 345 compiler tests in each condition; the reproducible script
uses the normal CI exclusions for separately scheduled editor snippet/schema contracts.
Three additional confirmation pairs used those exact CI exclusions, with all 342 tests passing
in each of their six fresh builds.

Three cold-then-warm pairs per candidate assess variation. Cold-first ordering, OS page/DNS caches,
shared machine load, compiler/JIT startup, npm audit/registry behavior, and unpinned transitive Nx
resolution remain confounders. These are local observations, **not hosted cache benchmarks**.

### Install/restore observations

Seconds below exclude archive overhead. Savings use the median of paired differences, which
need not equal the difference between the two condition medians.

| Candidate | Cold seconds, trials 1/2/3 | Warm seconds, trials 1/2/3 | Paired savings median (range), seconds |
|---|---|---|---|
| Nx npm downloads | 7.177 / 5.808 / 6.907 | 3.495 / 2.928 / 4.087 | 2.880 (2.820-3.682) |
| Editor npm downloads | 8.560 / 5.604 / 6.592 | 3.835 / 4.723 / 4.573 | 2.019 (0.881-4.725) |
| NuGet pilot | 5.947 / 4.867 / 5.296 | 4.478 / 2.920 / 3.482 | 1.814 (1.469-1.947) |
| NuGet CI-filter confirmation | 4.989 / 4.147 / 4.730 | 2.923 / 3.124 / 3.361 | 1.369 (1.022-2.065) |

Median command totals including verification: editor 12.32s cold / 9.83s warm;
NuGet pilot 24.90s / 19.63s; CI-filter confirmation 17.91s / 18.68s.
The confirmation's warm total was slightly slower despite a faster restore.
Do not attribute all build/test variation to downloads. Editor Node tests and all compiler tests
passed in every condition.

### Store size and local overhead proxy

| Store | Uncompressed MiB, approximately | gzip MiB | Local save seconds, median | Local restore seconds, median |
|---|---:|---:|---:|---:|
| Nx npm | 113.0 | 32.32 | 2.616 | 0.888 |
| Editor npm | 25.1 | 22.98 | 1.296 | 0.763 |
| NuGet pilot | 94.14 | 47.94 | 9.531 | 1.331 |
| NuGet CI-filter confirmation | 94.14 | 47.94 | 7.405 | 0.983 |

Archive sizes were stable to two decimal places across three pairs. npm store growth from cold
to warm was about 0.07 MiB for Nx and 0.06 MiB for editor; NuGet size was unchanged.
This short experiment does not measure long-term lockfile/version churn.
The proxy uses local gzip/filesystem copies. Actions uses its own compression and network service;
these numbers are **not** measured Actions restore/save overhead.

## Hosted evidence and break-even

Across successful steps in completed runs, including partially failed runs:

- Selection: 64 successful steps, median 11s, range 7-14s, including Nx selection and installation.
- Editor npm install: 53 successful steps, median 4s, range 2-8s.
- Editor setup-node with caching: 53 successful steps, median 2s, range 1-7s.
  This includes Node setup, not exclusively cache overhead.

Direct job logs from successful [40265213141](https://github.com/squid-farm/talewright/actions/runs/40265213141)
and [40253117826](https://github.com/squid-farm/talewright/actions/runs/40253117826) show:

- Existing editor exact-key hits restore 56,991,039 bytes in about 1.16s and 1.42s from
  hit announcement to restore completion. No cache save occurs on these exact hits.
  Editor installs still execute and report 4s / 3s. Nx installation also still executes.
- The actual UI image verifies `pw1.63.0-r1`, Node `22.23.4`, and the non-root environment.
  Chromium/Linux jobs still install 94 Nx packages in 5-7s and restore the E2E project in
  approximately 4.84-6.14s. Project restore timings are not summed: restores may overlap,
  and Nx can buffer their log timestamps.
- Therefore the image does not eliminate all package downloads, but this alone does not
  justify another cache. The Docker daemon was unavailable locally; no container cold/warm
  result or browser/native verification result is claimed.
- Five of six whole-run `gh run view --log` captures were empty despite successful CLI exits.
  Those are missing evidence, not cache misses. Direct job-log REST requests supplied the
  ten nonempty job logs used above.

For exact-hit rate `h`, cold-minus-warm install time `D`, hit restore overhead `R`, miss lookup
overhead `L`, and miss save overhead `S`, estimate:

```text
net seconds per eligible job = h * (D - R) - (1 - h) * (L + S)
break-even h > (L + S) / (D - R + L + S), provided D > R
```

Partial-key hits that save a new entry need additional save cost. Do not count the same npm store
twice or assume all selected groups restore identical NuGet dependency sets.

For Nx, the optimistic all-hit, zero-overhead local estimate is only **0.048 runner-minutes per
installation**. At 64 successful selection steps plus 58 successful ordinary unit router steps,
that is **5.86 gross runner-minutes** across this sample, before any cache overhead.
It excludes packaging, UI, editor, and checks rather than assuming their reuse is additive.
For an illustrative hosted `R=2s, L=0.5s, S=4s`, the median local Nx delta needs an approximately
84% hit rate just to break even. At `R >= 2.88s`, even all hits have no median benefit.
Those assumptions are sensitivity examples, not measured hosted Nx cache performance.

NuGet confirmation's 1.37s gross benefit leaves only about 0.39s after the **local** restore proxy;
its 7.41s save proxy needs roughly 95% exact hits even with free miss lookup and no transfer.
Larger unit dependency sets may behave differently. This is insufficient evidence to add a
general NuGet cache. The existing editor cache has observed cheap hits, but noisy local deltas;
retain it without claiming a newly measured incremental optimization.

**Incremental hosted savings delivered by this change: zero.** A future hosted experiment would
need authorization, equivalent repeated cold/warm jobs, hit/miss/save telemetry, real job elapsed
times, and positive net savings beyond variability before enabling new reuse. It is not necessary
to weaken tests or change main verification to get that evidence.

## Storage, billing, and limits

At collection, the repository is public and the observed jobs use standard Ubuntu hosted runners.
[GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
documents free standard hosted execution for public repositories: reducing these runner-minutes
does not imply a cash saving. No account billing statement was accessed.
Summed runner-seconds, workflow wall time, and a hypothetical sum of per-job whole-minute ceilings
are different quantities; do not label fractional runner-minute estimates as actual billed minutes.

The cache inventory has six entries totaling 188,833,065 bytes, approximately 180.1 MiB.
They are existing npm caches, including branch/PR-scoped and older lockfile entries.
The [dependency caching reference](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching)
documents seven-day inactivity eviction, a default 10 GB repository limit, and least-recently-used
eviction at the configured limit. Storage beyond the included 10 GB can be billed if the limit
is explicitly increased. Do not increase it for this experiment.
PR-scoped entries cannot be assumed reusable by other PRs; default-branch caches matter for hit rate.
Policies were checked for this report and can change.

## Evidence and validation

Session evidence retains raw run/job JSON, historical workflow snapshots, cache inventory, direct
job logs, routing calculations, and per-trial command logs/JSON. The tables above preserve the
relevant compact results in the repository; raw logs and dependency stores are not checked in.
Successful trial checkouts/stores and local archive proxies are removed by the measurement script.
On failure, its output identifies the retained directory for investigation.

[Measurement tests](../../tests/test_nx_dependency_reuse.py) cover matching trial pairs, negative
savings, failed/incomplete records, fresh-output isolation, pair-local stores, cleanup, and nonzero
command propagation. Changes to the experiment script select only the CI checks. Existing full/fallback, browser matrix,
serialized builds, and fail-closed aggregate contracts remain unchanged.

Validation ran the existing 47 Nx regression cases. The new script initially required adding
its explicit infrastructure scenario; after that correction, all five infrastructure cases passed.
The other 46 cases had passed, as did the four new measurement tests and documentation-link test.
The initial separate 24-case CI contract run exposed a pre-existing failure (two host subtests):
`test_both_hosts_embed_all_bundled_story_files` expected only tale/YAML files while the hosts also
embed four JPEG assets. This was reproduced in a pristine baseline checkout. PR CI reproduced
the same failure, so the exact upstream #249 assertion fix was subsequently applied: the expected
inventory includes bundled image files as well as tale/YAML files. Host embedding behavior is
unchanged.
