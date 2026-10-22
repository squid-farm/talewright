# Talewright — Implementation Plan

> A C#/.NET engine for stat-driven, massively branching, text-first RPG visual novels.
> Desktop (Photino) + Web (Blazor WASM). On rails first, open world later.
> License: MPL-2.0. Story files: `.tale` (Markdown superset).

This is the living roadmap, not the live task board. Track requirements, status, blockers, and implementation
in [GitHub Issues](https://github.com/squid-farm/talewright/issues) and the
[Development Project](https://github.com/orgs/squid-farm/projects/1). Section 7 preserves completed M0/M1
history; new work belongs in GitHub. See [issue tracking](issues.md) for legacy IDs and coordination.

Distribution now uses two packages without changing the project layout:
`Talewright` is a packaging shell containing the six existing headless assemblies;
`Talewright.Player` provides the optional UI and depends on that package.
Component projects remain separate and are not individually published. NuGet is
for C# customization; ordinary story authors do not need project dependencies.
CLI/VS Code tooling and plugin loading remain roadmap work.

---

## 1. Problem statement

Write a reusable engine for tabletop-style text RPGs and visual novels in which:
- story content is chosen by **rules evaluated over rich, typed game state**: stats, levels, inventory, factions, relationships, history;
- authors write **simple text files** (no code needed), and can add **C#** when they need more power;
- the engine is **three-tiered and decoupled**: Presentation → Template Renderer (Narrative) → Rules → RPG Engine;
- the game ships to **desktop and web** (one-click play on itch.io), with room left for images and open-world play later.

## 2. Research findings (session 1)

### 2.1 Ink — your concern is confirmed
- Ink is strong for prose-first branching dialogue, but its state is a **flat set of global variables plus `LIST` flags**. It has no records, collections of characters, or typed entities.
- Stat-heavy logic ends up either inline (`{str > 12 && has_sword && rep_guild >= 3: …}`) all through the prose, or in C# through `EXTERNAL` functions. Ink then becomes a text formatter with a **second state store** that has to be kept in sync.
- Yarn Spinner has the same limitation.
- The model you described is **storylets / quality-based narrative** (Fallen London, StoryNexus): content units become eligible through predicates over world state. It fits on-rails play (narrow predicates plus explicit gotos) and open-world play (broad predicates plus hubs) without changing the core. **We adopt it.**

### 2.2 Existing engines — none fit; build custom
| Option | Why not |
|---|---|
| Ren'Py | Python, VN-focused, weak stat modeling |
| Twine / SugarCube / Harlowe | JavaScript, string-macro state |
| Naninovel / Fungus | Unity-bound |
| ink / Yarn Spinner | Dialogue layer only (see above) |
| GoRogue / RogueSharp | Roguelike grids/maps, not narrative |

Reusable pieces:
- **Markdig** (MIT, C#): extensible Markdown parser, the base for `.tale`.
- **YamlDotNet**: `story.yaml` and front matter.
- **Photino.Blazor** (v4.x): cross-platform desktop host.
- **NRules** was considered and **rejected for the core**. It compiles expression trees at runtime, which is slow under WASM interpretation and adds a heavy dependency. A small custom typed predicate evaluator is simpler and faster for storylet selection.
- **Scriban, Fluid and RazorLight** were rejected because they are string-template engines. We use a typed template AST instead.

### 2.3 Blazor + WASM — good fit
- Blazor WASM publishes as **pure static files**: GitHub Pages, Netlify, or an **itch.io HTML5 zip**. No server needed.
- Cost: a one-time first load of about 2–4 MB compressed (trimmed); later loads come from cache. AOT is optional and is a size-versus-speed trade-off. Not needed at first.
- Desktop host: **MAUI Blazor Hybrid does not support Linux** (the dev machine is Linux). **Photino.Blazor** supports Windows, macOS and Linux (it needs WebKitGTK on Linux).
- Shape: one **Razor Class Library** of UI shared by a Photino host and a WASM host.
- Target **.NET 10 (LTS)**; it is installed locally.

## 3. Decisions log (confirmed with user)

| # | Decision |
|---|---|
| D1 | .NET 10, C#, Blazor; Photino desktop + Blazor WASM web. **Desktop first.** |
| D2 | Name **Talewright**; namespaces/packages `Talewright.*` |
| D3 | License **MPL-2.0**. User acknowledged that commercial use is allowed; only modified engine files must stay open. |
| D4 | Authoring format: **`.tale`** is a Markdown superset (Markdig plus custom directives), with `story.yaml` for story configuration and schema. |
| D5 | Two authoring paths sharing **one compiler library**: (a) **no-code** story folder/zip loaded by the player app, compiled once at load; (b) **C# story project** compiled at build time, with errors as build diagnostics (M2). The runtime never parses text during play; it runs on a typed AST. |
| D6 | The template renderer outputs a **presentation-agnostic document model (AST)**, not HTML. It includes media placeholders for future images. |
| D7 | Conditions: **both** inline expressions and named C# predicates. Both compile to the same predicate tree and run in one evaluator, the rules engine. |
| D8 | Randomness is **optional**: configured per story (`randomness: off | checks-only | on`) and/or invoked only through a `skill-check` directive. **Seeded RNG**; roll results are recorded as events so replays are deterministic. |
| D9 | State model: **event-sourced**. State is a fold over events. This enables saves, rewind, debugging, and history-based conditions. |
| D10 | Combat is **out of scope** for early milestones (to be a future module). |
| D11 | **Predefined protagonist** in M1. The MVP story's player is female. **Pronoun/grammar system** is in scope. |
| D12 | The core ruleset is **generic, d20-style** (STR/DEX/CON/INT/WIS/CHA plus skills). It ships as a module that can be **disabled or replaced**. |
| D13 | Saves are **planned for M2**, including save-format/story version identifiers and explicit rejection of incompatible saves without modifying them. Version-to-version migration is a separate **M8** milestone with explicit migration contracts and old/new fixtures; it is not an M3 requirement. |
| D14 | The story is **never built into the engine**. Samples live in `samples/` as standalone story folders/projects. |
| D15 | Distribution: M1 points the player at a folder or zip. M2 adds a `talewright publish` command that **bakes a story into the player** to produce itch.io-ready web and desktop zips. |
| D16 | Repo: `git init`; commit `README.md`, `LICENSE`, `docs/PLAN.md`, `docs/ARCHITECTURE.md`, `docs/TALE-SYNTAX.md`, and `docs/adr/*`. |
| D17 | Authoring tools are **editor-neutral**: CLI, MSBuild, and a C# Language Server Protocol (LSP) server reuse shared Tale analysis and diagnostics. The VS Code extension is a thin client, not a second semantic implementation. |
| D18 | Phase authoring support: **M2** CLI validation; **M3** shared partial analysis, linting, and LSP/VS Code intelligence; **M9** safe autofixes, lowest priority. Validation checks correctness; lint adds configurable authoring/style advice; autofix never guesses story intent. |
| D19 | **Production readiness before breadth, not backlog removal.** M2 prioritizes core reliability; M3 focuses on authoring. Preserve previously milestone-scoped features as numbered milestones: M4 media, M5 world simulation, M6 combat, M7 runtime plugins, M8 save migration, M9 autofix. Supporting work is organized as epics with explicit dependencies and acceptance criteria. |

## 4. Architecture

### 4.1 Tiers and data flow
```
 ┌──────────────────────────────────────────────────────────────┐
 │ Presentation  (Talewright.Player RCL → Desktop / Web hosts)  │  renders RenderedFrame, sends PlayerCommand
 └───────────────▲───────────────────────────────┬──────────────┘
                 │ RenderedFrame                 │ PlayerCommand (choose, continue, …)
 ┌───────────────┴───────────────────────────────▼──────────────┐
 │ Runtime facade  IStorySession  (Talewright.Runtime)          │  orchestrates the loop below
 └──┬──────────────────────┬──────────────────────┬─────────────┘
    │                      │                      │
 ┌──▼───────────┐   ┌──────▼────────┐   ┌─────────▼────────────┐
 │ Narrative    │   │ Rules         │   │ RPG Engine (Core)    │
 │ template AST │◄──│ predicates,   │◄──│ entities, stats,     │
 │ → document   │   │ storylet      │   │ inventory, factions, │
 │ model        │   │ selection,    │   │ event store, state   │
 │              │   │ modifiers,    │   │ projection, RNG      │
 │              │   │ triggers,     │   │                      │
 │              │   │ skill checks  │   │                      │
 └──────────────┘   └───────────────┘   └──────────────────────┘
        ▲ compiled from
 ┌──────┴────────────────────────────────────┐
 │ Talewright.Tale compiler (.tale + yaml)   │  Markdig + YamlDotNet → typed StoryDefinition + diagnostics
 └───────────────────────────────────────────┘
```

**Loop:**
1. A `PlayerCommand` comes in.
2. The engine validates it and emits **events**.
3. The state projection updates.
4. The rules engine fires triggers, which can emit more events.
5. The rules engine selects the eligible content unit(s).
6. The narrative layer renders the template AST against a **read-only state view**.
7. A `RenderedFrame` (document plus available choices) goes to the presentation layer.

**Dependency rule:** Core ← Rules ← Narrative ← Runtime ← Player. The compiler depends on Core, Rules and Narrative to produce their typed models. **The presentation layer only depends on Runtime contracts.**

### 4.2 RPG Engine (`Talewright.Core`) — the heart
- **Entity model** (composition, not inheritance): `Entity { EntityId, Kind }` plus typed **components** (Stats, Inventory, Pronouns, Names, Allegiances, Tags, …). The player, NPCs, factions, items and locations are all entities.
- **Typed keys:** `StatKey<T>`, `EntityId`, `FactionId`, `ItemId`, and so on. These are strongly typed IDs. String names exist only at the authoring boundary.
- **Stat definitions** come from the **ruleset**: id, value type (int/decimal/bool/enum/text), min/max, default, display name, category, and an optional **derived** formula (a predicate/expression tree).
- **Ruleset modules:** `RulesetBuilder.Use(GenericRuleset).Remove(Generic.Strength).Add(MyStats.Charm)`. A dating sim drops STR/VIT and adds Charm/Affection. In YAML this is `ruleset: { base: generic, disable: [strength, vitality], add: [...] }`.
- **Levels/XP:** stats plus a level-up rule supplied by the ruleset, not hardcoded.
- **Inventory:** items are entities. Containment is a relation (owner → item, with quantity), which leaves room later for containers, equipment slots and shops.
- **Factions and relationships:** `Relationship(from, to, kind, value)`, with reputation tiers defined by the ruleset (hostile … allied). This covers both faction allegiance and NPC affection.
- **Event store:** an append-only `IReadOnlyList<GameEvent>`, with events as records (`StatChanged`, `ItemGranted`, `SceneEntered`, `ChoiceMade`, `DiceRolled`, `FlagSet`, …). State is a pure fold over them, with snapshots later for performance.
- **History queries:** `visited(scene)`, `chose(choice)` and `count(event…)` power conditions such as "has ever betrayed X".
- **RNG:** an `IRandomSource` with a seed held in state. Every roll emits a `DiceRolled` event, so replays read the recorded value and never re-roll.
- **Not a one-way state machine:** there is no hardcoded "next" pointer. Position is state (`current scene`, plus later `current location`); movement is an event. This leaves open-world hubs, locations, time and clocks as additions rather than rewrites.

### 4.3 Rules (`Talewright.Rules`)
- **Predicate/expression AST:** literals, `entity:path` references, arithmetic, comparisons, `and/or/not`, `has <item>`, `visited`/`chose`, reputation tiers, and **named predicates** (`@CanClimbWall`) registered from C#.
- **Evaluator:** pure functions over a read-only `IGameStateView`. No reflection, no runtime expression compilation (WASM-friendly).
- **Storylet selection:** content units carry `requires`, `priority` and `tags`. The selector returns eligible units. "Rails" means explicit diverts (`-> scene`); "hubs" means offering every eligible storylet. Both come in M1 at basic level.
- **Modifiers:** derived and temporary stat adjustments (an equipped item gives +2).
- **Triggers:** reactive rules (`when faction:guild < hostile → emit FactionTurnedHostile`), bounded to prevent infinite cascades.
- **Skill checks:** `d20 + modifier vs DC` by default, defined by the ruleset and replaceable. Outcomes (success, failure, later crit or partial) map to branches.

### 4.4 Narrative / Template renderer (`Talewright.Narrative`)
- **Template AST (input):** `Text`, `Emphasis/Strong`, `Paragraph`, `SpeakerLine`, `Substitution(path, modifiers[])`, `Conditional(predicate, then, else)`, `MediaRef(kind, id)`, `Choice(...)`.
- **Document model (output):** a presentation-agnostic `RenderedDocument` of blocks and inlines with **all substitutions resolved**. Media refs pass through as `MediaPlaceholder` nodes that M1 ignores, reserving room for images and sprites.
- **Substitutions:** `{player:first-name#possessive}` → "Mara's", `{player:last-name#possessive}` → "Burns's" (Chicago-correct by default; see Risks/notes).
- **Modifier pipeline:** `#possessive`, `#capitalize`, `#upper`, `#lower`, `#plural`, `#a` (a/an). Modifiers can be chained and new ones can be registered.
- **Pronouns:** `{player:pronoun.subject}` → she; `.object` → her; `.possessive` → her; `.possessive-pronoun` → hers; `.reflexive` → herself. Built-in she/he/they sets plus custom sets. Verb agreement helper is a stretch goal (`{player:pronoun.subject} {player:verb.is}`).
- **Grammar provider seam:** the `IGrammar` interface ships with English first, which keeps localization possible later.

### 4.5 `.tale` compiler (`Talewright.Tale`)
- Markdig pipeline plus custom extensions, YAML front matter and `story.yaml`.
- **Validation against the schema:** every path and name, stat type and comparison type, every divert target and every item/faction id is checked. Unreachable or dead-end scenes produce warnings.
- **Diagnostics** carry file, line, column, code and message. The same diagnostics will feed the CLI, MSBuild and the VS Code language server (M3).
- Output: an immutable typed `StoryDefinition`, which can be serialized to a compact bundle for publishing later.

#### Authoring tooling (M2 implemented; M3 and M9 planned)

The compiler already reports syntax, reference/type, YAML, and structural diagnostics. Authoring tools
will reuse that work rather than build a separate parser or duplicate checks in each front end:

```text
Shared Tale compiler / analysis / lint rules
  +-- talewright validate
  +-- talewright lint [--fix]
  +-- MSBuild diagnostics for C# story projects
  +-- C# LSP server (proposed CLI host: talewright lsp)
        +-- Thin VS Code extension
        +-- Other LSP-capable editors
```

`validate` and explicit C# project/MSBuild integration are available. The lint/editor
commands below remain planned:

- **`talewright validate` (M2, implemented):** compile/check a story root and report all compiler diagnostics, including
  warnings. Provide human-readable `file:line:column` output and machine-readable JSON with stable codes
  and severities. Return success with warnings by default, fail on validation errors, and offer an explicit
  warnings-as-errors policy. Invalid arguments and I/O failures must be visible and fail the command.
  This is static validation, not proof that every conditional path is playable.
- **`talewright lint` (M3):** include compiler diagnostics plus configurable authoring/style rules with stable
  rule IDs, severities, configuration, and explicit suppression. Do not reimplement existing compiler checks.
  Begin with a bounded, documented Tale-aware rule set; agree the exact rules and defaults before implementation.
  Share human/JSON output and failure policy with validation. Do not apply generic Markdown linting wholesale:
  Tale choices and directives have their own structural meanings.
- **LSP + VS Code (M3):** a long-running C# language server handles live syntax/semantic diagnostics,
  context-aware completion, and hover/syntax help. VS Code supplies highlighting, language configuration,
  and snippets, then delegates intelligence to LSP. Other editors can launch the same server; they do not
  need VS Code. Do not invoke one-shot CLI validation for every completion request.
- **`talewright lint --fix` (M9, lowest priority):** opt-in, deterministic edits for an explicitly safe subset of lint rules.
  Preserve prose, comments, and unrelated formatting; reject overlapping edits and do not overwrite files
  changed since analysis. Reanalyze after fixing, report applied fixes and remaining diagnostics, and use
  the resulting diagnostics for the exit policy. Repeated fixes must be idempotent. Ambiguous or
  story-changing repairs remain suggestions, not automatic edits.

**Shared-analysis prerequisite:** the current compiler targets runtime execution. Its symbols are internal,
diagnostics have point locations rather than ranges, and `CompilationResult.Story` is null on errors.
Add an editor-facing analysis result that retains partial symbols, source spans/declaration locations, and
useful context even for incomplete input. Keep this separate from the source-free runtime AST.
Use the existing in-memory `StorySource` to overlay unsaved buffers on the story's other files; discover
the story root, invalidate analysis when Tale/YAML files change, and avoid publishing stale results.
Start with debounced whole-story analysis; incremental compilation is not an initial requirement and
should follow measured need.

**Initial editor scope:** highlight Tale directives/expressions alongside supported Markdown; provide
snippets for scenes, choices, conditions, and skill checks; complete scene IDs (including across files),
characters, stats, items, factions, flags, variables, directives, predicates, and text modifiers in the
appropriate context. Hover help should expose available schema descriptions/types and syntax documentation,
not execute story logic or imply knowledge of live gameplay values. Signature help, inlay hints, rename,
general formatting, and full YAML authoring assistance are outside the initial scope. YAML changes must
still refresh Tale analysis and compiler diagnostics for `story.yaml`.

**C# extensions:** the implemented project integration shares an explicit options factory between the
validator and both hosts; an opt-in transitive MSBuild target executes the author's validator.
The data-only CLI does not discover or execute arbitrary C# extensions. See
[the package-based example](../../examples/CSharpStory/README.md). Future editor analysis must agree
on these registrations through explicit integration or exported metadata, without silently treating
custom symbols as built-ins or executing arbitrary project code during editing.

**Relative effort and dependencies** (including tests/docs, not schedule estimates):

| Deliverable | Relative effort | Prerequisite / main work |
|---|---|---|
| CLI validation | Small | CLI foundation; existing compiler API, output/exit policy, integration tests |
| Highlighting, language configuration, snippets | Small | Can land independently of LSP; must follow Tale rather than generic Markdown semantics |
| Shared analysis + live diagnostics | Medium | Partial symbols/source spans, unsaved buffers, root discovery, invalidation, LSP synchronization |
| Context-aware IntelliSense + hover help | Large overall | Shared analysis and LSP; useful behavior while expressions are incomplete |
| CLI lint | Medium | CLI foundation; shared analysis for source-aware rules; rule/configuration/suppression design |
| Safe autofix subset | Medium-large increment | Lint rules and precise source spans; non-destructive edits and idempotence |

Acceptance checks belong with each deliverable: CLI output shape and exit policies; matching diagnostics
across front ends; completion on incomplete text and cross-file symbols; unsaved Tale/YAML updates and stale
diagnostic clearing; C# metadata consistency; and autofix preservation, edit-conflict, and repeat-run tests.
Track validation in [#28](https://github.com/squid-farm/talewright/issues/28), shared analysis in [#29](https://github.com/squid-farm/talewright/issues/29), lint in [#30](https://github.com/squid-farm/talewright/issues/30), LSP/VS Code in [#31](https://github.com/squid-farm/talewright/issues/31), and autofix in [#32](https://github.com/squid-farm/talewright/issues/32).

### 4.6 Presentation (`Talewright.Player` RCL plus hosts)
- Renders `RenderedFrame` → HTML via Blazor components. **It holds no game logic** and only knows `IStorySession`.
- Includes a scroll-back transcript, choice list, character sheet panel (generated from ruleset metadata, so disabled stats disappear automatically) and a basic theme (CSS variables).
- `Talewright.Host.Desktop` (Photino): opens a story folder or zip.
- `Talewright.Host.Web` (WASM): loads a story from a bundled path or a file picker.
- Each host is a thin wrapper of a few lines.

### 4.7 Planned solution layout
```
Talewright.sln
README.md  LICENSE  .gitignore  Directory.Build.props  global.json
docs/  PLAN.md  ARCHITECTURE.md  TALE-SYNTAX.md  adr/0001-…md
src/
  Talewright.Core/              RPG engine: entities, stats, inventory, factions, events, state, RNG
  Talewright.Rules/             predicates, evaluator, storylets, modifiers, triggers, skill checks
  Talewright.Narrative/         template AST, document model, renderer, grammar/pronouns
  Talewright.Rulesets.Generic/  default d20-ish attributes & skills (opt-out)
  Talewright.Tale/              .tale + story.yaml compiler (Markdig, YamlDotNet), diagnostics
  Talewright.Runtime/           IStorySession facade, command handling, frame production
  Talewright.Player/            Razor Class Library (UI)
  Talewright.Host.Desktop/      Photino.Blazor host
  Talewright.Host.Web/          Blazor WASM host
tests/
  Talewright.*.Tests/           xUnit per project + an end-to-end "play the sample" test
samples/
  LostRelic/                    story.yaml + *.tale (NOT referenced by engine projects)
```

## 5. `.tale` syntax v0

The authoritative spec is the [Tale syntax reference](../reference/tale-syntax.md). Summary:
- A story is a folder: `story.yaml` (schema, characters, factions, items, triggers) + `*.tale` files.
- A `.tale` file holds many scenes (`=== scene.id` + `@title/@requires/@tags/@priority/@once/@hub` metadata).
- Prose is inline Markdown with `{player:first-name#possessive}` substitutions and `{if}/{else}/{/if}` conditionals.
- `> who: text` speaker lines, `~ effect` lines, `* [label] (requires: expr) { effects } -> target` choices, `-> target` diverts.
- `skill-check` on a choice or as a `::: skill-check` block.

Not in v0: inline function definitions, loops, ink-style tunnels/threads, visible-but-locked choices.

## 6. Milestones

- **M0 — Foundations:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/1)) git repo, docs, license, solution skeleton, build and test green.
- **M1 — Vertical slice (desktop):** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/2)) core engine, generic ruleset, rules, narrative, `.tale` v0 compiler, runtime, Player UI and Photino host. *The Lost Relic* is playable from a folder. The web host builds and runs the same story.
- **M2 — Ship stories reliably:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/3))
  - prioritize the core production-readiness gates below before expanding gameplay or presentation;
  - saves through `ISaveStore` (file on desktop, IndexedDB on web), with format/story version identifiers and explicit, non-destructive rejection of incompatible saves ([#40](https://github.com/squid-farm/talewright/issues/40), [#34](https://github.com/squid-farm/talewright/issues/34));
  - a `talewright` CLI (`validate` with human/JSON diagnostics and explicit exit policy, `publish` producing itch-ready web and desktop zips) ([#28](https://github.com/squid-farm/talewright/issues/28), [#41](https://github.com/squid-farm/talewright/issues/41));
  - the C# story project path (Tier 1): NuGet packaging plus an MSBuild task giving build-time diagnostics ([#42](https://github.com/squid-farm/talewright/issues/42));
  - richer hubs/storylets ([#44](https://github.com/squid-farm/talewright/issues/44));
  - character creation ([#43](https://github.com/squid-farm/talewright/issues/43)).

  All listed feature slices are implemented on the M2 branch, pending review/merge and release gates.
  Saves are manual named slots with explicit overwrite; compatibility is exact story ID/version/source
  fingerprint plus extension compatibility ID, with no migration or autosave.
  `publish` defaults to story-only; `-a --target web|desktop` includes the complete player.
  Desktop defaults to one archive of six self-contained native builds and selecting launchers, not
  one portable binary. Creation is opt-in author-bounded names/pronouns/presets/stat choices, not
  an unrestricted point-buy system. See [performance and artifact checks](../../tests/README.md) for
  measured budgets; source compilation remains the format until precompiled bundles show a real benefit.
- **M3 — Author stories comfortably:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/4)) shared analysis, editor-neutral LSP, VS Code support, and a bounded initial lint rule set. Deliver independently usable slices:
  1. Tale syntax highlighting, language configuration, and snippets (independent of LSP; [#31](https://github.com/squid-farm/talewright/issues/31)).
  2. Shared partial/source-aware analysis and LSP-backed live validation ([#29](https://github.com/squid-farm/talewright/issues/29), [#31](https://github.com/squid-farm/talewright/issues/31)).
  3. Context-aware completion and hover help in a thin VS Code client; document use from other LSP editors ([#31](https://github.com/squid-farm/talewright/issues/31)).
  4. `talewright lint` with a small, agreed initial rule set and shared CLI/editor diagnostics ([#30](https://github.com/squid-farm/talewright/issues/30)).
  These are delivery slices, not a requirement to hold all releases until every slice is complete.
- **M4 — Presentation:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/5)) bounded image/media support, including asset resolution/loading, safe paths,
  publishing/bundling, and consistent desktop/web behavior. Agree supported asset types and limits before implementation.
- **M5 — World simulation:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/6)) locations/travel, deterministic time advancement, and event-backed clocks;
  expose world state to storylet eligibility and Tale authoring. Build on hubs rather than replace them
  ([FR-028 / #36](https://github.com/squid-farm/talewright/issues/36)).
- **M6 — Combat:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/7)) an optional ruleset-driven module, with explicit encounter/turn/outcome contracts,
  deterministic events/replay, narrative transitions, and desktop/web presentation
  ([FR-029 / #37](https://github.com/squid-farm/talewright/issues/37)).
- **M7 — Runtime plugins:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/8)) a versioned extension API, bounded sandboxed scripting for desktop/web, and
  an explicitly trusted desktop DLL path; include packaging, persistence, diagnostics, and compatibility
  contracts ([FR-030 / #38](https://github.com/squid-farm/talewright/issues/38)).
- **M8 — Save migration:** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/9)) explicit save-format and story-version migration paths, migration fixtures,
  non-destructive conversion, and desktop/web load integration ([FR-031 / #39](https://github.com/squid-farm/talewright/issues/39)).
- **M9 — Safe authoring fixes (lowest priority):** ([GitHub milestone](https://github.com/squid-farm/talewright/milestone/10)) a documented safe subset of `talewright lint --fix`,
  with conflict detection, preservation, reanalysis, and idempotence ([FR-024 / #32](https://github.com/squid-farm/talewright/issues/32)).

Milestone numbers express the planned phases, not artificial dependencies. M2 readiness comes first;
later milestones can ship independently once their stated prerequisites are met. In particular, M8 depends
on M2 saves, not on combat or plugins, and migration work can be pulled forward for a compatibility change.
M9 depends on M3 analysis/lint, not on M4-M8. None of M4-M9 blocks the core or authoring release.
Each milestone needs its own acceptance evidence; moving it later does not remove it from the backlog.
M4's concrete deliverables are tracked in [FR-027 / #35](https://github.com/squid-farm/talewright/issues/35).

### Core production-readiness gates (M2)

These are planned release criteria, not claims that the current engine is production-ready. Existing M2
features remain in scope, but reliability work takes precedence over additional features.

- **Correctness:** resolve known S1/S2 bugs in [issue tracking](issues.md) and explicitly triage remaining
  release risks. Cover the existing M2-deferred arithmetic overflow and commented-out auto-declaration
  bugs ([#6](https://github.com/squid-farm/talewright/issues/6)/[#8](https://github.com/squid-farm/talewright/issues/8)) with fixes and regression tests before release.
- **Save safety:** round-trip saves on desktop and web; preserve deterministic state/choices on reload;
  reject corrupt, unsupported, or incompatible saves with actionable errors and without partial session
  mutation or overwriting the original. Define compatibility policy before claiming cross-version support.
- **Input and failure handling:** maintain bounded untrusted story/archive loading, test malformed input,
  and surface load/save/validation/publish failures clearly rather than failing silently or appearing successful.
- **Release verification:** build/test in CI; run the existing player regression suites and verify published
  Release artifacts, not only development hosts. Check desktop and web load/play/save flows, static hosting
  and itch.io-style subpaths, trimming, and install/publish instructions. Document the tested platform matrix
  and any unverified platforms rather than treating a Linux test as evidence for every desktop OS.
- **Operational clarity:** document supported story/save formats, CLI diagnostic/exit policies, and known
  limitations. Measure representative story load/validation and save/replay behavior; agree explicit budgets
  from those measurements rather than adding speculative optimization or declaring performance acceptable.

Track the readiness and save-compatibility work in [FR-025 / #33](https://github.com/squid-farm/talewright/issues/33) and [FR-026 / #34](https://github.com/squid-farm/talewright/issues/34).

### Backlog organization for GitHub

The migration is complete: [GitHub Issues](https://github.com/squid-farm/talewright/issues) holds the live
backlog, preserving FR/BUG identifiers. [Milestones](https://github.com/squid-farm/talewright/milestones)
are release targets; [epics](issues.md#epics) group work across milestones without promoting exploratory
ideas to approved tasks. Each milestone feature has a dedicated issue with prerequisites, implementation
slices, decisions, and acceptance checks. Split slices into linked child issues when taking up the work;
use native blockers, not numeric milestone order, for actual dependencies. Update live requirements in
GitHub instead of recreating a local board.

### Code-extension tiers (for reference)
- **Tier 0 (M1):** `.tale` plus YAML only.
- **Tier 1 (M2):** a C# story project references `Talewright.*` NuGet packages, registers named predicates, stats, modifiers, grammar modifiers and rulesets, and builds its own desktop and web app.
- **Tier 2 (M7):** plugins inside the generic player.
  - Desktop: AssemblyLoadContext (full trust, no sandbox).
  - Web: arbitrary DLLs are fragile under trimming, so **sandboxed script plugins (Jint/MoonSharp) are preferred**.
  - Roslyn scripting in the browser was rejected (10+ MB, slow).

## 7. Todos (M0 + M1)

**M0 and M1 are done** (all rows below). See section 9 for what was built and how it differs from the plan.

| id | title | depends on | status |
|---|---|---|---|
| repo-init | git init, .gitignore, LICENSE (MPL-2.0), README, docs/PLAN.md, ARCHITECTURE.md, TALE-SYNTAX.md, initial ADRs; first commit | — | done |
| solution-skeleton | Talewright.sln, Directory.Build.props (net10.0, nullable, warnings-as-errors), global.json, all src/tests projects, build+test green | repo-init | done |
| core-entities | Typed IDs, Entity + components, stat definitions, ruleset builder (add/remove/replace) | solution-skeleton | done |
| core-events | GameEvent records, event store, state projection (fold), read-only IGameStateView, history queries | core-entities | done |
| core-inventory-factions | Inventory relations, factions/relationships with ruleset-defined tiers, levels/XP hooks | core-events | done |
| core-rng | Seeded IRandomSource, DiceRolled events, deterministic replay | core-events | done |
| ruleset-generic | Generic d20-ish attributes/skills/tiers/skill-check rule as an opt-out module | core-inventory-factions | done |
| rules-predicates | Expression/predicate AST + evaluator + named predicate registry | core-events | done |
| rules-storylets | Storylet eligibility/selection, diverts vs hubs, priority/tags | rules-predicates | done |
| rules-modifiers-triggers | Derived stats/modifiers, bounded triggers | rules-predicates, core-inventory-factions | done |
| rules-skill-checks | Skill-check resolution via ruleset + RNG, per-story randomness config | rules-predicates, core-rng, ruleset-generic | done |
| narrative-model | Template AST + RenderedDocument model (incl. MediaPlaceholder) | solution-skeleton | done |
| narrative-renderer | Renderer: substitutions, conditionals, modifier pipeline, English grammar (possessive, a/an, capitalize), pronoun sets | narrative-model, rules-predicates | done |
| tale-compiler | Markdig + YAML front matter + story.yaml → StoryDefinition; v0 directives; diagnostics with line/col; schema validation | narrative-model, rules-storylets, rules-skill-checks, ruleset-generic | done |
| runtime-session | IStorySession: commands → events → triggers → selection → render → StoryFrame | tale-compiler, narrative-renderer, rules-modifiers-triggers | done |
| player-rcl | Blazor RCL: transcript, choices, ruleset-driven character sheet, theme | runtime-session | done |
| host-desktop | Photino.Blazor host: open story folder/zip | player-rcl | done |
| host-web | Blazor WASM host: load bundled story / file picker; verify static publish | player-rcl | done |
| sample-lost-relic | samples/LostRelic: female treasure hunter in an ancient temple; exercises stats, inventory, faction gate, skill check, pronouns/possessives, branching + convergence | tale-compiler | done |
| e2e-tests | Deterministic shared story fixtures, three-engine Playwright player suite, and real Linux Photino tests; LostRelic compilation/bundled-open smoke only (see tests/README.md) | runtime-session, sample-lost-relic | done |

## 8. Risks / notes
- **itch.io and Blazor WASM:** itch serves from a CDN subpath inside an iframe. We need a relative `<base href>` and must check how pre-compressed `.br`/`.gz` files are served (we may need to ship uncompressed). To be verified in M2.
- **Photino on Linux** requires WebKitGTK (`libwebkit2gtk-4.1`). Document it in the README.
- **Trimming:** avoid reflection-heavy designs, and use source-generated `System.Text.Json` contexts so WASM trimming stays safe.
- **Possessive style:** the default is **grammatically correct English** following Chicago Manual of Style rules:
  - singular nouns and names always take `'s` (`Mara's`, `Burns's`);
  - plurals ending in `s` take only `'` (`the guards'`);
  - irregular plurals take `'s` (`the children's`).
  - Names and nouns carry a `plural` flag so the renderer can tell singular from plural.
  - Overrides (AP style, or invented sci-fi/fantasy forms) are a **future phase**, handled through the `IGrammar` seam.
  - Note: this makes `{player:last-name#possessive}` → `Burns's`, not the `Burns'` from the original example.
- **Path syntax:** use `entity:path` **everywhere**, in both substitutions and expressions, for consistency (e.g. `player:dex >= 12`). Flagged in case you prefer `player.dex` in expressions.
- **Event-log growth** in long open-world games: snapshots come later. Not an M1 concern.
- The no-code path parses text **once at load**. That is accepted under D5; per-frame rendering never parses strings.

## 9. M1 outcome (what was actually built)

Read [architecture](architecture.md) for the code tour. Summary of where reality differs from, or sharpens,
the plan above:

- **Solution file** is `Talewright.slnx` (the new XML format), not `.sln`.
- **Rendered output type** is `StoryFrame` (Runtime), not `RenderedFrame`. The media placeholder is
  `RenderedMedia`, not `MediaPlaceholder`.
- **`Entity` + components** (from `core-entities`) became simpler: characters are `CharacterDefinition` plus stats
  and inventory in `GameState`. An entity/component model can come with open-world needs; nothing outside Core
  depends on the representation.
- **RNG** is a pure function, `DeterministicDice.Roll(seed, index, sides)`, rather than an `IRandomSource`
  object. Same guarantees, nothing to serialise.
- **Fall-through diverts:** a choice or check outcome without `-> target` continues at the scene's first matching
  `->`, evaluated *after* its effects. This was not in the original syntax draft; it removes a lot of repetition.
  Documented in the [Tale syntax reference](../reference/tale-syntax.md).
- **Built-in rulesets:** `generic` and `none` (no stats or checks; tiers only).
- **Bundled stories** are embedded resources in each host (`stories/<Story>/<path>`), read through
  `IStoryLibrary`. No HTTP manifest. Desktop also takes folders/zips on the command line; both hosts accept a zip
  upload.
- **Web host has no router** (one screen), so subfolder static hosting needs only a `<base href>` change.
- **Validator** suppresses "unreachable scene" warnings while errors exist (one broken divert used to flood the
  list).
- **Verification:** 69 automated tests including 300 seeded random playthroughs of the sample; the desktop
  app was launched against the sample; the published web build was played to an ending in headless Chromium, and
  good and broken zips were uploaded.

### Open questions for review

1. Fall-through divert semantics (above): keep?
2. The `none` ruleset keeps the generic reputation tiers. Should it have no tiers?
3. Embedding all of `samples/` in both hosts: fine for now, to be replaced by `talewright publish` in M2?
4. Item names are lowercase in the sample ("sputtering torch") and appear that way on the character sheet. Should
   the sheet capitalise, or should authors write display case?
5. **Licence: decided 2026-10-20, keep MPL-2.0.** GPL-3.0 with a linking exception was considered (the goal being
   "the engine stays open, games can use any licence"). It was rejected: MPL already gives games that freedom, no
   open-source licence prevents reselling or copying the engine, and stronger copyleft wouldn't stop anyone
   determined to reimplement it. Revisit only if dual licensing (a paid commercial tier) is ever wanted.
