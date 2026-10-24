# Architecture

This is the guided tour of the code: what each project does, how a click becomes text on screen, where the
extension points are, and where to look when something breaks. [the plan](plan.md) records _why_ (decisions,
research, roadmap); this document records _what is actually built_. The [ADRs](adr) hold the big decisions in
long form, and the [Talemark syntax reference](../reference/talemark-syntax.md) is the authoritative reference for the story language.

Contents:

1. [The big picture](#1-the-big-picture)
2. [Invariants (the rules the code must keep)](#2-invariants)
3. [Project by project](#3-project-by-project)
4. [Life of a story: load → compile → play](#4-life-of-a-story)
5. [The engine loop in detail](#5-the-engine-loop-in-detail)
6. [Extension points](#6-extension-points)
7. [Where to find X](#7-where-to-find-x)
8. [Testing](#8-testing)
9. [Known limitations and seams left for later](#9-known-limitations-and-seams-left-for-later)

---

## 1. The big picture

### NuGet distribution

Project boundaries and the dependency graph below remain intact. The
`packaging/Talewright/Talewright.csproj` shell references all six headless projects and
packages their existing assemblies together as `Talewright`; it contains no engine
implementation. `Talewright.Player` retains its direct Runtime project reference,
and its published package depends on `Talewright`. Both packages share one version.
The component projects are not separately published. Namespaces and assembly
identities are independent of source paths; packaging is independent of code organization.

### Repository areas

The workspace is organized by responsibility, not by implementation language:

- `engine/src` and `engine/tests`: reusable headless libraries and their tests.
- `player/src` and `player/tests`: reusable UI, opt-in end-to-end suites, and their runners.
- `apps/web` and `apps/desktop`: runnable hosts of the player.
- `tooling/authoring`, `tooling/cli`, `tooling/debugger`, and
  [`tooling/language-server`](language-server.md): development tools,
  each with local source/tests where applicable. The VS Code extension remains
  in `tooling/vscode-talewright`.
- `packaging/Talewright`: the aggregate NuGet packaging shell.
- `benchmarks`: performance budgets, BenchmarkDotNet workloads, and report tooling.
- Root `tests`: repository-wide CI/Nx contracts only.

The root solution mirrors these areas. Shared story fixtures are in
`engine/tests/Talewright.TestStories`; the separate player E2E solution remains
opt-in. Root build defaults, Nx, and CI coordinate all areas. See
[Contributing](../../CONTRIBUTING.md#repository-layout) for paths and commands.

Ordinary story authors need no NuGet project dependencies. C# customizers normally
install only `Talewright.Player`; headless/custom-UI consumers install `Talewright`.

Talewright is a **storylet engine** sitting on an **event-sourced RPG state**. A story is a set of _scenes_. Each
scene has prose (a template), optional requirements, effects, skill checks, choices and diverts. Nothing about
"which scene comes next" is hard-wired into the engine: scenes say where to go (`-> scene`, "rails"), or a hub scene
offers every storylet whose requirements currently hold ("hubs"). The same mechanism will later drive
open-world locations.

The three tiers you asked for map onto projects like this:

| Your tier             | Projects                                                              | One-line job                                                                         |
| --------------------- | --------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| **RPG engine**        | `Talewright.Core`, `Talewright.Rulesets.Generic`                      | Who exists, what they have, what has happened. Pure state + events.                  |
| **Rules engine**      | `Talewright.Rules`                                                    | Evaluate conditions, apply effects, resolve checks, fire triggers, select storylets. |
| **Template renderer** | `Talewright.Narrative`                                                | Turn a typed template AST + state into a presentation-agnostic document.             |
| **Presentation**      | `Talewright.Player`, `Talewright.Host.Desktop`, `Talewright.Host.Web` | Blazor UI and the two native/web shells.                                             |

Two more projects glue it together:

- `Talewright.Tale` is the **compiler**: it turns `.tale` + `story.talewright.yaml` text into the typed objects the other
  layers use. It is the _only_ place that parses strings, and it runs once when a story loads.
- `Talewright.Runtime` is the **orchestrator**: `StorySession` runs the loop (choice → events → rules → render →
  frame). It is the only project that knows about all the others.

`Talewright.Authoring` is a development-tool library above the compiler. It owns
unsaved workspaces, analysis scheduling, explicit project metadata integration,
and the shared Talemark-aware lint rules/configuration consumed by the CLI and editor.
The compiler exposes parser-backed partial analysis without depending on this
library. Authoring is excluded from the shipped engine and player packages; see
[shared authoring analysis](authoring-analysis.md).

### Dependency graph

Arrows mean "references". Nothing points upward; the engine never knows about the UI.

```
        Host.Desktop   Host.Web
              \          /
               ▼        ▼
             Talewright.Player   (Blazor RCL)
                     │
                     ▼
             Talewright.Runtime  (StorySession, EventNarrator)
                     │
                     ▼
             Talewright.Tale ─────────────────► Talewright.Rulesets.Generic
             (compiler, StoryDefinition)                │
               │            │                           │
               ▼            ▼                           │
   Talewright.Narrative ─► Talewright.Rules             │
                               │                        │
                               ▼                        ▼
                           Talewright.Core ◄────────────┘
```

(`Narrative` references `Rules` because inline conditionals `{if …}` use the same typed expression trees and
evaluator as everything else.)

### The data that flows between tiers

| Boundary             | Type                                                                | Defined in |
| -------------------- | ------------------------------------------------------------------- | ---------- |
| Story files → engine | `StorySource` (path → text map)                                     | `Talewright.Tale` |
| Compiler → runtime   | `StoryDefinition` (world, scenes, triggers, predicates)             | `Talewright.Tale` |
| Rules ↔ state        | `IGameStateView` (read) and `GameEvent`s (write)                    | Core       |
| Templates → renderer | `TemplateBlock` / `TemplateInline` AST                              | Narrative  |
| Renderer → UI        | `RenderedDocument` (blocks, styled runs, notes, media placeholders) | Narrative  |
| Runtime → UI         | `StoryFrame` (scene title, document, `ChoiceView`s, ended flag)     | Runtime    |
| UI → runtime         | a choice **key** string (`IStorySession.Choose(key)`)               | Runtime    |

The UI never sees `Expr`, `Effect`, scenes or events directly. It renders frames and sends back keys. It also
reads `IGameStateView` for the character sheet. `StoryPlayer` uses the `StoryHistory` wrapper for
transcript, Back/Forward and save orchestration; presentation does not reimplement replay.

---

## 2. Invariants

These are load-bearing. Breaking one will break saves, replays, tests or the "decoupled" promise.

1. **State changes only by applying `GameEvent`s.** `GameState.Apply(event)` is the only mutator, and
   `EventLog.Append` is the only caller. State is `fold(initial, events)`. Want a new kind of change? Add an event.
2. **Dice results live in events.** `DiceRolled` stores the value. Replaying a log never re-rolls, and
   `DeterministicDice.Roll(seed, index, sides)` is a pure function, so seed + choices reproduces a playthrough.
3. **The engine has no built-in stats.** Strength, hit points, levels, reputation tiers, how checks resolve: all
   come from the `Ruleset`. `Talewright.Core` does not mention "strength" anywhere. The generic d20 set is an
   opt-out module.
4. **No string parsing at play time.** Templates and conditions are compiled into typed trees (`Expr`,
   `TemplateInline`, `Effect`). Rendering a frame walks trees; it never parses.
5. **No reflection or runtime code generation in the engine.** This keeps Blazor WASM trimming safe. Extension
   is by registering objects/delegates (predicates, modifiers, rulesets), not by scanning assemblies.
6. **Stories are data, outside the engine.** `samples/` is not referenced by engine code. Hosts merely bundle it.
7. **No fixed "next scene" pointer.** Movement is the `SceneEntered` event. A scene never "owns" its successor;
   it requests one. This is what keeps the door open for open-world play (locations, travel, time).
8. **The renderer never produces HTML.** It produces `RenderedDocument`. The Player turns that into HTML; a
   future console or Unity front-end could turn it into something else.

---

## 3. Project by project

Each project also has its own `README.md` with a file-by-file guide. This section is the summary.

### 3.1 `Talewright.Core` — the RPG engine

Folders:

- **`Identity/Ids.cs`**: strongly typed ids (`EntityId`, `StatId`, `ItemId`, `FactionId`, `SceneId`, `FlagId`,
  `VarId`). They are readonly record structs wrapping a string, so you can't pass a stat where a faction is
  expected. `EntityId.Player` is the well-known player id.
- **`Rulesets/`**: the "what kind of game is this" layer.
  - `StatDefinition(Id, Name, Category, Min, Max, Default, GoverningAttribute)`. A stat is just an integer with
    bounds and a display name. `StatCategory` (Attribute, Skill, Resource, Progression, Custom) is only used for
    grouping (the character sheet uses it).
  - `ReputationTier(Id, Name, Minimum)`: named bands of faction standing (hostile … allied).
  - `ISkillCheckRule`, `ILevelingRule`, `IStatModifier`: the pluggable behaviours. See §6.
  - `Ruleset` is immutable; `RulesetBuilder` builds it with `AddStat` / `RemoveStat` / `ReplaceStat` /
    `WithTiers` / `WithSkillCheckRule` / `WithLevelingRule` / `AddModifier`. `ruleset.ToBuilder()` starts from an
    existing one, which is how a story "turns off" stats it doesn't want.
- **`World/`**: static definitions. `CharacterDefinition` (names, pronouns, starting stats and inventory),
  `ItemDefinition` (name + stat modifiers while held), `FactionDefinition`, and `WorldDefinition`, which bundles
  them all with the ruleset. `Pronouns` holds subject/object/possessive/reflexive forms plus presets.
- **`Events/GameEvents.cs`**: every possible change, as records: `StatChanged`, `ItemsGranted`, `ItemsRemoved`,
  `StandingChanged`, `FlagChanged`, `VariableChanged`, `SceneEntered`, `ChoiceMade`, `DiceRolled`,
  `SkillCheckResolved`, `LevelChanged`, `TriggerFired`, `StoryEnded`. Events store **absolute new values**
  (`StatChanged(..., OldValue, NewValue)`), not deltas, so they are self-describing in a log.
- **`State/`**:
  - `IGameStateView`: the read-only query surface everything else uses (`GetStat`, `GetBaseStat`,
    `GetItemCount`, `GetInventory`, `GetStanding`, `GetFlag`, `GetVariable`, `VisitCount`, `HasFiredTrigger`…).
  - `GameState`: the projection. `CreateInitial(world, seed)` + `Apply(event)`. `GetStat` = base stat + every
    `IStatModifier` + held items' modifiers, clamped to the stat's bounds.
  - `EventLog`: owns a `GameState` and the event list. `Append`, `AppendRange`, `Replay(world, seed, events)`,
    `Truncate(count)` (for future undo), and an `Appended` event.
  - `StateChanges`: helpers that _compute_ the events for a change (`AdjustStat`, `GiveItem`, `SetStanding`,
    `CheckLevel` …). They clamp values and return nothing if nothing changes. Use these rather than building
    events by hand.
- **`Randomness/DeterministicDice.cs`**: SplitMix64-based `Roll(seed, index, sides)`. The index is
  `state.RollCount`, which advances with each `DiceRolled` event.

### 3.2 `Talewright.Rulesets.Generic` — the default d20-flavoured ruleset

`GenericRuleset.Create()` builds: six attributes (`str dex con int wis cha`, 1–30, default 10), a list of skills
each governed by an attribute (athletics→STR, perception→WIS, history→INT, survival→WIS …), `hp`, `xp`, `level`,
the five reputation tiers, `D20SkillCheckRule` (d20 + skill + attribute modifier vs DC; with randomness off it
"takes 10"), and `ExperienceLevelingRule` (level _n_ needs 100 × _n_ × (_n_ − 1) / 2 XP: 0, 100, 300, 600 …; max 20). It is registered under the name `generic`.
There is also `none` (no stats, no checks, just the tiers) for stories such as dating sims that want to define
everything themselves. Both are listed in `CompilerOptions.DefaultRulesets()`.

### 3.3 `Talewright.Rules` — the rules engine

- **`Expressions/`**: the typed condition/value language.
  - `Expr` is an abstract record with one subclass per node: literals, `StatRef`, `StandingRef`,
    `StandingTierRef`, `FlagRef`, `VarRef`, `HasItem`, `Visited`, `NamedPredicate`, `NameRef`, `PronounRef`,
    `ItemNameRef`, `FactionNameRef`, `UnaryExpr`, `BinaryExpr`. Each node has an `ExprType` (Number, Bool,
    Text, Tier) checked at compile time.
  - `ExpressionEvaluator.Evaluate(expr, EvaluationContext)` → `Value`. The context is the state view plus the
    `NamedPredicateRegistry`.
  - `NamedPredicateRegistry`: C#-defined conditions callable from stories as `@Name`. The escape hatch for logic
    too hairy for an expression.
- **`Effects/Effects.cs`**: `ChangeStat`, `ChangeStanding`, `ChangeVariable`, `SetFlag`, `GiveItem`, `TakeItem`,
  each with an `AssignOp` (`=`, `+=`, `-=`). `EffectExecutor.Execute` evaluates the amount and appends the
  events from `StateChanges`.
- **`Checks/SkillCheckResolver.cs`**: `SkillCheck(Actor, Skill, Difficulty)` + `RandomnessMode` (`Off`,
  `ChecksOnly`). Rolls the ruleset's die (appending `DiceRolled`), asks the ruleset's `ISkillCheckRule` for the
  result, and appends `SkillCheckResolved`.
- **`Triggers/TriggerEngine.cs`**: `TriggerDefinition(Id, When, Effects, Once)`. After every change the runtime
  calls `Run(log)`, which fires triggers whose condition has become true (edge-triggered: it fires on
  false→true, not every frame). It loops until stable, capped at `MaxPasses = 32`; reaching the cap with
  eligible triggers remaining throws an error instead of silently skipping effects.
- **`Storylets/StoryletSelector.cs`**: `IStorylet` (Id, Requires, Tags, Priority, Once) and `SelectEligible(…,
tag, context)`. Eligible = has the tag, requirements hold, and not already visited if `Once`. Ordered by
  priority (high first), then declaration order. `SceneDefinition` implements `IStorylet`.

### 3.4 `Talewright.Narrative` — the template renderer

- **`Templates/Template.cs`**: the input AST.
  - Blocks: `ParagraphTemplate`, `SpeechTemplate` (speaker + line), `ConditionalBlockTemplate`.
  - Inlines: `TextTemplate`, `StyledTemplate` (emphasis/strong/code, nested via `TextStyle` flags),
    `LineBreakTemplate`, `MediaTemplate` (reserved for images), `SubstitutionTemplate` (an `Expr` plus a list of
    text modifiers), `ConditionalInlineTemplate`.
  - Note: this is what "strongly typed, not string parsing" means in practice. `{player:first-name#possessive}`
    compiles to `SubstitutionTemplate(NameRef(player, First), [possessive])`.
- **`Templates/TextModifiers.cs`**: `ITextModifier` (`#possessive`, `#capitalize`, `#upper`, `#lower`, `#a` …) and
  `TextModifierRegistry`. Register your own to add `#whatever`.
- **`Grammar/EnglishGrammar.cs`**: `IGrammar` with Chicago-style possessives (`Burns's`, `the guards'`),
  a/an, capitalisation. Swap the grammar to localise or change style.
- **`Rendering/`**: `TemplateRenderer.Render(blocks, state)` → `RenderedDocument`. Output types:
  `RenderedParagraph`, `RenderedSpeech`, `RenderedNote` (system messages like "Gained: rope", with a
  `NoteKind`), and inlines `RenderedText(Text, Style)`, `RenderedLineBreak`, `RenderedMedia(MediaId, AltText)`.

### 3.5 `Talewright.Tale` — the compiler

Entry point: `StoryCompiler.Compile(StorySource, CompilerOptions?) → CompilationResult(Story?, Diagnostics)`.

- **`Compilation/`**
  - `StorySource`: a dictionary of relative path → text, plus an immutable `ImagePaths` catalogue of
    story-root-relative paths under `images/`. Image bytes are not read or decoded.
    In-memory callers can supply image paths through the constructor.
    Factories: `FromDirectory`, `FromZip(stream)` (a
    single wrapping top-level folder is stripped). `ReadTitle()` peeks at `story.talewright.yaml` without compiling (used
    by the library screen).
  - `StoryImageResolver`: the shared path-only lookup for future compiler and CLI image checks.
    It takes a `StorySource`, a path relative to `images/`, optional alt text, the reference's
    source location, and a `DiagnosticBag`. It returns a `ResolvedStoryImage` with a
    story-root-relative path and effective alt text, or null with an error.
    Paths match case-sensitively; explicit extensions recognize PNG, JPEG and WebP regardless
    of extension casing, but the filename must still match exactly. Extensionless paths try
    `.png`, `.jpg`, `.jpeg`, then `.webp`, warning and listing matches if ambiguous.
    Absolute paths, parent traversal, backslashes, web addresses, empty path segments,
    query strings and fragments are rejected. No files are downloaded.
    Missing, empty or whitespace-only alt text warns and uses Humanizer.Core 3.0.10 to
    turn the filename into words, then applies invariant sentence case. For example,
    `old_map-v2.png` becomes `Old map v2`; punctuation-only names become `Image`.
    Author-written alt text is preserved exactly. All diagnostics point to the reference.
    This is the foundation from #198. Its diagnostics remain API-only until a follow-up
    connects the resolver to story compilation and the existing `talewright validate`
    command. The syntax-only slice in #197 does not include resolver integration or
    image-file existence checks. This adds no image rendering or image size checks.
  - `CompilerOptions`: the registries the compiler resolves names against: `Predicates` (for `@Name`),
    `Modifiers` (for `#mod`) and `Rulesets` (name → factory, for `ruleset: base:`). Tier 1 extension in M2 means
    "pass your own options".
  - `StoryCompiler`: orchestrates. Order: read `story.talewright.yaml` → build ruleset and `WorldDefinition` → syntax discovery of
    every `.tale` for scene ids and auto-declared flags/vars (so forward references work) → parse each `.tale` →
    validate → assemble `StoryDefinition`. Any error ⇒ `Story` is null.
  - `StoryValidator`: whole-story checks after parsing: start scene exists, dead ends, fall-through with nowhere
    to go, empty hubs, unreachable scenes.
- **`Parsing/`**
  - `StoryYamlReader`: `story.talewright.yaml` → manifest (uses YamlDotNet's node model, not reflection-based
    deserialisation, for trimming and precise error locations).
  - `TaleParser`: line-based parser for `.tale` structure: `=== scene` headers, `@directives`, `~ effects`,
    `* choices`, `-> diverts`, `::: skill-check` blocks, `{if}` blocks, speech lines, paragraphs.
  - `InlineParser`: prose → inline template AST. Masks `{…}` substitutions, lets Markdig parse emphasis, then
    restores the substitutions. Only the paragraph parser is enabled so `#`, `>`, lists etc. are not reinterpreted.
  - `ExpressionParser` + `Lexer`: recursive-descent parser for conditions, numbers, substitutions and effects,
    with type checking (TW2xxx).
  - `StorySymbols`: what's declared (stats, characters, items, factions, flags, vars, scenes), for resolving
    names.
- **`Model/StoryDefinition.cs`**: the compiled story. `StoryDefinition` (Id, Title, Start, Randomness, Seed,
  World, Scenes, Triggers, Predicates), `SceneDefinition`, `ChoiceDefinition`, `CheckBranch`/`CheckOutcome`,
  `ConditionalEffect`, `ConditionalDivert`, `DivertTarget` (`End` is a target too).
- **`Diagnostics/Diagnostic.cs`**: `Diagnostic(Severity, Code, Message, SourceLocation(File, Line, Column))`.
  Codes are documented in the [diagnostics reference](../reference/diagnostics.md).

### 3.6 `Talewright.Runtime` — the orchestrator

- `IStorySession`: `Start()`, `Choose(key)`, `Current`, `State`, `Events`, `Seed`, `Story`. The UI's only
  contract.
- `StorySession`: the engine loop (§5).
- `StoryFrame(Scene, Title, Document, Choices, IsEnded)` and `ChoiceView(Key, Label)`.
- `EventNarrator.Describe(events, state)`: turns the events produced by an action into `RenderedNote`s
  ("Athletics check: 10 +2 = 12 vs DC 12 — success", "Gained: rope", "Explorers' Guild −20 (now Unfriendly)",
  "Level 2!"). This is why authors never write "you lost 3 HP" by hand.

### 3.7 `Talewright.Player` — the shared Blazor UI (Razor Class Library)

- `Components/TalewrightApp.razor`: the whole app. Library screen (list + `.zip` upload) → compile → either show
  diagnostics or play. Hosts render only this. Opening the library mid-story keeps the player mounted (hidden),
  so a "Back to …" button resumes the playthrough; picking a story from the list starts it fresh.
- `Components/StoryPlayer.razor`: one playthrough. Keeps a transcript of past frames (echoing the chosen label),
  shows current choices, Back/Forward, named manual saves and Restart/Library buttons. Its collocated `StoryPlayer.razor.js` module scrolls the
  newest frame to the top of the viewport and focuses it (moving screen readers to it); plain `focus()` would
  not scroll a frame that is already partly visible.
- `Components/DocumentView.razor`, `InlineView.razor`: `RenderedDocument` → HTML (`<p>`, `<em>`, `<strong>`,
  `<code>`). `RenderedMedia` renders nothing yet.
- `Components/CharacterSheet.razor`: generated from ruleset metadata; whatever stats the story's ruleset has,
  grouped by `StatCategory`. Skills at 0 are hidden. Shows inventory and faction standings with tiers.
- `Components/DiagnosticList.razor`: compiler errors/warnings with file:line:col.
- `Components/CharacterCreator.razor`: optional author-bounded creation form. `TalewrightApp` validates
  selections before constructing history; presets/identity/stats become initialization events, never
  changes to shared static world definitions. Restart keeps the active (including restored) character.
- `Saves/`: `FileSaveStore` uses staged atomic file replacement; `IndexedDbSaveStore` uses browser transactions.
  Failures are visible and rejected restores do not replace live history or mutate the stored save.
- `Library/StoryLibraries.cs`: `IStoryLibrary` (List/Load) with `EmbeddedStoryLibrary` (stories compiled into
  the host assembly), `FileSystemStoryLibrary` (folders/zips; desktop only) and `CompositeStoryLibrary`.
- `wwwroot/talewright.css`: all player styles, driven by CSS variables (`--tw-bg`, `--tw-accent`…), with a dark
  mode via `prefers-color-scheme`. Hosts link it as `_content/Talewright.Player/talewright.css`.

### 3.8 The hosts

- **`Talewright.Host.Desktop`** (Photino.Blazor): a native window using the OS webview (WebView2 / WKWebView /
  WebKitGTK). No browser, no server, no ports. `Program.cs` registers a composite library (embedded samples +
  command-line paths) and auto-opens the first path. `wwwroot/index.html` is the shell page.
- **`Talewright.Host.Web`** (Blazor WebAssembly): `dotnet publish` produces static files only, so any static host
  works (GitHub Pages, itch.io, an S3 bucket, `python -m http.server`). There is no router on purpose: one
  screen means no URL-rewrite rules when hosted in a subfolder. Change `<base href>` in `wwwroot/index.html` for
  subfolder hosting.

Both hosts embed `samples/**/*.tale|yaml` as resources named `stories/<Story>/<path>` (see the `EmbeddedResource`
item in each `.csproj`). That avoids any HTTP fetching or manifest files for the bundled stories.
Story resources disable MSBuild culture inference so names such as `chapter.fr.tale`
stay in the main assembly. The CLI builds standalone hosts using explicit Razor/HTML
templates (including a relative web base path); project changes use `XDocument` and
`XElement` serialization, not hand-built XML or replacements inside component source.

---

## 4. Life of a story

```
samples/LostRelic/               IStoryLibrary.LoadAsync        StoryCompiler.Compile
  story.talewright.yaml           ───────►  StorySource            ──────►  CompilationResult
  chapters/*.tale                (path → text)                    ├─ Diagnostics (shown if errors)
                                                                  └─ StoryDefinition
                                                                          │
                                         new StorySession(story, seed) ◄──┘
                                                 │ Start()
                                                 ▼
                                            StoryFrame ──► StoryPlayer renders it
                                                 ▲                 │ player clicks
                                                 └── Choose(key) ◄─┘
```

1. **Load.** The host's `IStoryLibrary` hands back a `StorySource`. For an uploaded zip, `TalewrightApp` buffers
   it into memory first (browser streams aren't seekable) and calls `StorySource.FromZip`.
2. **Compile.** `StoryCompiler` reads `story.talewright.yaml`:
   - `ruleset.base` picks a ruleset factory (`generic`, `none`, or one you registered); `ruleset.disable`
     removes stats; `ruleset.stats` adds or overrides them.
   - `characters`, `items`, `factions`, `flags`, `vars`, `triggers` become the `WorldDefinition` and
     `TriggerDefinition`s.
   - Then every `.tale` file (sorted by path, subfolders allowed) is parsed into `SceneDefinition`s. Names are
     resolved and type-checked here, so `player:strenght` is a TW2xxx error at load time, not a crash at chapter 9.
3. **Play.** Fixed stories start immediately; creation stories first validate and snapshot an author-approved
   selection. `StorySession` creates an `EventLog` from world + seed, applies character initialization,
   primes triggers and enters the start scene. Every `Choose(key)` appends events and returns a new frame.
   `StoryHistory` replays a fresh session for Back/Forward and validates the entire saved redo sequence
   before replacement, preserving deterministic dice and trigger edge memory.
4. **Render.** `StoryPlayer` appends each frame to its transcript and asks `DocumentView` to draw it. The character
   sheet re-reads `IGameStateView` on each render, so it's always current.

---

## 5. The engine loop in detail

All of this is in `engine/src/Talewright.Runtime/StorySession.cs` (the class remarks repeat it next to the code).

**Entering a scene** (`Enter`):

1. Append `SceneEntered(scene)` (this increments the visit count).
2. Run entry effects (`~` lines), each only if its `{if}` condition holds.
3. `AfterChanges()`: run triggers until stable, then `StateChanges.CheckLevel` for every character.
4. Render the scene body with the _current_ state, so text reflects the entry effects.
5. Append notes describing what changed (`EventNarrator`).
6. Decide what to offer:
   - **Entry skill check** (`::: skill-check` at the top level of a scene): resolve it now, apply the outcome's
     effects, and offer a single **Continue** that leads to the outcome's target.
   - Otherwise, **choices**: explicit `*` choices whose `requires` hold (keys `c0`, `c1`, …) plus, if the scene is
     a `@hub tag`, `all(a,b)` or `any(a,b)`, eligible other storylets matching that query
     (keys `storylet:<scene-id>`). Filter requirements/once/self first, sort by descending priority
     then declaration order and apply optional positive `@hub-limit` to generated choices only.
   - If that's empty: the **first matching divert** `->` becomes a **Continue**. If that divert is `-> END`, the
     story ends right here and this scene is the final frame.

**Choosing** (`Choose(key)`):

1. Append `ChoiceMade`.
2. Run the choice's effects.
3. If the choice has a skill check, resolve it (`DiceRolled` + `SkillCheckResolved`) and run the success or
   failure effects.
4. `AfterChanges()` (triggers, leveling).
5. Collect notes for everything that happened. They are shown at the top of the _next_ frame.
6. Go to the choice's (or outcome's) target. **If none was given, fall through** to the current scene's first
   matching `->`, evaluated _after_ the effects. This lets an author write
   `* [Bribe him] { var:gold -= 5 }` and let a single `-> market` below handle every choice.

Invalid keys throw `InvalidOperationException` listing the valid ones. The UI only ever sends keys it was given.

**Determinism.** Given the same `StoryDefinition`, seed, snapshotted character selection and sequence of
keys, a session produces the same events, frames and ending. Saves store those inputs plus cursor,
format/story versions and source/extension fingerprint. Unknown/corrupt/incompatible saves are rejected,
not silently migrated. Older fixed-protagonist v1 saves may omit the optional character field.

---

## 6. Extension points

This is how the "dating sim doesn't want strength" requirement is met, and where a community author plugs in.

| I want to…                                        | Today (no code, story.talewright.yaml)                                                              | With C# (Tier 1, polished in M2)                                           |
| ------------------------------------------------- | ---------------------------------------------------------------------------------------- | -------------------------------------------------------------------------- |
| Remove default stats                              | `ruleset: { base: generic, disable: [str, con] }`                                        | `GenericRuleset.Create().ToBuilder().RemoveStat(...)`                      |
| Start from nothing                                | `ruleset: { base: none }`                                                                | `new RulesetBuilder("mine")`                                               |
| Add stats (affection, sanity…)                    | `ruleset: { stats: [ { id: affection, name: Affection, category: custom, max: 100 } ] }` | `builder.AddStat(new StatDefinition(...))`                                 |
| Change how checks work (2d6, percentile, no dice) | pick a ruleset that has the rule                                                         | implement `ISkillCheckRule`, `WithSkillCheckRule`                          |
| Change levelling                                  | —                                                                                        | implement `ILevelingRule`                                                  |
| Derived stats, conditions, auras                  | items with `modifiers`                                                                   | implement `IStatModifier`, `AddModifier`                                   |
| Rename reputation tiers                           | —                                                                                        | `WithTiers(...)`                                                           |
| Complex conditions                                | —                                                                                        | `NamedPredicateRegistry.Register("IsMidnight", s => …)`, use `@IsMidnight` |
| New text modifier (`#shout`)                      | —                                                                                        | `TextModifierRegistry.Register(new DelegateTextModifier("shout", …))`      |
| Other language / possessive style                 | —                                                                                        | implement `IGrammar`, pass to `StorySession`                               |
| Different story source (database, HTTP)           | —                                                                                        | implement `IStoryLibrary` and register it in the host                      |
| Different UI (console, Unity, MAUI)               | —                                                                                        | consume `IStorySession` + `RenderedDocument`; ignore `Talewright.Player`   |

The C# column already works if you reference the projects and pass a custom `CompilerOptions`
(`Rulesets`, `Predicates`, `Modifiers`); what M2 adds is packaging (NuGet) and a template project.

Things that are deliberately **not** extension points yet: the `.tale` syntax itself, the event set (adding an
event means touching `GameState.Apply`), and save formats.

---

## 7. Where to find X

| Question                                   | Look in                                                                                                       |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| Where does a stat's value come from?       | `GameState.GetStat` (base + `IStatModifier`s + held items, clamped)                                           |
| Why did a skill check pass/fail?           | `D20SkillCheckRule.Resolve` (generic) and `SkillCheckResolver.Resolve`; the note text is from `EventNarrator` |
| Why was a choice not offered?              | its `requires` (`ChoiceDefinition.Requires`), evaluated in `StorySession` when building choices               |
| Why isn't a storylet showing in a hub?     | `StoryletSelector.IsEligible`: tag, `requires`, `@once` + visit count                                         |
| Why did a trigger fire / not fire?         | `TriggerEngine.Run` (edge-triggered, `MaxPasses`)                                                             |
| What can `story.talewright.yaml` contain?             | `StoryYamlReader` and the [Talemark syntax reference](../reference/talemark-syntax.md#1-storytalewrightyaml)                                                                       |
| How is a `.tale` line classified?          | `TaleParser` (line kinds), the [Talemark syntax reference](../reference/talemark-syntax.md)                                                                     |
| How does `{…}` inside prose become a node? | `InlineParser` → `ExpressionParser.ParseSubstitution`                                                         |
| What does error TWxxxx mean?               | the [diagnostics reference](../reference/diagnostics.md) and `grep -rn TWxxxx src/`                                                                  |
| Where do "Gained: X" lines come from?      | `EventNarrator.Describe`                                                                                      |
| Where is the HTML?                         | `Talewright.Player/Components/*.razor`                                                                        |
| Where are the styles?                      | `Talewright.Player/wwwroot/talewright.css`                                                                    |
| How do bundled stories get into the app?   | `EmbeddedResource` in the host `.csproj` + `EmbeddedStoryLibrary`                                             |
| How does the desktop app take a path?      | `Talewright.Host.Desktop/Program.cs`                                                                          |
| The default stats/skills list              | `GenericRuleset.Create` and `GenericRuleset.Skills`                                                           |

---

## 8. Testing

One xUnit test project per engine project, under `tests/` (the generic ruleset is tested in `Talewright.Core.Tests`):

| Project                      | What it covers                                                                                                                                                                              |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Talewright.Core.Tests`      | initial state, clamping, item modifiers, no-op changes, replay/truncate, dice, removing/replacing stats, tiers, d20 checks, leveling                                                        |
| `Talewright.Rules.Tests`     | expression evaluation, effects, triggers (once, edge, pass cap), storylet selection, checks                                                                                                 |
| `Talewright.Narrative.Tests` | substitutions and modifier chains, Chicago possessives, a/an, inline/block conditionals, styles, speech                                                                                     |
| `Talewright.Tale.Tests`      | compiler: every construct, every diagnostic family, zip loading                                                                                                                             |
| `Talewright.Runtime.Tests`   | session loop, deterministic shared player fixtures and transport parity, and sample compilation smoke |
| `Talewright.Cli.Tests`       | validation output/exit policies, bounded publication and archive contracts |
| `Talewright.Player.E2E.Tests` | one C#/xUnit suite: production Release web host in Chromium, Firefox, and WebKit plus real Linux Photino; opt-in, outside the root solution |

The player has one C#/xUnit authoring surface with shared assertions behind browser and Linux drivers.
Playwright handles browsers; Python is only a thin AT-SPI command adapter for actual Linux Photino,
not a second test suite. Both reuse C#-defined story fixtures from `engine/tests/Talewright.TestStories`; generated ZIPs and folders are not
checked in or shipped. Sample-specific routes are deliberately not the detailed UI test contract.
See [testing guidance](../../tests/README.md) for the per-host coverage matrix, native prerequisites, commands,
failure evidence, and the distinction between Playwright WebKit and system webviews.

Run fast engine tests with `dotnet test` from the repo root. Player UI suites are explicit:
`python3 player/tests/run_e2e.py web` and `python3 player/tests/run_e2e.py linux`.

---

## 9. Known limitations and seams left for later

- **No save migration or autosave.** Manual slots reject mismatched content/extensions/version;
  migration is M8, autosave is a separate follow-up.
- **Bounded creation only.** Author-approved values/presets, not unrestricted point-buy or custom
  player-entered pronoun sets. Reopening a creation story requires completing creation before loading a slot.
- **Media.** `MediaTemplate`/`RenderedMedia` exist and flow through every layer, but the Player renders nothing.
  Images/sprites slot in at `InlineView.razor` and a future media resolver (M4: Presentation).
- **Open world.** No locations/time yet, but nothing assumes linear flow: scenes request moves, hubs select
  storylets by state, and movement is an event. Locations will be another kind of storylet source.
- **One language.** `EnglishGrammar` only; the `IGrammar` seam is there.
- **Event log growth.** No snapshots yet; histories are bounded to 10,000 choices. Generated load/replay
  workloads have explicit Release budgets; see [testing](../../tests/README.md). Real complex stories and
  constrained devices may need further profiling before snapshots or a precompiled bundle format.
- **Development hosts embed samples.** `publish -a` instead includes only the selected story.
- **Desktop platform matrix.** Universal packages contain six self-contained OS/CPU builds. Linux x64
  has actual native runtime tests; Windows/macOS/ARM64 builds require separate execution checks.
  Packages need OS webviews and are not signed/notarized installers or macOS app bundles.
