# Nx CI orchestration

Nx provides affected-project selection and task ordering. It does not replace MSBuild, NuGet, npm,
the existing test runners, or GitHub Actions environment setup. The official `@nx/dotnet` 24.0.0
plugin infers the .NET project graph from the repository's `.csproj` files and MSBuild references.
Non-.NET contracts are declared in their nearest `project.json`.

All targets are uncached by default. In particular, browser/native E2E, packaging checks, and
editor tests run their existing commands and are not restored from Nx cache.

## Local setup and selection

Use Node 22/npm and the SDK from `global.json`. The checked-in `./nx` wrapper installs the pinned
Nx/plugin versions declared in `nx.json` into `.nx/installation`; no root npm package is needed:

```sh
./nx show projects --json
python3 -m unittest discover -s tests -p 'test_nx*.py' -v
python3 -m unittest discover -s docs -p test_docs.py -v
```

Wrapper installation diagnostics go to stderr so machine-readable Nx stdout remains valid JSON,
including on clean CI runners. The cold-install regression test exercises this first-use path.
Both launchers quote the wrapper path, including when invoked by absolute path from a checkout
whose directory name contains spaces.

Preview the PR task graph using the actual base and head revisions:

```sh
python3 .github/scripts/nx_ci.py select --base=origin/main --head=HEAD
cat artifacts/ci/plan.json
```

Selection invokes `nx affected --dry-run --graph=stdout`. The small CI adapter groups actual graph
tasks by required execution environment, not by changed paths. Nx still executes each selected target
and its prerequisites. Independent verification tasks run with `--parallel=3`; target phases are ordered to avoid
packaging/editor and native report-output races. `editor-smoke` also explicitly depends on packaging,
and native regressions depend on the accessibility probe.
Editor compiler contracts share the `Talewright.Talemark.Tests` project's Release build prerequisite and execute with
`--no-build --no-restore`, preventing concurrent MSBuild writes when both contracts are selected.
.NET build targets use `parallelism: false`: Razor static-web-assets targets can revisit referenced
projects even with `--no-dependencies`, so concurrent host builds can write the same intermediate cache.
Nx still orders prerequisites and runs independent non-build verification concurrently.

Selection also requests inferred `build:release` targets. Builds outside every selected verification
project's transitive dependency graph run in `checks`; this compiles leaf projects such as
`Talewright.BenchmarkDotNet` and newly inferred libraries instead of silently accepting an empty plan.
Projects already covered by test/package contracts retain those contracts rather than duplicate builds.

## Project and target matrix

| Project / change area | Selected verification |
|---|---|
| .NET projects under `engine/`, `player/`, `apps/`, `tooling/`, `packaging/`, `benchmarks/`, and `examples/` | `@nx/dotnet` infers `build`, `test`, and packaging/run targets. MSBuild `ProjectReference` edges propagate affected selection and order prerequisite builds. |
| `engine/tests/Talewright.Talemark.Tests` | `test` excludes the editor snippet compiler contract; that contract is separately selectable. |
| `talewright-snippets` | `compiler-contract` compiles the shipped snippet defaults against the Talemark compiler. The test source is a separate graph node so compiler changes select this contract without treating all compiler tests as editor assets. |
| `talewright-editor` | `test`, `package-check`, and `editor-smoke` wrap the existing extension checks and package/installed-editor commands. `compiler-contract` runs the manifest-schema compiler fixtures separately from compiler units. Explicit dependencies on the compiler, `Talewright.Talemark.Tests` project and example Story project preserve fixture verification. Target-specific `documentedManifests` inputs cover the four docs consumed by schema tests without selecting package/installed-editor checks for doc edits. |
| `Talewright.Player.E2E.Tests` | `e2e-web` preserves the three-browser matrix; `e2e-linux` and `e2e-linux-probe` retain the native runner. Explicit non-code dependencies cover CLI, debugger, player, hosts, and runtime contracts. Published web and desktop targets retain their existing publish-and-smoke commands. |
| `talewright-packaging` | `package-check` wraps the existing versioned solution build, three pack commands, and clean-consumer verifier. Supply `VERSION`. Explicit dependencies include the engine/player packages, CLI, both source hosts copied into publishing assets, and all four CSharpStory projects consumed by the installed-package verifier. This separate contract does not change ordinary CLI build prerequisites. |
| `talewright-docs` | `check` validates relative Markdown links for changes under `docs/`. General docs edits select only this check. The editor's schema inputs additionally select Node tests and the compiler contract for `reference/talemark-syntax.md`, `guide/first-story.md`, `guide/debugger-test-plan.md` and `guide/writing-stories.md`, whose YAML examples are consumed by `story-schema.test.mjs`. These edits retain compiler build prerequisites but do not select editor packaging/smoke or unrelated product suites. |
| `talewright-samples` (`samples/`) | Story assets belong to a content project. Both source hosts depend on it because they embed the bundled stories; affected hosts propagate to package verification and all regular/published browser and Linux E2E targets, not unrelated unit/editor suites. |
| `talewright-e2e-runner` (`player/tests/`) | Owns `run_e2e.py` and `native/` helpers outside the more-specific E2E test project. The E2E test project depends on it, selecting browser/Linux regression and published-game targets without root fallback. |
| `talewright-example-settings` (`examples/CSharpStory/`) | Owns the example-local `Directory.Build.props`. All four example projects depend on it, preserving compilation, package-consumer checks and the editor's Story-fixture contracts. Their more-specific roots still own ordinary example source edits. |
| `talewright-performance-tooling` (`benchmarks/performance/`) | `check` runs the performance-report Python unit tests in `checks`. Script edits do not select exhaustive performance workloads or product suites. Both full verification and unknown-path fallback include this check. |
| `talewright-ci-infrastructure` (`.github/`) | Owns CI files outside more-specific projects, avoiding root fallback. Workspace-file inputs select `talewright-ci-tests:ci-check` for infrastructure edits and additional consumers according to the matrix below; this ownership node has no runnable targets. |
| No-impact repository metadata | `.nxignore` excludes `.vscode/`, agent guidance under `.github/{skills,agents,prompts,instructions}/`, `.github/copilot-instructions.md`, root `AGENTS.md`, and the explicitly listed repository README/contribution/license/ownership/ignore files. PRs changing only these files select no work targets, but still run selection and aggregate verification. Product docs and unknown paths are not blanket-excluded. |
| `talewright-workspace` fallback | Unowned/root and newly introduced paths select broad verification rather than silently producing no tasks. No-op targets depend on the real projects, so broad selection uses the same commands without running duplicate solution tests. `docs/` is a more-specific project and receives documentation checks. |

