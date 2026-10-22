# Contributing to Talewright

Thanks for helping. This page gets you building and testing. For how the code fits together, start with
the [architecture tour](docs/developer/architecture.md).

## Set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). On Linux the desktop
host and Author Debugger also need WebKitGTK 4.1 (`sudo apt install libwebkit2gtk-4.1-0`).

```bash
dotnet build
dotnet test                                                       # fast .NET suite, no browsers

dotnet run --project src/Talewright.Host.Desktop -- samples/LostRelic   # desktop player
dotnet run --project src/Talewright.Host.Web                            # web player dev server
dotnet run --project src/Talewright.Cli -- validate samples/LostRelic   # CLI
```

## Player UI tests (opt-in)

```bash
python3 tests/run_e2e.py install-browsers
python3 tests/run_e2e.py web       # Chromium, Firefox, WebKit against the Release-published web host
python3 tests/run_e2e.py linux     # real Photino window through AT-SPI
```

Tests use small stories defined in C#, not the samples. See [tests/README.md](tests/README.md) for
prerequisites, targeted runs, performance budgets and other platforms.

## Repository layout

| Path | What |
|---|---|
| `src/Talewright.Core` | State, events, ruleset contracts, deterministic randomness |
| `src/Talewright.Rules` | Conditions, effects, checks, triggers, storylets |
| `src/Talewright.Narrative` | Templates, grammar, presentation-independent documents |
| `src/Talewright.Rulesets.Generic` | Default d20 ruleset |
| `src/Talewright.Tale` | `.tale` / `story.yaml` compiler and diagnostics |
| `src/Talewright.Runtime` | Story sessions, history, saves, author sessions |
| `src/Talewright` | Packaging shell: ships the six projects above as one NuGet package |
| `src/Talewright.Player` | Shared Blazor UI (Razor Class Library) |
| `src/Talewright.Host.Desktop` / `.Host.Web` | Photino desktop host / Blazor WebAssembly host |
| `src/Talewright.Cli` / `.Debugger` | `talewright` tool and the native Author Debugger |
| `samples/` | Example stories (never referenced by engine code) |
| `examples/CSharpStory` | Package-based C# extension example |

Each project has a short README describing its files and extension points.

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
the project READMEs. When behavior changes, update the [Tale syntax reference](docs/reference/tale-syntax.md)
and [error codes](docs/reference/diagnostics.md) in the same PR.
