# Contributing to Talewright

Thanks for helping. This page gets you building and testing. For how the code fits together, start with
the [architecture tour](docs/developer/architecture.md).

**Before adding settings or syntax:** read the
[naming conventions](docs/developer/naming-conventions.md). This applies to human
contributors and coding agents; author-facing YAML keys use lowercase kebab-case,
not the casing of their C# implementation.

## Pre-v1 compatibility

Target the current syntax, APIs and file formats. Do not retain superseded properties, overloads,
serialized shapes or replay digests solely for backward compatibility. Update callers, fixtures and
documentation together; historical compatibility expectations do not override this policy.
Any exception requires an explicit project-lead decision.

When a persisted contract changes, version it and reject unsupported data explicitly rather than
migrating it. Failed loads must preserve the active session and original stored bytes. Keep
deterministic replay and valid current optional behavior; breaking old formats is not a goal by itself.

## Set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). On Linux the desktop
host and Author Debugger also need WebKitGTK 4.1 (`sudo apt install libwebkit2gtk-4.1-0`).
SDK 10 is a build requirement, not the minimum runtime for a headless engine consumer.
The [older-runtime compatibility spike](docs/developer/csharp-extensions.md#older-runtime-compatibility-spike)
describes the .NET 8, 9 and 10 engine targets, scope, and verification.

```bash
dotnet build
dotnet test                                                       # fast .NET suite, no browsers

dotnet run --project apps/desktop -- samples/LostRelic   # desktop player
dotnet run --project apps/web                            # web player dev server
dotnet run --project tooling/cli/src/Talewright.Cli -- validate samples/LostRelic   # CLI
```

The normal test command runs on .NET 10. To compile the five engine test projects and their
shared fixtures for the oldest runtime, without retargeting the hosts or tooling:

```bash
dotnet build Talewright.slnx -c Release -p:TalewrightTestTargetFrameworks=net8.0
dotnet vstest engine/tests/*.Tests/bin/Release/net8.0/*.Tests.dll
```

Install the .NET 8 runtime for this check; SDK 10 alone does not include it.
CI runs this check after the normal unit suite.
`TalewrightTestTargetFrameworks` can also select another engine test target, such as `net9.0`.
Running those tests requires the selected runtime, not just SDK 10.

## Player UI tests (opt-in)

```bash
python3 player/tests/run_e2e.py install-browsers
python3 player/tests/run_e2e.py web       # Chromium, Firefox, WebKit against the Release-published web host
python3 player/tests/run_e2e.py linux     # real Photino window through AT-SPI
```

Tests use small stories defined in C#, not the samples. See [tests/README.md](tests/README.md) for
prerequisites, targeted runs, performance budgets and other platforms.

PR checks run affected projects. To run every regular CI check on a branch without deploying, use
the [full branch verification workflow](docs/developer/nx-ci.md#run-the-full-pipeline-on-a-branch-without-deploying).

## Repository layout

| Path | What |
|---|---|
| `engine/src/Talewright.Core` | State, events, ruleset contracts, deterministic randomness |
| `engine/src/Talewright.Graphs` | Directed graphs and reachability for story analysis |
| `engine/src/Talewright.Rules` | Conditions, effects, checks, triggers, storylets |
| `engine/src/Talewright.Narrative` | Templates, grammar, presentation-independent documents |
| `engine/src/Talewright.Rulesets.Generic` | Default d20 ruleset |
| `engine/src/Talewright.Talemark` | Talemark and `story.talewright.yaml` compiler and diagnostics |
| `engine/src/Talewright.Runtime` | Story sessions, history, saves, author sessions |
| `packaging/Talewright` | Packaging shell: ships the seven engine projects above as one NuGet package |
| `player/src/Talewright.Player` | Shared Blazor UI (Razor Class Library) |
| `apps/desktop` / `apps/web` | Photino desktop host / Blazor WebAssembly host |
| `tooling/authoring/` | Editor-neutral workspace and project metadata infrastructure |
| `tooling/cli/` / `tooling/debugger/` | `talewright` tool and the native Author Debugger |
| `tooling/vscode-talewright/` | VS Code extension, syntax, snippets, and manifest schema |
| `engine/tests/`, `player/tests/`, `tooling/*/tests/` | Tests colocated with the area they exercise; shared story fixtures live in `engine/tests/Talewright.TestStories` |
| `benchmarks/` | Engine budgets, BenchmarkDotNet workloads, and publication measurements |
| `tests/` | Repository-wide CI and Nx contract tests; not product test suites |
| `samples/` | Example stories (never referenced by engine code) |
| `examples/CSharpStory` | Package-based C# extension example |

The root solution groups projects by the same responsibilities. Project names,
namespaces, assembly identities, and published package IDs are independent of
these source paths. Directory placement communicates ownership; project references
and package checks enforce what actually ships. Engine code does not depend on
tooling. Shared build settings and Nx orchestration remain at the repository root.

## Picking up work

[GitHub Issues](https://github.com/squid-farm/talewright/issues) is the backlog and the
[project board](https://github.com/orgs/squid-farm/projects/1) tracks status and priority.

1. Check the issue's blockers, parent, assignees and linked PRs.
2. Claim it: assign yourself and comment with your branch/worktree.
3. Work on a branch, ideally in its own `git worktree`.
4. Open a PR with `Closes #N` (or `Refs #N` for partial work) and test evidence.

Agents and multi-worker sessions: follow the full [issue workflow](docs/developer/issues.md).

## Docs

Author-facing docs (`README.md`, `docs/guide`, `docs/reference`) are written for non-programmers:
task first, short paragraphs, examples before rules. Put implementation detail in `docs/developer` or
the project READMEs. When behavior changes, update the [Talemark syntax reference](docs/reference/talemark-syntax.md)
and [error codes](docs/reference/diagnostics.md) in the same PR.

## Language changes

Before proposing or implementing Talemark syntax, read the
[operator registry](docs/reference/talemark-operators.md) and complete the
[language-change checklist](docs/developer/language-changes.md). The checklist covers alternatives,
diagnostics, and the parser, schema, editor and documentation changes that must ship together.