Shared inputs include the SDK, build props, solution, NuGet configuration, and Nx configuration.
Those changes conservatively affect verification targets. `.github/` files are no longer blanket
shared inputs. Nx filters the explicit no-impact paths in `.nxignore` before affected computation.
Changing `.nxignore` itself retains conservative verification. The graph selection
tests exercise editor-only, snippet, rules/compiler, UI, runtime/save, CLI/publish, shared/unknown,
and documentation-only changes, and assert build prerequisite edges. Changes to shared test runners
under `player/tests/` belong to the runner project consumed by the E2E project. Repository CI contracts remain
under root `tests/` and select CI validation only, not product E2E.

## CI-infrastructure change matrix

All paths below are relative to `.github/`. Every row selects `ci-check`; the table lists additional
work. Native Nx inputs model these consumers, not a path classifier in the workflow router.

| Changed file(s) | Additional verification |
|---|---|
| `scripts/nx_ci.py`, `scripts/ui_test_image_plan.py` | None: CI/selection and image-planning contract tests validate them. |
| `scripts/package_version.py`, `scripts/verify_packages.py` | Complete `build` group: versioned packages and clean consumers. |
| `scripts/publish_samples.py` | Complete browser group, including regular and published-game tests in all three browsers. |
| `workflows/build.yml`, `workflows/publish-nuget.yml` | Complete `build` group. CI contract tests additionally exercise the aggregate/orchestration wiring. |
| `workflows/editor-tooling.yml` | Complete editor group, including both compiler contracts, packaging and installed-editor smoke. |
| `workflows/tests.yml` | All non-build groups: headless units, browser, Linux, editor and every checks target (including uncovered leaf compilation). |
| `workflows/publish-samples.yml` | Complete browser group. Deployment remains main-only. |
| `workflows/performance.yml` | Performance-report Python tests; exhaustive measurements remain scheduled/manual. |
| `workflows/publish-ui-test-image.yml`, `docker/ui-tests/Dockerfile`, `docker/ui-tests/image.json`, `docker/ui-tests/verify.py` | The independent UI-image workflow still validates candidate builds, browser story loading and the native prerequisite probe. |

Workflow edits run complete driven groups rather than a new smoke-only path. This exercises existing
runner/artifact/environment contracts without maintaining a parallel subset of test commands.
The narrower exceptions are selector/planner unit contracts, scheduled performance tooling and the
UI-image workflow's existing candidate validation. Local CI contracts do not prove GitHub deployment,
permissions or reusable-workflow execution; hosted workflow runs remain necessary release evidence.

`InfrastructureSelectionTests` audits every current workflow, script and image-definition file against
this matrix and asserts exact task lists, not just the presence of one expected task. Adding one of
these files requires extending the matrix/test; otherwise CI validation fails. Mixed source and
infrastructure edits retain both sets of verification. Genuinely shared inputs and unknown product
paths outside owned projects retain broad verification.

The `test` input override retains the pinned .NET plugin's dependency/toolchain/environment inputs
and adds the Tests workflow. Root fallback noop targets explicitly use root inputs so these targeted
workflow inputs cannot accidentally expand through the full-workspace fallback.

## CI jobs and coverage

The Build workflow always starts selection: no whole-workflow path filters are used. PRs use the
event's exact base/head SHAs with `fetch-depth: 0`; all jobs check out that same head. The selection
manifest is uploaded once and consumed by the jobs, so there is no parallel workflow path classifier.

