# Talewright

<img width="1408" height="768" alt="image_3ab69b7a" src="https://github.com/user-attachments/assets/7bb26ecc-f688-4b54-98b3-85eb6fa67bcc" />

**DISCLAIMER:** All art is AI generated for now. If this sees success, and it works well for me while I'm dogfooding it, I will work with a real artist to get something one of a kind that our AI overlords haven't ripped off of thousands of artists. This I solemnly swear


A C#/.NET engine for **stat-driven, massively branching, text-first RPG visual novels**: on-rails
tabletop-style stories now, open-world later. Runs on the desktop (Photino) and the web (Blazor WebAssembly).

Writers author stories in **`.tale`**, a small Markdown superset, plus a `story.yaml` that declares the characters,
stats, factions and items. No code is needed. Programmers can extend the engine in C#.

```tale
=== temple.entrance
{player:first-name#possessive} torch gutters as **cold air** spills from the door.

* [Climb the collapsed wall] skill-check athletics dc 14
  success: -> temple.upper
  failure: { player:hp -= 2 } -> temple.fall
* [Read the glyphs] (requires: player:int >= 12) -> temple.glyphs
```

## Status

**Milestone 1 (vertical slice) is complete:** the engine, the `.tale` compiler, the shared Blazor player, and the
desktop and web hosts all work, and the sample story *The Lost Relic* is playable end to end on both.
**M2 implementation** adds manual named saves with conservative compatibility checks, deterministic
Back/Forward history, `validate` and story-only/complete-game `publish`, explicit C# project
integration, all/any bounded hubs, and opt-in character creation. Save migration, autosave and
editor tooling are not included. Release readiness is tracked separately in
[#33](https://github.com/squid-farm/talewright/issues/33); local implementation is not a registry release.
M3 adds `lint` and editor-neutral language-server support with a thin VS Code extension for Tale highlighting,
snippets, live validation, completion, and hover help, delivered in independently usable slices.
Later milestones preserve the feature backlog: **M4** image/media support, **M5** world simulation,
**M6** combat, **M7** runtime plugins, **M8** save migration, and **M9** safe `lint --fix` (lowest priority).
Dependencies, not milestone numbers alone, determine when independent work can start; none blocks M2/M3.
CLI `validate` and `publish` are available; editor tools remain planned. They share the compiler rather
than duplicate its analysis. See [docs/PLAN.md](docs/PLAN.md) for scope and dependencies.

## Performance snapshot

The initial Release benchmark compiles **10,000 generated scenes** with substitutions,
effects and choices, and restores **10,000-choice histories**:

| Operation | Median | Observed p95 |
|---|---:|---:|
| Compile 10,000 scenes | 138 ms | 145 ms |
| Load in-memory story ZIP + compile | 135 ms | 149 ms |
| Restore 10,000 choices | 106 ms | 115 ms |
| Back + Forward at 10,000 choices | 112 ms | 172 ms |
| Serialize a 10,000-choice save | 0.23 ms | 0.24 ms |

Published, trimmed WebAssembly player tests also loaded a generated 10,000-scene
story and reached its first playable passage in **1.44 s in Chromium, 1.86 s in
Firefox and 1.33 s in Playwright WebKit** (one integration measurement per browser,
including archive generation/upload, compilation and rendering; not p95 values).

These are recorded local measurements on **Linux Mint 22.3 x64 / .NET 10.0.13**,
not universal performance guarantees. The .NET table uses warmed execution with
tiered compilation disabled, seven samples per operation, and nearest-rank p95
(the largest sample). The synthetic story has one Tale file of about 1.24 MB;
these numbers do **not** measure cold process/disk startup, complex real-world
stories or maximum file/byte limits. Compile and restore allocated approximately
118 MiB and 81 MiB respectively; those are managed allocations, not peak memory.
Compilation p95 reached 878 ms during a separate run under concurrent build load.

See [methodology, reproduction commands and recorded results](tests/README.md#reproducible-performance-budgets).
The broader **10 / 100 / 500 / 1,000 real-file BenchmarkDotNet suite** remains
tracked in [#57](https://github.com/squid-farm/talewright/issues/57); it has not yet
produced results.

## Work tracking

[GitHub Issues](https://github.com/squid-farm/talewright/issues) is the live backlog.
Use the [Development Project](https://github.com/orgs/squid-farm/projects/1) for status/priority views and
[milestones](https://github.com/squid-farm/talewright/milestones) for release targets.
Before starting work, read the issue's blockers and parent, check existing claims/PRs, and claim it with an
assignee and branch/worktree comment. See [the migration index and workflow](docs/ISSUES.md) for bot coordination.

## Layout

| Path | What |
|---|---|
| `src/Talewright` | Packaging shell bundling the six headless project assemblies into one NuGet package |
| `src/Talewright.Core` | State, events, ruleset contracts, and deterministic randomness |
| `src/Talewright.Rules` | Conditions, effects, checks, triggers, and storylets |
| `src/Talewright.Narrative` | Templates, grammar, and presentation-independent documents |
| `src/Talewright.Rulesets.Generic` | Default d20 ruleset |
| `src/Talewright.Tale` | Story compiler and diagnostics |
| `src/Talewright.Runtime` | Story sessions and frames |
| `src/Talewright.Cli` | `talewright validate` and story-only/standalone `publish` tool |
| `src/Talewright.Player` | Blazor UI (Razor Class Library) |
| `src/Talewright.Host.Desktop` / `.Host.Web` | Photino desktop host / Blazor WASM host |
| `samples/LostRelic` | Sample story (not part of the engine) |

## Build and run

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test

# Desktop: native window; bundled samples plus any story folders/zips you pass
dotnet run --project src/Talewright.Host.Desktop -- samples/LostRelic

# Web: dev server (prints a URL)
dotnet run --project src/Talewright.Host.Web

# Web: static build you can put on any file host
dotnet publish src/Talewright.Host.Web -c Release -o out/web
python3 -m http.server 8080 --directory out/web/wwwroot
```

On Linux the desktop host needs WebKitGTK (`libwebkit2gtk-4.1`). Each project has its own README with details.

## Authoring a story

No C# project or NuGet dependency is needed to write a story. Copy a story from
[samples](samples/README.md), edit its `.tale` files and `story.yaml`, and open the
folder in the desktop player or upload its ZIP in the web player. The published
[sample player](https://squid-farm.github.io/talewright/) includes all bundled samples.
Validate from source with `dotnet run --project src/Talewright.Cli -- validate samples/LostRelic`.
See the [CLI reference](src/Talewright.Cli/README.md) for JSON output and exit policy.
Use `talewright publish story-folder --output story.zip` for a portable story archive, or add
`-a --target web|desktop` for a complete deployable game. The VS Code authoring extension remains roadmap work.
Web output is a static site that also works beneath a subpath. Desktop output defaults to one ZIP
with self-contained Windows/Linux/macOS x64 and ARM64 builds plus launchers that select the right one;
players do not need .NET, but do need their OS webview. `--runtime` selects a smaller single-platform ZIP.
This is not a universal native executable or a signed/notarized installer.
For custom C# registrations and build-time Tale errors, see the package-based
[C# story example](examples/CSharpStory/README.md).

## C# customization packages

Packages are for C# extensions and custom applications, not ordinary story authors:

| Package | Use |
|---|---|
| `Talewright` | Complete headless engine, compiler, default ruleset, and runtime; no Blazor dependency |
| `Talewright.Player` | Supplied Blazor UI; automatically depends on `Talewright` |

Install **only `Talewright.Player`** for the usual C# customization experience.
Install `Talewright` alone for tooling or a replacement frontend. Both packages
share one release version. Hosts, sample stories, and test helpers are not packages.
`Talewright.Cli` is a separate .NET tool package, not a library dependency.
The Player package is a UI library, not a runnable desktop/web application; a
custom application still supplies its host and story library.

GitHub Packages is the registry, **not nuget.org**. Even public GitHub packages
require consumer authentication. Create a classic PAT with `read:packages` and
appropriate access to the packages (including organization SSO authorization if
required). Configure the feed once:

```bash
dotnet nuget add source https://nuget.pkg.github.com/squid-farm/index.json --name talewright

# Set GITHUB_PACKAGES_TOKEN securely outside the repository.
# This POSIX-shell example supplies credentials through the environment, not a checked-in config.
export NuGetPackageSourceCredentials_talewright="Username=YOUR_GITHUB_USER;Password=$GITHUB_PACKAGES_TOKEN"

dotnet add package Talewright.Player --version VERSION
# Engine-only alternative:
dotnet add package Talewright --version VERSION
```

Replace `VERSION` with a published version; prereleases must be requested explicitly.
The six headless projects retain their original source layout, namespaces, and
assembly identities. `Talewright` is a packaging shell that ships those assemblies
together; they are not separate NuGet products. Player retains its direct Runtime
project reference for compilation and depends on the shell package for distribution.

## GitHub Actions and releases

The Build workflow runs on PRs targeting `main`, pushes to `main`, version-tag
pushes, and manual requests. It builds Release and verifies both libraries and the CLI tool with
clean, isolated consumers. Its reusable Tests workflow runs unit tests, Chromium,
Firefox, WebKit, and actual Linux Photino tests. Both must pass before publishing:

- **Main pushes:** Publish Samples builds the production Blazor host, verifies a
  sample opens and a choice works at the configured Pages base path, then deploys
  its static files to GitHub Pages.
- **Version-tag pushes:** Publish NuGet uploads the already-verified package
  artifact to GitHub Packages using the run's `GITHUB_TOKEN`.
- **PRs/manual runs:** validation only, never publishing.

For repository administrators:

1. Set **Settings > Pages > Source** to **GitHub Actions** and allow `main` in the
   `github-pages` environment's deployment rules. Configure a custom domain in
   Pages settings, if needed; the workflow obtains the base path from Pages.
2. Allow the publishing jobs their scoped Pages/OIDC and package-write permissions.
   No PAT publishing secret is required. Check package visibility and inherited
   repository/Actions access after first publication; publishing alone is not a
   substitute for the desired public/private access configuration.
3. Update branch-protection required checks if they referred to the old standalone
   Tests workflow. Build now orchestrates reusable tests.

Release a validated commit with an immutable tag:

```bash
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

Supported tags are `vMAJOR.MINOR.PATCH` with optional SemVer prerelease identifiers,
such as `v0.1.0-preview.1`. Leading zeros in numeric identifiers and build metadata
(`+...`) are rejected. The tag supplies the version for both packages. Ordinary CI
uses a non-published `0.0.0-ci.RUN_NUMBER` version.

Package publication is not atomic: the engine is pushed before the Player. Rerun
the failed Build run after a transient failure; NuGet push skips existing versions
and continues with missing ones, without overwriting or comparing existing package
contents. **Never move/reuse a release tag for different code.** Use a new version
for corrections. Authentication, network, validation, and other non-duplicate
failures remain failures. Version-tag releases are not cancelled by newer CI runs.

## Player regression tests

```bash
python3 tests/run_e2e.py install-browsers
python3 tests/run_e2e.py web       # Chromium, Firefox, WebKit; real Release-published web host
python3 tests/run_e2e.py linux     # real Photino / installed WebKitGTK, through AT-SPI
```

Both targets use one C#/xUnit E2E project with shared, programmatically defined stories rather than
sample-specific playthroughs. Playwright drives browsers; a thin AT-SPI adapter drives Linux Photino.
UI tests are opt-in; ordinary `dotnet test` still runs the fast .NET suite without browsers.
See [tests/README.md](tests/README.md) for Linux prerequisites, targeted commands, coverage, artifacts,
and Windows/macOS desktop options.

## Docs

- [Plan and roadmap](docs/PLAN.md)
- [Architecture](docs/ARCHITECTURE.md): start here when reading the code; a guided tour with a "where to find X" table
- Per-project READMEs in each `src/*` folder, plus [samples/README.md](samples/README.md)
- [`.tale` syntax](docs/TALE-SYNTAX.md)
- [Architecture decision records](docs/adr/)
- [Issues](docs/ISSUES.md): bugs and feature requests/shortcomings, ranked

## License

[MPL-2.0](LICENSE). Use it for anything, including commercial games. Changes to Talewright's own source files must stay open.
