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
Core reliability and release verification come first in M2, alongside saves with compatibility safeguards,
a CLI with `validate` and `publish`, and character creation.
M3 adds `lint` and editor-neutral language-server support with a thin VS Code extension for Tale highlighting,
snippets, live validation, completion, and hover help, delivered in independently usable slices.
Later milestones preserve the feature backlog: **M4** image/media support, **M5** world simulation,
**M6** combat, **M7** runtime plugins, **M8** save migration, and **M9** safe `lint --fix` (lowest priority).
Dependencies, not milestone numbers alone, determine when independent work can start; none blocks M2/M3.
These commands and editor tools are planned, not available yet; they will share the compiler's analysis
rather than duplicate it. See [docs/PLAN.md](docs/PLAN.md) for scope and dependencies.

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
The CLI and VS Code authoring extension are roadmap work, not prerequisites or
commands implemented by this change.

## C# customization packages

Packages are for C# extensions and custom applications, not ordinary story authors:

| Package | Use |
|---|---|
| `Talewright` | Complete headless engine, compiler, default ruleset, and runtime; no Blazor dependency |
| `Talewright.Player` | Supplied Blazor UI; automatically depends on `Talewright` |

Install **only `Talewright.Player`** for the usual C# customization experience.
Install `Talewright` alone for tooling or a replacement frontend. Both packages
share one release version. Hosts, sample stories, and test helpers are not packages.
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
pushes, and manual requests. It builds Release and verifies the two packages with
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

These suites use shared, programmatically defined stories rather than sample-specific playthroughs.
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