| Job | PR coverage when selected |
|---|---|
| `build` | Existing Release solution build, NuGet packs, clean library consumers, installed CLI and publication checks. |
| `tests / unit` | Every test in each affected headless unit project, in Release. The normal `Talewright.Talemark.Tests` suite excludes the editor/compiler contract. One nonempty, passing TRX report is required per selected project; reports are isolated under each area-local test project's `artifacts/unit`. |
| `tests / browser` | Complete regular player regression suite in **all three** browsers: Chromium, Firefox, WebKit. Also runs the focused `Artifact=Standalone` published-game smoke suite under a nested static path. |
| `tests / linux-desktop` | Accessibility/chooser probe, complete regular native regression suite, then focused published-desktop smoke. Regression and standalone reports have separate names. |
| `tests / editor / syntax` | Applicable Node syntax/snippet tests, focused snippet compiler contract, VSIX packaging/content check, and installed-editor smoke. Compiler-only changes need not run all editor checks. |
| `tests / checks` | Applicable CI/selection regression tests, documentation-link checks, performance-report Python tests, and bounded performance budgets. |

“Regular” browser/native coverage excludes `RealCorpus`, just as before this change. PR E2E is not
reduced to a tiny smoke subset when core changes are affected: the full regular regression matrix is
intentional. Gigantic/real-corpus measurements remain in the separate performance workflow with its
existing longer timeouts and schedule.

Both reusable Tests verification and the Build **CI verification** aggregate run with `if: always()`.
They require successful selection, success for every selected job, and `skipped` only for explicitly
unselected jobs. Failures, cancellations, missing results, unexpected execution/skips, and selection
errors fail closed. Browser matrix fail-fast remains disabled, so every browser produces evidence.
NuGet/sample publication additionally requires the aggregate.

For a no-impact PR, `build` and all Tests work jobs skip. The reusable Tests workflow still runs
its planning and verification jobs and returns success; the top-level **CI verification** check
therefore succeeds rather than being skipped or left pending. Mixed metadata/source changes retain
the source change's affected tasks. Full-verification events do not use these exclusions to select
projects: they still run every regular verification target.

Browser and Linux desktop jobs use the [prebuilt UI environment](ui-test-image.md) instead of
installing SDK/browser/native dependencies per job. They keep the same selection manifest and
`nx_ci.py run` entry points, including the native probe prerequisite and published-artifact targets.
Node 22/npm come from the image; non-root execution requires writable checkout, `.nx/installation`,
npm home, and NuGet home directories. Image files select CI contracts and retain the independent
candidate-image validation described above.

The repository ruleset was inspected during implementation: its required-status-check list was empty.
This change preserves `build`, `unit`, `browser`, `linux-desktop`, and editor `syntax` job IDs, but editor
now runs as a reusable job inside Tests, so its displayed check context changes. After a first PR run
confirms the emitted contexts, maintainers should add **CI verification** to the ruleset's required
status checks rather than requiring `build`, conditional Tests leaf checks, or the nested
`tests / verification` check. Keep that top-level name stable. No repository protection settings
are changed by this PR.

## Full verification

Pushes to `main`, release tags, Build manual dispatches, and the weekly Build schedule always use
`run-many`, bypassing affected selection. Standalone Tests manual dispatches also generate a full
verification plan. A release still uses the existing publication approvals and package version logic.

To reproduce the full regular suite locally, retain the same browser/native prerequisites as CI:

```sh
python3 .github/scripts/nx_ci.py select --full
python3 .github/scripts/nx_ci.py run unit
python3 .github/scripts/nx_ci.py run checks
npm ci --prefix tooling/vscode-talewright
python3 .github/scripts/nx_ci.py run editor
for browser in chromium firefox webkit; do
  python3 player/tests/run_e2e.py install-browsers --browser "$browser"
  python3 .github/scripts/nx_ci.py run browser --browser "$browser"
done
python3 .github/scripts/nx_ci.py run linux-desktop
VERSION=0.0.0-ci.123 python3 .github/scripts/nx_ci.py run build
```

Run groups only when their manifest entries are nonempty. The adapter intentionally rejects an empty
group instead of silently reporting successful verification. Do not use inferred `test` on the opt-in
E2E project as a substitute for the host-aware runners.

## Regression evidence

`tests/test_nx_selection.py` validates the inferred graph against MSBuild references and explicit
contracts, affected selections, Release prerequisites, browser configurations, and disabled caching.
`tests/test_nx_ci.py` checks environment routing, full/fallback equivalence, invalid Git revisions,
and success/failure/cancellation/skip aggregation. It also executes an isolated real Nx workspace:
a prerequisite must finish before its consumer; forcing that prerequisite to exit nonzero must return
failure without executing the consumer. These tests need the pinned Nx installation, not UI libraries.

Shared workflow ownership was cleared with the M2 CI session before wiring these jobs. Container/image
provisioning is deliberately separate: any future image reference belongs in shared inputs, and
browser/native caching must remain disabled without independently proven input/output correctness.
