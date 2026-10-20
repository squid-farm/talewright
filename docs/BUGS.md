# Bug tracker

Known defects and review findings. Add new entries at the bottom of the index and as a new section below it;
never reuse an id. When something is fixed, set its status to `fixed (<commit>)` rather than deleting it.

**Severity**

| Level | Meaning |
|---|---|
| **S1** | Crash, data loss, or a story that compiles cleanly but can't be played. Fix before the next milestone. |
| **S2** | Wrong behaviour an author or player can reasonably hit, or a safety issue with a realistic trigger. |
| **S3** | Edge case, hardening, or a defensive fix. It only shows up with unusual input or misuse. |

**Status:** `open`, `triaged`, `in progress`, `fixed (<commit>)`, `won't fix (<reason>)`, `deferred (<milestone>)`.

**Source:** who found it, e.g. `review session 2026-10-20` or `Joel`. The original reviewer's own rating is kept
in brackets where it differs.

## Index

Ranked by priority (severity, then how easy it is to hit, then cost of fixing).

| Id | Sev | Status | Area | Summary | Effort |
|---|---|---|---|---|---|
| [BUG-001](#bug-001) | S1 | open | Tale / yaml | A stat with `min > max` compiles, then crashes when the story starts | XS |
| [BUG-002](#bug-002) | S2 | open | Runtime | Triggers that depend on `level` are not re-evaluated after a level-up | XS |
| [BUG-003](#bug-003) | S2 | open | Tale / yaml | Duplicate trigger ids compile and silently suppress each other | XS |
| [BUG-004](#bug-004) | S2 | open | Tale / zip | Zip loading has no limit on decompressed size (zip bomb) | S |
| [BUG-005](#bug-005) | S3 | open | Rules | The trigger cascade stops silently at 8 passes | S |
| [BUG-006](#bug-006) | S3 | open | Rules | Integer overflow in effects and arithmetic wraps instead of clamping | S |
| [BUG-007](#bug-007) | S3 | open | Core | `Ruleset` collections can be mutated by casting them back | XS |

Effort: XS < 30 min, S < 2 h, M < 1 day, L > 1 day (fix plus test).

---

## BUG-001

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

## BUG-002

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

## BUG-003

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

## BUG-004

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

## BUG-005

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

## BUG-006

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

## BUG-007

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

## Template for new entries

```markdown
## BUG-NNN

**One-line summary.**

- **Severity:** S1 / S2 / S3
- **Status:** open
- **Area / location:** project, file, and line or member
- **Source:** who found it
- **What happens:** steps or input, expected vs actual
- **Proposed fix:** (optional)
- **Effort:** XS / S / M / L (optional)
```
