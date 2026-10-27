# Developer documentation

For people extending Talewright in C# or working on the engine itself. Writing stories? See the
[author guide](../guide/README.md) instead.

| Page | What's in it |
|---|---|
| [Contributing](../../CONTRIBUTING.md) | Build, test, repository layout, picking up work |
| [Architecture](architecture.md) | Guided code tour, invariants, engine loop, extension points, "where to find X" |
| [C# extensions and packages](csharp-extensions.md) | Which NuGet package to use, registry setup, extension examples |
| [Distribution and runtime safety](../reference/safety.md) | Framework contracts, executable-extension boundaries and focused follow-up gaps |
| [Shared authoring analysis](authoring-analysis.md) | Partial symbols, source ranges, unsaved workspaces, and explicit project metadata |
| [Plan and roadmap](plan.md) | Decisions, milestones and their dependencies |
| [Decision records](adr) | The big design decisions in long form |
| [Issue tracking](issues.md) | Full claim/status workflow for people and agents |
| [Releasing and CI](releasing.md) | Workflows, GitHub Pages, version tags, package publication |
| [CLI setup details](cli-setup.md) | Download verification, system dependencies, .NET tools and source builds |
| [UI test container image](ui-test-image.md) | Build/publish process and CI image rollout for browser/Linux player tests |
| [Nx CI orchestration](nx-ci.md) | Affected selection, task graph, developer commands, and verification matrix |
| [Performance](performance.md) | Recorded benchmark results and how they were measured |
| [Performance scorecard](performance-scorecard.md) | Cross-host baseline, measured dimensions, limitations, and budget status |
| [Testing](../../tests/README.md) | Unit, browser, desktop and performance test suites |

Projects are grouped into engine, player, apps, tooling, packaging, and benchmarks.
See the [repository layout](../../CONTRIBUTING.md#repository-layout) and each
area's project READMEs for files and extension points.
