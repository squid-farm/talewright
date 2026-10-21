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
- **Source** records who raised it, e.g. `review session 2026-10-20` or `Joel`.
- **Effort:** XS < 30 min, S < 2 h, M < 1 day, L > 1 day (including tests and docs).

## Contents

- [Bugs](#bugs)
- [Feature requests and shortcomings](#feature-requests-and-shortcomings)
- [Templates](#templates)

---

## Bugs

**Severity**

| Level | Meaning |
|---|---|
| **S1** | Crash, data loss, or a story that compiles cleanly but can't be played. Fix before the next milestone. |
| **S2** | Wrong behaviour an author or player can reasonably hit, or a safety issue with a realistic trigger. |
| **S3** | Edge case, hardening, or a defensive fix. It only shows up with unusual input or misuse. |

**Status:** `open`, `triaged`, `in progress`, `fixed (<commit>)`, `won't fix (<reason>)`, `deferred (<milestone>)`.
Where the original reviewer rated an entry differently, their rating is kept in brackets.

### Bug index

Ranked by priority: severity first, then how easy the bug is to hit, then the cost of fixing it.

| Id | Sev | Status | Area | Summary | Effort |
|---|---|---|---|---|---|
| [BUG-001](#bug-001) | S1 | open | Tale / yaml | A stat with `min > max` compiles, then crashes when the story starts | XS |
| [BUG-002](#bug-002) | S2 | open | Runtime | Triggers that depend on `level` are not re-evaluated after a level-up | XS |
| [BUG-003](#bug-003) | S2 | open | Tale / yaml | Duplicate trigger ids compile and silently suppress each other | XS |
| [BUG-004](#bug-004) | S2 | open | Tale / zip | Zip loading has no limit on decompressed size (zip bomb) | S |
| [BUG-005](#bug-005) | S3 | open | Rules | The trigger cascade stops silently at 8 passes | S |
| [BUG-006](#bug-006) | S3 | open | Rules | Integer overflow in effects and arithmetic wraps instead of clamping | S |
| [BUG-007](#bug-007) | S3 | open | Core | `Ruleset` collections can be mutated by casting them back | XS |

---

### BUG-001

**A stat with `min > max` compiles, then crashes when the story starts.**

- **Severity:** S1 *[reviewer: P1]*
- **Status:** open
- **Area / location:** `src/Talewright.Tale/Parsing/StoryYamlReader.cs` (custom `ruleset.stats` reading, around
  line 176); the crash is in `GameState.CreateInitial` → `StatDefinition.Clamp` → `Math.Clamp`.
- **Source:** review session 2026-10-20
- **What happens:** `stats: [ { id: love, min: 10, max: 0 } ]` produces no diagnostic. The story compiles and
  appears in the library. Pressing play throws `ArgumentException` from `Math.Clamp`, and the user sees Blazor's
  generic error banner instead of a diagnostic.
- **Why it matters:** this breaks the compiler's promise that a clean compile means a playable story. It is easy
  to hit with a typo.
- **Proposed fix:** in the YAML reader, report `TW4003` ("min must not be greater than max") on the `min` node and
  skip the stat. Optionally also warn when `default` is outside the range (it is clamped today, so that is not a
  crash). Add a compiler test.
- **Effort:** XS

### BUG-002

**Triggers that depend on `level` are not re-evaluated after a level-up.**

- **Severity:** S2 *[reviewer: P1]*
- **Status:** open
- **Area / location:** `src/Talewright.Runtime/StorySession.cs`, `AfterChanges()` (around line 248).
- **Source:** review session 2026-10-20
- **What happens:** `AfterChanges` runs triggers, then `CheckLevel`. A trigger such as `when: player:level >= 3`
  does not see the new level until the *next* action. Its effects (and notes) then appear one step late, attached
  to an unrelated choice. If the level-up happens in the final scene, the trigger never fires at all. The reverse
  also applies: a trigger that grants XP can cause a level-up, which is handled, but a level-up that should then
  fire another trigger is not.
- **Proposed fix:** loop `triggers.Run` and `CheckLevel` until neither produces events, with a bounded iteration
  count (same idea as `TriggerEngine.MaxPasses`). Add a runtime test with an XP grant leading to a level trigger in
  the same choice.
- **Effort:** XS

### BUG-003

**Duplicate trigger ids compile and silently suppress each other.**

- **Severity:** S2 *[reviewer: P1]*
- **Status:** open
- **Area / location:** `src/Talewright.Tale/Parsing/StoryYamlReader.cs`, `ReadTriggers` (around line 401);
  behaviour in `src/Talewright.Rules/Triggers/TriggerEngine.cs`.
- **Source:** review session 2026-10-20
- **What happens:** `TriggerEngine` keys both its edge memory and the `once` check (`TriggerFired` events) by id.
  With two triggers named the same, whichever runs first marks the id as true and the second never fires. Two
  `once` triggers with the same id also block each other.
- **Why it matters:** this is likely when copy-pasting triggers. It produces no error and the behaviour looks
  random.
- **Proposed fix:** report an error (new code, e.g. `TW4007` duplicate trigger id) on the second occurrence. Add it
  to the diagnostics table in TALE-SYNTAX.md §7 and add a compiler test.
- **Effort:** XS

### BUG-004

**Zip loading has no limit on decompressed size (zip bomb).**

- **Severity:** S2 *[reviewer: P1]*
- **Status:** open
- **Area / location:** `src/Talewright.Tale/Compilation/StorySource.cs`, `FromZip` (around line 67).
- **Source:** review session 2026-10-20
- **What happens:** every story-file entry is read fully into memory. The 20 MB upload limit in `TalewrightApp`
  applies to the *compressed* bytes, so a small archive that expands to gigabytes can exhaust memory. On the web
  that crashes the tab; on desktop it can take down the app.
- **Why S2 rather than S1:** everything runs client-side, so the only victim is the person who opened the file. No
  server or other user is affected. It matters more once stories are shared in the community, because opening a
  story from a stranger should be safe.
- **Proposed fix:** add limits to `FromZip` (a parameter or `CompilerOptions`-style options), for example a
  maximum entry count, maximum bytes per file (e.g. 5 MB) and maximum total expanded bytes (e.g. 50 MB). Enforce
  them by counting bytes as they are read, not by trusting `entry.Length`. Throw `InvalidDataException` with a
  clear message; `TalewrightApp` already shows that error. Add a test with a highly compressible entry.
- **Effort:** S

### BUG-005

**The trigger cascade stops silently at 8 passes.**

- **Severity:** S3 *[reviewer: P2]*
- **Status:** open
- **Area / location:** `src/Talewright.Rules/Triggers/TriggerEngine.cs`, `Run` (around line 54).
- **Source:** review session 2026-10-20
- **What happens:** `Run` stops after `MaxPasses = 8` even if triggers are still becoming true. Their effects are
  skipped until some later change happens to call `Run` again. Nothing reports that the cap was hit.
- **Why S3:** it needs a chain of more than 8 dependent triggers, or a ping-pong loop, which is unusual in VN-sized
  stories. But when it does happen it is very hard to debug.
- **Proposed fix:** decide which behaviour is wanted:
  - **(a)** If the last allowed pass still fired something, throw a descriptive exception naming the triggers
    involved. That treats it as an authoring bug, matching how a ping-pong loop really is one.
  - **(b)** Keep running but emit a diagnostic event or note.

  Recommendation: (a), plus raising the cap (e.g. 32) so long legitimate chains work. Add a test for both the
  long-chain and the ping-pong cases.
- **Effort:** S

### BUG-006

**Integer overflow in effects and arithmetic wraps instead of clamping.**

- **Severity:** S3 *[reviewer: P2]*
- **Status:** open
- **Area / location:** `src/Talewright.Rules/Effects/Effects.cs`, `Apply` (around line 66);
  `src/Talewright.Rules/Expressions/ExpressionEvaluator.cs` (`+`, `-`, `*` on `int`).
- **Source:** review session 2026-10-20
- **What happens:** `player:hp += 2147483647` wraps to a negative number, which is then clamped to the stat's
  *minimum*. Variables and faction standings are unbounded, so they keep the wrapped value.
- **Why S3:** it needs absurd numbers. The realistic risk is a runaway `var:x *= …` style loop in a long open-world
  game, or the default `max: int.MaxValue` on custom stats plus a large XP grant.
- **Proposed fix:** use saturating arithmetic: compute in `long`, then clamp to `int` range. Do this in
  `Effects.Apply` and the evaluator's `+ - *`. This is preferred over `checked`, which would crash at play time.
  Optionally, the compiler could warn on literal constants that exceed range.
- **Effort:** S

### BUG-007

**`Ruleset` collections can be mutated by casting them back.**

- **Severity:** S3 *[reviewer: P2]*
- **Status:** open
- **Area / location:** `src/Talewright.Core/Rulesets/Ruleset.cs`, constructor (lines 24–26).
- **Source:** review session 2026-10-20
- **What happens:** `Stats`, `Tiers` and `Modifiers` are arrays and `StatsById` is a `Dictionary`, exposed as
  read-only interfaces. Code that casts them back (`(StatDefinition[])ruleset.Stats`) can modify a ruleset shared
  by running games, and `Stats` and `StatsById` can drift apart.
- **Why S3:** only C# extension code could do this, and only deliberately. No story can. It is worth fixing before
  the NuGet packages are public (M2), because the docs promise rulesets are immutable.
- **Proposed fix:** store `Array.AsReadOnly(...)`/`ReadOnlyCollection` and a `FrozenDictionary` (available in
  .NET 8+, and trim-safe). Add a test that the exposed collections can't be cast to a mutable type.
- **Effort:** XS

---

## Feature requests and shortcomings

**Priority**

| Level | Meaning |
|---|---|
| **High** | Blocks a story you actually want to write, or would be embarrassing in a public release. |
| **Medium** | Would clearly help, and there is a workaround in the meantime. |
| **Low** | Nice to have, polish, or an idea worth remembering. |

**Status:** `idea`, `accepted`, `planned (<milestone>)`, `in progress`, `done (<commit>)`, `declined (<reason>)`.
**Target** is the milestone it's aimed at (see [PLAN.md](PLAN.md) §7), or `unscheduled`.

### Feature request index

| Id | Pri | Status | Target | Area | Summary | Effort |
|---|---|---|---|---|---|---|
| [FR-001](#fr-001) | Medium | idea | unscheduled | Player | Character sheet shows only the player, not NPCs | S |
| [FR-002](#fr-002) | Medium | idea | unscheduled | Core / Rules | No NPC↔NPC relationships; faction standing is always the player's | M |

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
- **Shortcoming:** `faction:x` stores how faction *x* feels about the player. A per-person faction such as
  `faction:tomas` works for "Tomas likes the player". There is no way to express "Tomas dislikes Mara" or "the
  Guild is at war with the Crown" without inventing variables (`var:tomas-mara`), which can't be tiered or
  displayed.
- **Ideas:** a relationship store keyed by (from, to), with syntax like `rel:tomas>mara += 1`, reusing the
  reputation tiers. It matters more for open-world (factions acting on each other).

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
