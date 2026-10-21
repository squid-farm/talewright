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
Saves, a CLI with `validate` and `publish`, and character creation are next (M2).
M3 adds `lint` and editor-neutral language-server support with a thin VS Code extension for Tale highlighting,
snippets, live validation, completion, and hover help. Safe `lint --fix` support is lowest priority (M4).
These commands and editor tools are planned, not available yet; they will share the compiler's analysis
rather than duplicate it. See [docs/PLAN.md](docs/PLAN.md) for scope and dependencies.

## Layout

| Path | What |
|---|---|
| `src/Talewright.Core` | RPG engine: characters, stats, inventory, factions, event-sourced state, RNG |
| `src/Talewright.Rules` | Expressions, storylet selection, modifiers, triggers, skill checks |
| `src/Talewright.Narrative` | Template AST → presentation-agnostic document, grammar and pronouns |
| `src/Talewright.Rulesets.Generic` | Default d20-style attributes and skills (opt-out) |
| `src/Talewright.Tale` | `.tale` + `story.yaml` compiler with diagnostics |
| `src/Talewright.Runtime` | `IStorySession`, the loop that ties it all together |
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
