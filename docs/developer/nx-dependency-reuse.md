# Dependency-download caching in CI

CI caches only the editor's npm dependencies and the VS Code build used by editor tests. Nx installation, NuGet restores, browser/native jobs
and package verification are not cached. This page explains why and how to re-measure if you want
to change that.

## Why there are no more caches

Reusing downloads saves seconds per installation, not minutes, and saving or restoring a cache costs
about as much. Local paired measurements (three cold/warm pairs per candidate, measured October 2026):

| Candidate | Median time saved by a warm store | Store size (gzip) |
|---|---:|---:|
| Nx npm installation | 2.9 s | 32 MiB |
| Editor npm installation | 2.0 s | 23 MiB |
| NuGet restore (Talemark tests) | 1.4 s | 48 MiB |

Saving the NuGet store locally took about 7 s, so a cache would need roughly 95% exact hits to break
even. Nx would need about 84% hits under optimistic hosted overheads. The existing editor cache
restores in about a second on exact hits, so it stays.

Affected-project selection is where CI time is actually saved: PRs that touch little skip most
jobs. The repository is public and runs on standard hosted runners, so runner minutes are free;
fewer minutes means faster feedback, not a lower bill.

## Re-measuring

Use [measure_dependency_reuse.py](../../.github/scripts/measure_dependency_reuse.py). It needs
Python 3.12+, Git, Node/npm and the repository .NET SDK, and it measures the committed `HEAD`:

```sh
python3 .github/scripts/measure_dependency_reuse.py \
  --candidate all --trials 3 --output /absolute/path/to/new-evidence-directory
python3 -m unittest discover -s tests -p test_nx_dependency_reuse.py -v
```

The output directory must not exist yet. Each condition gets a fresh `git archive` checkout with its
own empty dependency store; the cold run seeds the warm run's store. Failures are recorded as
failures, not as fast samples. These are local observations, not hosted cache benchmarks.

To judge a hosted cache, use exact-hit rate `h`, cold-minus-warm install time `D`, hit restore
overhead `R`, miss lookup overhead `L` and miss save overhead `S`:

```text
net seconds per eligible job = h * (D - R) - (1 - h) * (L + S)
break-even h > (L + S) / (D - R + L + S), provided D > R
```

Only add a cache when repeated hosted cold/warm jobs show a positive net saving beyond normal
variation. Don't weaken tests or change main verification to get there. GitHub evicts caches after
seven days without use and at the repository's 10 GB limit; PR-scoped caches aren't shared between
PRs, so default-branch caches drive the hit rate.
