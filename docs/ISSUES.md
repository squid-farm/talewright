# Issues

One board for everything that's wrong or missing. There are two kinds of entry:

- **Bugs (`BUG-nnn`):** the engine does something it shouldn't, such as a crash, wrong behaviour, or an unsafe
  input.
- **Feature requests and shortcomings (`FR-nnn`):** the engine works as designed, but the design falls short.
  For example, something you need is missing, is awkward to author, or is limited.

If you're unsure which it is, file it as an FR; it can be moved later.

**Rules for both kinds:**

- Add new entries at the bottom of the relevant index and as a new section under it. Never reuse an id.
- When an entry is resolved, update its status (`fixed (<commit>)`, `done (<commit>)`, `won't fix (<reason>)`,
  …) instead of deleting it, so the history stays readable.
- **Source** records who raised it, e.g. `review session 2026-10-20` or `Mara`.
- **Effort:** XS < 30 min, S < 2 h, M < 1 day, L > 1 day (including tests and docs).

## Contents

- [Bugs](#bugs)
- [Feature requests and shortcomings](#feature-requests-and-shortcomings)
- [Templates](#templates)

---

## Bugs

**Severity**

| Level  | Meaning                                                                                                |
| ------ | ------------------------------------------------------------------------------------------------------ |
| **S1** | Crash, data loss, or a story that compiles cleanly but can't be played. Fix before the next milestone. |
| **S2** | Wrong behaviour an author or player can reasonably hit, or a safety issue with a realistic trigger.    |
| **S3** | Edge case, hardening, or a defensive fix. It only shows up with unusual input or misuse.               |

**Status:** `open`, `triaged`, `in progress`, `fixed (<commit>)`, `won't fix (<reason>)`, `deferred (<milestone>)`.
Where the original reviewer rated an entry differently, their rating is kept in brackets.

### Bug index

Ranked by priority: severity first, then how easy the bug is to hit, then the cost of fixing it.

| Id                  | Sev | Status | Area        | Summary                                                               | Effort |
| ------------------- | --- | ------ | ----------- | --------------------------------------------------------------------- | ------ |
| [BUG-001](#bug-001) | S1  | fixed (`c61a79f`)  | Tale / yaml | A stat with `min > max` compiles, then crashes when the story starts  | XS     |
| [BUG-002](#bug-002) | S2  | fixed (`e0b7e7d`)  | Runtime     | Triggers that depend on `level` are not re-evaluated after a level-up | XS     |
| [BUG-003](#bug-003) | S2  | fixed (`a0bfecb`)  | Tale / yaml | Duplicate trigger ids compile and silently suppress each other        | XS     |
| [BUG-004](#bug-004) | S2  | fixed (`14e2d37`)  | Tale / zip  | Zip loading has no limit on decompressed size (zip bomb)              | S      |
| [BUG-005](#bug-005) | S3  | fixed (`e0b7e7d`)  | Rules       | The trigger cascade stops silently at 8 passes                        | S      |
| [BUG-006](#bug-006) | S3  | deferred (M2)      | Rules       | Integer overflow in effects and arithmetic wraps instead of clamping  | S      |
| [BUG-007](#bug-007) | S3  | fixed (`124f14e`)  | Core        | `Ruleset` collections can be mutated by casting them back             | XS     |
| [BUG-008](#bug-008) | S3  | deferred (M2)      | Tale        | Commented-out `set flag:` / `var:` assignments still auto-declare     | S      |

---

### BUG-001

**A stat with `min > max` compiles, then crashes when the story starts.**

- **Severity:** S1 _[reviewer: P1]_
- **Status:** fixed (`c61a79f`)
- **Area / location:** `src/Talewright.Tale/Parsing/StoryYamlReader.cs` (custom `ruleset.stats` reading, around
  line 176); the crash is in `GameState.CreateInitial` → `StatDefinition.Clamp` → `Math.Clamp`.
- **Source:** review session 2026-10-20
- **What happens:** `stats: [ { id: love, min: 10, max: 0 } ]` produces no diagnostic. The story compiles and
  appears in the library. Pressing play throws `ArgumentException` from `Math.Clamp`, and the user sees Blazor's
  generic error banner instead of a diagnostic.
- **Why it matters:** this breaks the compiler's promise that a clean compile means a playable story. It is easy
  to hit with a typo.
- **Resolution:** the YAML reader reports `TW4003` on the `min` node and skips the invalid stat. A compiler
  regression test covers the diagnostic.
- **Effort:** XS

### BUG-002

**Triggers that depend on `level` are not re-evaluated after a level-up.**

- **Severity:** S2 _[reviewer: P1]_
- **Status:** fixed (`e0b7e7d`)
- **Area / location:** `src/Talewright.Runtime/StorySession.cs`, `AfterChanges()` (around line 248).
- **Source:** review session 2026-10-20
- **What happens:** `AfterChanges` runs triggers, then `CheckLevel`. A trigger such as `when: player:level >= 3`
  does not see the new level until the _next_ action. Its effects (and notes) then appear one step late, attached
  to an unrelated choice. If the level-up happens in the final scene, the trigger never fires at all. The reverse
  also applies: a trigger that grants XP can cause a level-up, which is handled, but a level-up that should then
  fire another trigger is not.
- **Resolution:** triggers and level checks now repeat until stable, bounded by 32 passes; failure to settle throws
  an explicit error. A runtime regression test verifies a level-dependent trigger fires on the same choice that
  grants the XP.
- **Effort:** XS

### BUG-003

**Duplicate trigger ids compile and silently suppress each other.**

- **Severity:** S2 _[reviewer: P1]_
- **Status:** fixed (`a0bfecb`)
- **Area / location:** `src/Talewright.Tale/Parsing/StoryYamlReader.cs`, `ReadTriggers` (around line 401);
  behaviour in `src/Talewright.Rules/Triggers/TriggerEngine.cs`.
- **Source:** review session 2026-10-20
- **What happens:** `TriggerEngine` keys both its edge memory and the `once` check (`TriggerFired` events) by id.
  With two triggers named the same, whichever runs first marks the id as true and the second never fires. Two
  `once` triggers with the same id also block each other.
- **Why it matters:** this is likely when copy-pasting triggers. It produces no error and the behaviour looks
  random.
- **Resolution:** the second occurrence reports `TW4007`; the code is documented in TALE-SYNTAX.md §7 and covered
  by a compiler regression test.
- **Effort:** XS

### BUG-004

**Zip loading has no limit on decompressed size (zip bomb).**

- **Severity:** S2 _[reviewer: P1]_
- **Status:** fixed (`14e2d37`)
- **Area / location:** `src/Talewright.Tale/Compilation/StorySource.cs`, `FromZip` (around line 67).
- **Source:** review session 2026-10-20
- **What happens:** every story-file entry is read fully into memory. The 20 MB upload limit in `TalewrightApp`
  applies to the _compressed_ bytes, so a small archive that expands to gigabytes can exhaust memory. On the web
  that crashes the tab; on desktop it can take down the app.
- **Why S2 rather than S1:** everything runs client-side, so the only victim is the person who opened the file. No
  server or other user is affected. It matters more once stories are shared in the community, because opening a
  story from a stranger should be safe.
- **Resolution:** `FromZip` counts actual bytes read and caps accepted story files at 1,000 entries, 5 MiB per
  file, and 50 MiB total. It throws a clear `InvalidDataException` on a limit breach; tests cover highly
  compressible input, aggregate expansion, and entry count. These limits apply to text files only; future media
  support should define separate type-specific limits.
- **Effort:** S

### BUG-005

**The trigger cascade stops silently at 8 passes.**

- **Severity:** S3 _[reviewer: P2]_
- **Status:** fixed (`e0b7e7d`)
- **Area / location:** `src/Talewright.Rules/Triggers/TriggerEngine.cs`, `Run` (around line 54).
- **Source:** review session 2026-10-20
- **What happens:** `Run` stopped after `MaxPasses = 8` even if triggers were still becoming true. Their effects
  were skipped until some later change happened to call `Run` again, without reporting that the cap was hit.
- **Why S3:** it needs a chain of more than 8 dependent triggers, or a ping-pong loop, which is unusual in VN-sized
  stories. But when it does happen it is very hard to debug.
- **Resolution:** the limit is now 32 passes. A final read-only eligibility pass detects work remaining beyond
  the limit and throws an `InvalidOperationException` naming fired and still-eligible triggers. Tests cover a
  32-pass chain and a cascade that exceeds the limit.

- **Effort:** S

### BUG-006

**Integer overflow in effects and arithmetic wraps instead of clamping.**

- **Severity:** S3 _[reviewer: P2]_
- **Status:** deferred (M2)
- **Area / location:** `src/Talewright.Rules/Effects/Effects.cs`, `Apply` (around line 66);
  `src/Talewright.Rules/Expressions/ExpressionEvaluator.cs` (`+`, `-`, `*` on `int`).
- **Source:** review session 2026-10-20
- **What happens:** `player:hp += 2147483647` wraps to a negative number, which is then clamped to the stat's
  _minimum_. Variables and faction standings are unbounded, so they keep the wrapped value.
- **Why S3:** it needs absurd numbers. The realistic risk is a runaway `var:x *= …` style loop in a long open-world
  game, or the default `max: int.MaxValue` on custom stats plus a large XP grant.
- **Proposed fix:** use saturating arithmetic: compute in `long`, then clamp to `int` range. Do this in
  `Effects.Apply` and the evaluator's `+ - *`. This is preferred over `checked`, which would crash at play time.
  Optionally, the compiler could warn on literal constants that exceed range.
- **Effort:** S
- **Deferral:** revisit during M2; the reported extreme-value scenario is not needed for the M1 core play loop.

### BUG-007

**`Ruleset` collections can be mutated by casting them back.**

- **Severity:** S3 _[reviewer: P2]_
- **Status:** fixed (`124f14e`)
- **Area / location:** `src/Talewright.Core/Rulesets/Ruleset.cs`, constructor (lines 24–26).
- **Source:** review session 2026-10-20
- **What happens:** `Stats`, `Tiers` and `Modifiers` are arrays and `StatsById` is a `Dictionary`, exposed as
  read-only interfaces. Code that casts them back (`(StatDefinition[])ruleset.Stats`) can modify a ruleset shared
  by running games, and `Stats` and `StatsById` can drift apart.
- **Why S3:** only C# extension code could do this, and only deliberately. No story can. It is worth fixing before
  the NuGet packages are public (M2), because the docs promise rulesets are immutable.
- **Resolution:** the list properties now expose read-only copies, and `StatsById` uses a `FrozenDictionary`.
  Tests verify that casting to collection interfaces does not permit mutation.
- **Effort:** XS

### BUG-008

**Commented-out `set flag:` / `var:` assignments still declare the flag or variable.**

- **Severity:** S3
- **Status:** deferred (M2)
- **Area / location:** `src/Talewright.Tale/Compilation/StoryCompiler.cs`, `WithAutoDeclarations` and the
  `FlagAssignment` / `VariableAssignment` regexes.
- **Source:** Project Lead (review comment on the pre-scan regexes); verified 2026-10-20
- **What happens:** flags and variables are auto-declared by a regex pre-scan of the raw `.tale` text. The regex
  doesn't know about `//` comments (or prose), so:

  ```
  === a
  // ~ set flag:foo        <- commented out
  {if flag:foo}yes{/if}
  ```

  compiles cleanly. Remove the comment line and the same file correctly fails with `TW2004` (flag never set). Net
  effect: a typo-guard is silently disabled whenever the only "assignment" is in a comment or in prose that
  happens to read `set flag:x`.

- **Why S3:** the story still runs; the flag is just always false. You lose a compile-time warning, not
  correctness.
- **Proposed fix:** stop pre-scanning with regex. Parse every file to its syntax tree first (scene headers,
  effects), collect declarations from the parsed effects, then bind/type-check. That is the standard two-pass
  compiler shape and also removes the `SceneHeader` regex. Cheaper interim fix: strip `//` comment lines before
  running the regexes.
- **Effort:** S (interim) / M (two-pass)
- **Deferral:** revisit with the M2 compiler integration; the complete parsed two-pass fix is broader than this
  pre-M2 correctness pass, and the comment-strip workaround would leave prose false positives.

---

## Feature requests and shortcomings

**Priority**

| Level      | Meaning                                                                                  |
| ---------- | ---------------------------------------------------------------------------------------- |
| **High**   | Blocks a story you actually want to write, or would be embarrassing in a public release. |
| **Medium** | Would clearly help, and there is a workaround in the meantime.                           |
| **Low**    | Nice to have, polish, or an idea worth remembering.                                      |

**Status:** `idea`, `accepted`, `planned (<milestone>)`, `in progress`, `done (<commit>)`, `declined (<reason>)`.
**Target** is the milestone it's aimed at (see [PLAN.md](PLAN.md) §7), or `unscheduled`.

### Feature request index

| Id                | Pri    | Status      | Target      | Area               | Summary                                                           | Effort |
| ----------------- | ------ | ----------- | ----------- | ------------------ | ----------------------------------------------------------------- | ------ |
| [FR-001](#fr-001) | Medium | idea        | unscheduled | Player             | Character sheet shows only the player, not NPCs                   | S      |
| [FR-002](#fr-002) | Medium | idea        | unscheduled | Core / Rules       | No NPC↔NPC relationships; faction standing is always the player's | M      |
| [FR-003](#fr-003) | Medium | shortcoming | unscheduled | Authoring          | Large stories need `story.yaml` split across files/folders        | TBD    |
| [FR-004](#fr-004) | Low    | idea        | M2          | Hosts              | Window/tab title should follow the loaded story                   | S      |
| [FR-005](#fr-005) | Medium | idea        | unscheduled | Player / hosts     | Theming: story/host stylesheets, overrides, SCSS                  | M      |
| [FR-006](#fr-006) | Low    | idea        | unscheduled | Hosts              | Custom JavaScript and extension of default scripts                | M      |
| [FR-007](#fr-007) | Medium | idea        | M2          | Hosts / packaging  | Publish without the bundled sample stories                        | S      |
| [FR-008](#fr-008) | Low    | deferred    | unscheduled | Narrative          | Use libraries (AvsAn, Humanizer) for English grammar              | S–M    |
| [FR-009](#fr-009) | Low    | idea        | unscheduled | Player / Core      | Character sheet shows active modifiers and their sources          | S      |
| [FR-010](#fr-010) | Medium | idea        | unscheduled | Player             | Authors can switch off sheet sections (inventory, factions, …)    | S      |
| [FR-011](#fr-011) | Medium | idea        | unscheduled | Player / syntax    | Customizable end screen: text, credits, links                     | M      |
| [FR-012](#fr-012) | Medium | idea        | unscheduled | Rulesets           | Skill model where attributes seed skills instead of always adding | S      |
| [FR-013](#fr-013) | Low    | idea        | M2          | Runtime            | Refactor: split `StorySession` responsibilities                   | M      |
| [FR-014](#fr-014) | Low    | idea        | with FR-003 | Tale               | Refactor: split `StoryYamlReader` along its seams                 | S–M    |
| [FR-015](#fr-015) | Medium | idea        | unscheduled | Syntax / Player    | Author control of transcript vs. clear; conversation flow         | S–M    |
| [FR-016](#fr-016) | Low    | idea        | unscheduled | Narrative / Player | Per-character speech colours, changeable during play              | S–M    |
| [FR-017](#fr-017) | Medium | idea        | unscheduled | Runtime / Player   | Option to hide notes (rolls, stat changes)                        | S      |
| [FR-018](#fr-018) | Low    | idea        | M2+         | Tale / publish     | Ship a precompiled story bundle instead of parsing at runtime     | M      |
| [FR-019](#fr-019) | Medium | idea        | M2          | Runtime / Player   | Global back/forward buttons (roll state back and forward)         | M      |

---

### FR-001

**The character sheet shows only the player, not NPCs.**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** `src/Talewright.Player/Components/CharacterSheet.razor`
- **Source:** M1 review Q&A, 2026-10-20
- **Shortcoming:** every character has stats (e.g. `guide:love`), but the sheet only displays `player`. Players
  only learn about NPC stats from change notes such as "Tomas: Affection +1". For a dating sim, seeing relationship
  stats is often the whole point.
- **Ideas:** a per-character tab or panel. Each NPC needs an author-controlled visibility setting, such as `shown`,
  `hidden`, or `after flag:met-tomas`, so the sheet doesn't spoil hidden values. Each NPC stat probably needs a
  visibility switch too.

### FR-002

**There are no NPC↔NPC relationships, and faction standing is always the player's.**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** Core (`GameState`), Rules (expressions/effects), Tale syntax
- **Source:** M1 review Q&A, 2026-10-20
- **Shortcoming:** `faction:x` stores how faction _x_ feels about the player. A per-person faction such as
  `faction:tomas` works for "Tomas likes the player". There is no way to express "Tomas dislikes Mara" or "the
  Guild is at war with the Crown" without inventing variables (`var:tomas-mara`), which can't be tiered or
  displayed.
- **Ideas:** a relationship store keyed by (from, to), with syntax like `rel:tomas>mara += 1`, reusing the
  reputation tiers. It matters more for open-world (factions acting on each other).

### FR-003

**Large stories may have extremely large yaml files that become unwieldy and brittle**

- **Priority:** Medium
- **Status:** shortcoming
- **Target:** unscheduled
- **Area:** Authoring
- **Source:** Project Lead
- **Shortcoming:** Imagine a story with 300 characters, 20 factions, and 600 items
- **Ideas:** We should offer the users the ability to create separate yaml files (folder based would be nice). Imagine a "characters" folder that gets automatically read by convention. Imagine an "items" folder. You could separate your items into subfolder "starting-gear", "late-game", or even just put each item into its own yaml file. Would also be great if you could specify your own path (folder only) to allow for organization in the way that makes sense to the author
- **Effort:** TBD

### FR-004

**The window/tab title is always "Talewright"; it should follow the loaded story.**

- **Priority:** Low
- **Status:** idea
- **Target:** M2 (with `talewright publish`)
- **Area:** Hosts (`Talewright.Host.Desktop/Program.cs`, both `wwwroot/index.html`), Player
- **Source:** Project Lead (review comments on desktop `SetTitle` and both `index.html` titles)
- **Shortcoming:** the desktop window title is hard-coded in `Program.cs` and the web `<title>` in
  `index.html`. Nothing in `story.yaml` can change it. A published game should show its own name.
- **Ideas:** default to the story `title:` from `story.yaml`, with an optional `window-title:` override. The
  Player raises a "story started" callback; the desktop host calls Photino's `SetTitle`, and the web host sets
  `document.title` (Blazor `<PageTitle>` + `HeadOutlet`, no JS needed). When the user returns to the library, the
  title reverts to the host default.
- **Effort:** S

### FR-005

**Theming: story- and host-supplied stylesheets, overrides, and composable styles.**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** Player (`wwwroot/talewright.css`), hosts, `story.yaml`
- **Source:** Project Lead (review comments on both `index.html` stylesheet links and on `CharacterSheet.razor`)
- **Shortcoming:** today you can restyle by editing the host's `index.html` to add a stylesheet after
  `talewright.css` that overrides the `--tw-*` CSS variables or `.tw-*` classes. That works, but it means editing
  engine files, and a story can't carry its own look.
- **What already exists (for reference):** colours and fonts are CSS custom properties (`--tw-bg`,
  `--tw-accent`, `--tw-font`, …) with a dark-mode variant; every element has a stable `tw-` class. For a radical
  change, you don't need to touch the engine: the Player components are just one consumer of
  `StorySession`/`StoryFrame` (plain records, no HTML), so a host can swap `TalewrightApp`/`StoryPlayer` for its
  own Razor components.
- **Ideas:**
  - `theme: theme.css` (or a `theme/` folder) in `story.yaml`, loaded after `talewright.css`. Published games bake
    it in.
  - Document the variable list and class names as a supported, versioned theming API.
  - SCSS: keep the _shipped_ output as plain CSS (no build step for authors). Optionally author the engine's own
    styles in SCSS via a .NET Sass compiler (e.g. `AspNetCore.SassCompiler`) at build time, and let `talewright
publish` compile a story's `.scss` if present.
  - Named theme presets (parchment, terminal, dating-sim pastel) as plain CSS files.
- **Effort:** M

### FR-006

**Custom JavaScript and extension of the default scripts.**

- **Priority:** Low
- **Status:** idea
- **Target:** unscheduled
- **Area:** Hosts (`index.html`), Player
- **Source:** Project Lead (review comment on desktop `index.html` script tag)
- **Shortcoming:** there's no supported way for a story or host to add scripts, e.g. analytics on itch.io,
  custom audio, or a JS-based visual effect.
- **Ideas:** a `scripts:` list in `story.yaml` injected by the host, plus Blazor JS initializers
  (`*.lib.module.js`, `beforeWebStart`/`afterWebStarted`), which are the supported hook for extending Blazor's
  startup. Keep C# the primary extension path; JS is for browser-only concerns. Needs a security note: a story
  with scripts is code, not data.
- **Effort:** M

### FR-007

**Publish without the bundled sample stories.**

- **Priority:** Medium
- **Status:** idea
- **Target:** M2 (with `talewright publish`)
- **Area:** Hosts (`.csproj` `EmbeddedResource` of `samples/**`), Player NuGet packaging
- **Source:** Project Lead (review comment on `Talewright.Host.Web/Program.cs`)
- **Shortcoming:** both hosts embed everything under `samples/`. Fine for the dev build, but a game built from
  these hosts (or from future NuGet packages) would ship the samples too, and they grow as sample coverage grows.
- **Ideas:** move the sample embedding behind an MSBuild property (`<TalewrightIncludeSamples>`), on by default
  for the dev hosts and off for `talewright publish`. Make sure the Player RCL/NuGet packages never contain
  samples. Covers PLAN §9 open question 3.
- **Effort:** S

### FR-008

**Use established libraries for English grammar instead of hand-rolled rules.**

- **Priority:** Low
- **Status:** deferred (Project Lead, 2026-10-20: keep the in-house rules and see how far they go; revisit if grammar edge cases start hurting)
- **Target:** unscheduled
- **Area:** `src/Talewright.Narrative/Grammar/EnglishGrammar.cs`
- **Source:** Project Lead (review comment on `EnglishGrammar`)
- **Shortcoming:** `#a` (a/an) uses a short exception list, so it gets things like "an FBI agent", "a URL",
  "an hour" vs "a historian" only partly right. There's no plural, ordinal or number-to-words support.
- **Ideas:**
  - **a/an:** the `AvsAn` NuGet package decides from real usage data (Wikipedia corpus) and handles acronyms and
    odd words. It is small and pure C#. Swap it in behind `IGrammar.WithIndefiniteArticle`. Check the licence
    before adopting.
  - **Plurals, ordinals, numbers to words:** `Humanizer` (MIT, supports .NET 10/WASM). Use it to add `#plural`,
    `#ordinal` (3rd), `#words` (three) modifiers. Use the English-only `Humanizer.Core` package to keep the WASM
    download small.
  - **Possessives:** no library does this well; the Chicago rule we implement is the whole rule. Keep ours.
  - Keep `IGrammar` as the seam so localization can plug in other languages later.
- **Effort:** S (a/an swap) / M (new modifiers + docs)

### FR-009

**The character sheet should show active modifiers and where they come from.**

- **Priority:** Low
- **Status:** idea
- **Target:** unscheduled
- **Area:** `src/Talewright.Player/Components/CharacterSheet.razor`, Core (`GameState` modifier query)
- **Source:** Project Lead (review comment on `CharacterSheet.razor`)
- **Shortcoming:** the sheet shows "14 (+2)" and a hover tooltip "modified by items or rules", but not _which_
  item or rule. With several modifiers stacking, players can't tell why a number changed.
- **Ideas:** expose the modifier breakdown from Core (`GetStatBreakdown(who, stat)` → base + list of
  `(source, amount)`). Show it as an expandable row or tooltip ("+2 jade charm"). The same data is useful for
  skill-check notes ("Perception +3 (WIS +1, charm +2)").
- **Effort:** S

### FR-010

**Let authors switch off character sheet sections (inventory, factions, skills, even the whole sheet).**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** Player (`CharacterSheet.razor`, `StoryPlayer.razor`), `story.yaml`
- **Source:** Project Lead (review comment on the inventory section of `CharacterSheet.razor`)
- **Shortcoming:** the sheet already hides stat categories that don't exist (the `none` ruleset has no stats) and
  skills at 0. It _always_ shows Inventory ("Nothing.") and Factions when any exist. A dating sim or a light
  narrative piece may want no inventory panel, or no sheet at all.
- **Ideas:** a `player-ui:` block in `story.yaml`, e.g.
  `sheet: { inventory: false, factions: false, stats: [love, trust] }` or `sheet: off`. Sections could also hide
  automatically when a story declares no items or factions. This is part of the "turn defaults off" goal.
- **Effort:** S

### FR-011

**Customizable end screen: text, credits, links.**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** Player (`StoryPlayer.razor` "The End" block), Tale syntax / `story.yaml`
- **Source:** Project Lead (review comment on `StoryPlayer.razor`)
- **Shortcoming:** every ending shows a fixed "The End" plus "Play again". You can put text in the final scene,
  but you can't replace the end banner, add credits or links, or vary it per ending without hacks.
- **Ideas:**
  - `ending:` metadata on a scene (`@ending "The Keeper"` with a title and optional `@credits` reference).
  - A `credits.tale` scene type rendered after the final frame.
  - `story.yaml` `end-screen:` with links (itch page, Patreon, sequel) and whether to show "Play again".
  - It could also record which endings the player has seen (needs persistence, M2+).
- **Effort:** M

### FR-012

**Skill progression independent of attributes (attribute seeds the skill, doesn't keep adding to it).**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** `src/Talewright.Rulesets.Generic/GenericRuleset.cs` (`D20SkillCheckRule`), ruleset config
- **Source:** Project Lead (review comment on `GenericRuleset` skills)
- **How it works today:** skills _are_ stored independently: each is its own stat (rank 0–10), and raising
  Wisdom never changes your Survival rank. But the d20 check adds both: `d20 + skill rank + attribute modifier`
  (5e style). So raising Wisdom does make Survival _checks_ easier.
- **Shortcoming:** the alternative model ("a wise scholar starts with better Survival, but training Wisdom later
  doesn't make you a better tracker") isn't available without writing a C# `ISkillCheckRule`.
- **Ideas:** a ruleset option `skill-checks: { attribute-bonus: always | starting-only | none }`. With
  `starting-only`, the attribute modifier is added once to the skill's starting rank at character creation and not
  at check time. Per-skill overrides would also be possible. This is data-only, so it fits the "custom rules need
  no C#" goal.
- **Effort:** S

### FR-013

**Refactor: `StorySession` carries too many responsibilities.**

- **Priority:** Low
- **Status:** idea
- **Target:** before or with M2 save/load
- **Area:** `src/Talewright.Runtime/StorySession.cs` (~290 lines)
- **Source:** Project Lead (review comment: "feels too big, give it a good think")
- **Analysis:** it currently does six jobs:
  1. Public façade and lifecycle (`Start`, `Choose`, `Current`, seed, event log)
  2. Navigation: entering scenes, storylet selection, diverts (`Enter`, `ContinueTo`, `FirstMatchingDivert`)
  3. Building the choice list and its actions (`OfferChoices`)
  4. Resolving a choice: effects, skill checks (`MakeChoice`, `ResolveCheck`)
  5. The consequence pipeline after any change: triggers, levelling (`AfterChanges`)
  6. Presentation: turning events into notes and assembling the `StoryFrame` (`NotesSince`, `Present`,
     `EndStory`)
- **Proposed split** (no public API change; `StorySession` stays the façade and owner of the `EventLog`):
  - `ConsequencePipeline` (5): the natural home for the BUG-002 trigger/level loop and BUG-005's cascade limit.
  - `FrameBuilder` (6): the natural home for FR-017 (hide notes) and FR-015 (transcript control).
  - `SceneNavigator` (2–4): pure functions over state + story, easier to unit-test in isolation and the piece
    that open-world navigation (location graphs) would replace or extend.
- **When:** do it as the first step of whichever of BUG-002, FR-015 or FR-017 lands first, so the refactor is
  driven by a real change rather than done speculatively.
- **Effort:** M

### FR-014

**Refactor: split `StoryYamlReader` along its natural seams.**

- **Priority:** Low
- **Status:** idea
- **Target:** with FR-003
- **Area:** `src/Talewright.Tale/Parsing/StoryYamlReader.cs` (~490 lines)
- **Source:** Project Lead (review comment on the helpers section: "feels like a natural seam")
- **Analysis:** the file is one class reading every `story.yaml` section, plus generic YAML node helpers (`Get`,
  `Scalar`, `RequiredScalar`, location mapping) at the bottom.
- **Proposed split:** move the helpers to a `YamlNodeReader` (location-aware scalar/mapping access with
  diagnostics). Then have one small reader per section (`CharacterReader`, `ItemReader`, `FactionReader`,
  `RulesetReader`, `TriggerReader`) and a manifest reader that orchestrates. This is exactly the shape FR-003
  needs: per-folder `characters/*.yaml` files feed the same `CharacterReader`.
- **Effort:** S–M

### FR-015

**Author control over what stays on screen (transcript vs. clear), and conversation-style flow.**

- **Priority:** Medium
- **Status:** idea (needs discussion)
- **Target:** unscheduled
- **Area:** Tale syntax (scene metadata), `StoryFrame`, Player (`StoryPlayer.razor`)
- **Source:** Project Lead (review comment on `TaleParser.FlushParagraph`)
- **How it works today:** nothing is ever cleared. `FlushParagraph` only ends a paragraph inside the compiled
  template (a blank line in prose). The Player keeps a full transcript: every previous frame stays on screen,
  dimmed, and the new frame is appended below and focused. So a quest-giver conversation that spans several
  scenes already reads top-to-bottom like a conversation.
- **Shortcoming:** authors can't control it:
  - There's no way to clear the screen (chapter break, "three days later…", a game-over screen).
  - Each scene is a separate visual section with its own `@title` heading, so a conversation split across scenes
    looks more chopped up than it should.
  - The transcript grows without bound in a long or open-world game.
- **Ideas:**
  - `@clear` scene metadata (clear the transcript when entering), and `@continues` (render into the previous
    frame without a heading or separator, for conversations).
  - A per-story default (`presentation: transcript | page`). A visual-novel "page" mode shows one frame at a
    time, with a backlog/history button.
  - Carry this as frame metadata (`StoryFrame.Clear`, `StoryFrame.ContinuesPrevious`) so any presentation layer
    can honour it, and the engine stays UI-agnostic.
- **Effort:** S–M

### FR-016

**Per-character speech colours and styles, changeable during play.**

- **Priority:** Low
- **Status:** idea
- **Target:** unscheduled
- **Area:** `story.yaml` characters, Narrative (`RenderedSpeech`), Player CSS
- **Source:** Project Lead (review comment on `TaleParser.ParseSpeech`)
- **Shortcoming:** all speech looks the same. Visual novels commonly give each speaker a colour, and sometimes
  change it with mood (e.g. red text when enraged).
- **Ideas:**
  - Static: `speech: { color: "#a33", class: gruff }` per character in `story.yaml`, rendered as a CSS
    variable/class on the speech block.
  - Dynamic: make the style a function of state rather than a mutable setting, e.g.
    `speech-style: [ { when: flag:tomas-angry, class: angry }, { class: calm } ]`. That keeps it data-driven,
    replay-safe and consistent with how everything else is derived from events.
  - The renderer resolves it into `RenderedSpeech.Style` (a class name or token, not raw CSS) so non-HTML
    presentation layers can map it too.
- **Effort:** S (static) / M (dynamic)

### FR-017

**Option to hide notes (skill-check rolls, stat changes) for a more immersive experience.**

- **Priority:** Medium
- **Status:** idea
- **Target:** unscheduled
- **Area:** `src/Talewright.Runtime/StorySession.cs` (`NotesSince`), Player, `story.yaml`
- **Source:** Project Lead (review comment on `StorySession` notes)
- **Shortcoming:** every check shows "Perception check: rolled 14 + 3 = 17 vs 15, success", and every change
  shows "+10 XP" or "Tomas: Affection +1". Some stories want hidden mechanics.
- **Ideas:** have the engine keep producing notes (they're derived from events, so they cost nothing) and let
  filtering happen at presentation. Set it with `story.yaml` `notes: { checks: hidden | outcome-only | full,
stats: shown | hidden }`, plus an optional player-side toggle in the Player, the way many games offer
  "show dice rolls". Per-stat visibility could reuse FR-001's visibility setting, so hidden stats never leak
  through notes.
- **Effort:** S

### FR-018

**Ship a precompiled story bundle instead of parsing `.tale` sources at runtime.**

- **Priority:** Low
- **Status:** idea
- **Target:** M2+ (with `talewright publish`)
- **Area:** Tale (`StoryDefinition` serialization), Runtime loading, publish CLI
- **Source:** Project Lead (review comment on `Talewright.Tale/README.md`)
- **Shortcoming:** every launch compiles the story from source. That is fast for the sample (milliseconds), but
  large stories would pay the parse/validate cost on every start, especially in WASM. Shipping sources also
  makes a published game's script trivially readable (a mild concern).
- **Ideas:** `talewright publish` compiles once and writes a versioned bundle (System.Text.Json with source
  generation, which is trim/WASM-safe, or a compact binary format). The player loads the bundle directly and
  falls back to source for development. Named C# predicates (`@Name`) can't be serialized; the bundle stores
  their names and the host re-binds them from the story's C# assembly. PLAN §4 already anticipates this
  ("serialized to a compact bundle for publishing later"). Benchmark before committing to it: it's only worth it
  if real stories compile slowly.
- **Effort:** M

### FR-019

**Global back/forward buttons that roll the playthrough's state back and forward.**

- **Priority:** Medium
- **Status:** idea
- **Target:** M2 (alongside saves, which use the same mechanism)
- **Area:** Runtime (`StorySession` / a history wrapper), Player (`StoryPlayer.razor`), `story.yaml`
- **Source:** Project Lead, 2026-10-20
- **Shortcoming:** the only way to see what another option does is to restart and replay. Players who like to
  try every option before committing to a path can't step back one choice, look at the alternative, and step
  forward again. PLAN D9 says event sourcing "enables rewind", but no milestone schedules it.
- **Ideas:**
  - **Behaviour:** Back undoes the last choice and restores the previous frame, state, and transcript. Forward
    re-applies an undone choice. Making a different choice after going back clears the forward stack (normal
    undo/redo, not a branching tree).
  - **Authoring:** on by default. Authors can hide it with a top-level `undo: false` in `story.yaml`, for
    example in stories where choices are meant to be irreversible.
  - **Mechanism:** replay, not truncation. Rebuild a fresh `StorySession` with the same seed and the
    choice-key prefix (the determinism contract in ARCHITECTURE.md; the same "seed + keys" that M2 saves will
    store). `EventLog.Truncate` alone isn't enough, because `TriggerEngine` keeps its edge state
    (`_currentlyTrue`) outside the event log. Replay costs O(choices) per step, which is negligible for
    interactive fiction. A good home is a runtime-level history wrapper (`Choose`, `Back`, `Forward`,
    `CanBack`, `CanForward`, frames for the transcript), so it can be unit-tested without the UI.
  - **Dice:** rolls are a function of `(seed, roll index)`, so going back and retrying returns the same roll.
    That is intended: it keeps exploration honest and matches the anti-save-scumming stance of
    `DeterministicDice`.
  - **Possible extras:** keyboard shortcuts (e.g. Alt+←/→), and a frame showing which option was taken last time.
- **Effort:** M

---

## Templates

```markdown
### BUG-NNN

**One-line summary.**

- **Severity:** S1 / S2 / S3
- **Status:** open
- **Area / location:** project, file, and line or member
- **Source:** who found it
- **What happens:** steps or input, expected vs actual
- **Proposed fix:** (optional)
- **Effort:** XS / S / M / L (optional)
```

```markdown
### FR-NNN

**One-line summary of what's missing or awkward.**

- **Priority:** High / Medium / Low
- **Status:** idea
- **Target:** unscheduled
- **Area:** project, file, or "authoring" / "syntax"
- **Source:** who raised it
- **Shortcoming:** what you tried to do and why the engine made it hard or impossible
- **Ideas:** (optional) possible designs, workarounds in the meantime
- **Effort:** XS / S / M / L (optional)
```
