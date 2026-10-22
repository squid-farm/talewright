# Talewright.Runtime — the engine loop

Runs a compiled `StoryDefinition`. It is the only project that touches every layer: Core state, Rules
(effects, checks, triggers, storylets), Narrative (rendering) and the Tale model. The UI talks to it through one
small interface.

```csharp
IStorySession session = new StorySession(story, seed: 1234);
StoryFrame frame = session.Start();
while (!frame.IsEnded)
{
    Console.WriteLine(frame.Document.ToPlainText());
    frame = session.Choose(frame.Choices[0].Key);
}
```

## Files

| File | Contents |
|---|---|
| `IStorySession.cs` | `Start()`, `Choose(key)`, `Current`, `State` (`IGameStateView`), `Events`, `Seed`, `Story`. |
| `StorySession.cs` | The loop: entering scenes, building choices, resolving choices. The class doc comment is the spec. |
| `StoryFrame.cs` | `StoryFrame(Scene, Title, Document, Choices, IsEnded)` and `ChoiceView(Key, Label)` (`Label` is rendered inlines; `.Text` gives plain text). |
| `EventNarrator.cs` | Events → `RenderedNote`s ("Gained: rope", "Hit Points −3 (7)", "Athletics check: 10 +2 = 12 vs DC 12 — success", "Explorers' Guild −20 (now Unfriendly)", "Level 2!"). Other characters' changes are prefixed with their name. |

## The loop, briefly

Full description in [architecture §5](../../docs/developer/architecture.md#5-the-engine-loop-in-detail).

- **Enter scene:** `SceneEntered` → entry effects → triggers + leveling → render body → notes → entry check
  (offer Continue) *or* choices (explicit + hub storylets) *or* first matching divert as Continue (`-> END` ends
  the story on this frame).
- **Choose:** `ChoiceMade` → effects → check + outcome → triggers + leveling → go to target or fall through to the
  scene's first matching `->`. Notes about what changed appear at the top of the next frame.

## Choice keys

| Key | Meaning |
|---|---|
| `c0`, `c1`, … | explicit `*` choices, by position in the scene |
| `storylet:<scene-id>` | a hub storylet |
| `continue` (`StorySession.ContinueKey`) | the synthetic Continue after an entry check or before a divert |

Keys are stable for a given story version, so **seed + list of keys** reproduces a playthrough. That's the
save/replay contract.

## Headless author scene playtesting

`AuthorSession` is an author-only foundation, not a runnable debugger UI or a new
player save format. Start directly at a scene without simulating preceding scenes:

```csharp
var author = AuthorSession.Create(story, new SceneId("finale"), new AuthorSetup
{
    Flags = new Dictionary<FlagId, bool> { [new("has-key")] = true },
    Stats = [new AuthorStatValue(EntityId.Player, new StatId("str"), 16)],
}, seed: 42, character: selection); // null for a fixed-protagonist story
StoryFrame frame = author.Current;
author.Choose(frame.Choices[0].Key);
```

Setup accepts only declared flags, integer variables, character base stats,
absolute inventory quantities (zero removes the item), and faction standings.
Unknown IDs, duplicate character/stat or character/item pairs and negative item
quantities throw before entry. Stats use existing domain clamping: `Setup`
retains requested values; `Checkpoint.State` exposes actual values before entry.
Selection and setup collections are copied. State/event views are stable
read-only snapshots; mutable `GameState` is not exposed.

A fresh jump initializes the world and validated character, applies setup
**before trigger priming**, then enters the target once through the normal
engine loop. Setup does not fabricate prior choices, visits, trigger firings or
dice. Target entry effects, checks, triggers and leveling run normally.
`IsSynthetic` is always true: reaching a scene this way is not proof of normal
reachability. Visit/fired-trigger history and dice position cannot be edited.

`JumpFresh(target, setup, seed, character)` replaces the run only after a separate
candidate succeeds. It discards progress and does not implicitly inherit the
old setup, character or seed; omitted seed follows normal story/random defaults.
Creation stories must supply a valid character again. `Choose` likewise rebuilds
a candidate and commits it only on success, preserving the active frame, state,
events and checkpoint if a predicate, check or trigger cascade throws.

`Checkpoint` captures the latest scene's boundary **before** its visit event,
entry effects, triggers and entry check. It exposes pre-entry state, events,
seed/roll position and pending transition notes. `checkpoint.Reenter()` returns
an independent candidate at the displayed scene, including its normal entry.
The checkpoint retains a private replay recipe, not just an event snapshot:
re-executing the prefix reconstructs trigger-edge memory and pending actions.
Prior choice/check effects are replayed before target entry; entry effects and
entry-check dice are not applied twice. A transition ending without another
scene entry has no checkpoint.

Replay requires the same unchanged compiled story and deterministic, side-effect-free
extensions, just like ordinary choice replay. Session isolation cannot undo
external effects performed by custom C# delegates. Rebuilding costs time
proportional to the selected path; choices are bounded by `StoryHistory.MaxChoices`.
Live edits, portable scenarios and player-save compatibility bypass are not
implemented here.

### Guarded author reload

`var candidate = author.Reload(newStory)` returns an independent candidate; it
never mutates `author`. Before entering the displayed scene, it verifies:

- Compatible declarations, creation, rules and triggers, including Tale-inferred
  flags/variables. Custom predicates/rules/modifiers require trusted rebuild/restart.
- Structural equality of every previously consumed scene, not merely surviving
  `c0` positional keys, plus the selected rendered label/key and generated hub
  choice's target/title/eligibility metadata. Current target body edits remain
  allowed. This deliberately rejects earlier prose changes too.
- The same pre-entry event prefix, hence the same event-folded state, seed/RNG
  position, pending transition notes and captured trigger-edge memory.

Engine data is compared structurally (including polymorphic AST nodes and
collections); collection order is conservatively significant. The new scene then
enters **once**. Its effects/check results may change intentionally. Decisions are
rebuilt; no stale trace/redo objects are adopted. Missing targets, divergent
history, schema changes, no entry boundary or runtime/cascade failures throw,
leaving the active run unchanged. `AuthorReloadException` distinguishes
reconstruction incompatibility from a candidate runtime failure that can be
repaired and retried. Fresh-jump recipes are replayed as such.

`ValidateReloadSchema(oldStory, newStory)` exposes the declaration guard before a
run exists. `AuthorSetup.Validate(story)` returns a copied/revalidated setup without
scene entry. Hosts remain responsible for whole-source/YAML restart policy and
serialized candidate adoption. None of this changes normal save fingerprints,
restoration or migration policy. Future edited timelines must extend the replay
recipe and guard explicitly; they cannot be inferred from event snapshots.

Text modifiers in all compiled bodies/choice labels (including hidden/nested
branches) must be engine-owned built-in instances before reconstruction starts.
Names and delegate-wrapper types do not establish provenance; custom callbacks
and same-name replacements require trusted rebuild/restart, without executing
those callbacks during the reload attempt.

### State inspection and execution explanations

Enable decision recording explicitly; ordinary player and author sessions do not
record by default:

```csharp
var author = AuthorSession.Create(story, new SceneId("finale"),
    captureDecisions: true, seed: 42, character: selection);
var inspection = author.Inspect();
var guide = inspection.FindCharacters("guide").FirstOrDefault();
var hidden = inspection.FindDecisions(kind: DecisionKind.Choice)
    .Where(d => d.Outcome == DecisionOutcome.Excluded);
```

`Inspect()` returns a stable read-only snapshot of all declared characters
(identity, base/effective stats, inventory), factions/tiers, flags including
false values, variables including zero, all scene visit counts, fired-trigger
history, seed/roll count and events. Its typed lists support normal LINQ
filtering; character and decision searches use ordinal case-insensitive matching.
Inspecting/searching does not reevaluate branch predicates. Effective stats use
the ruleset's normal getters; custom modifiers must remain side-effect-free.

`DecisionsCaptured` distinguishes unavailable recording from an empty trace.
Each `RuleDecision` records its kind, subject (scene/choice/trigger ID or local
entry/divert index), scene, outcome/reason and event offsets. Applied effects
and resolved checks occupy `[EventStart, EventEnd)` in the **same snapshot's**
`Events`; pure decisions have equal offsets. Trigger passes, hub priority/rank
and limits are included where relevant. Never present a historical condition
result as a fresh evaluation against later state. A fresh jump replaces the
timeline; old inspection objects remain unchanged.

The expression tree retains the evaluator's actual operand/results, including
effective stat values. A null expression result means **not evaluated**, not
false. Short-circuit branches, later diverts and choices suppressed by an entry
check are explicitly skipped without executing their predicates. Named C#
predicates are opaque: their name and actual result are available, not invented
internals. No extra calls are made for explanations.

Hub explanations distinguish tag/self/once exclusion, false requirements,
duplicate IDs, stable priority ordering and hub-limit exclusion. Requirements
are evaluated before ranking/limiting, as in normal selection. Explicit diverts
still bypass storylet eligibility; observing them does not introduce new gates.
Entry conditions, choice requirements, fallback diverts, check difficulty/results
and outcome effects, trigger priming/edges/cascade effects are observed at their
actual engine boundaries. This is not a trace of every prose substitution or
effect arithmetic operation, nor an exact source navigator; source mapping is
separate shared-analysis work.

Recording follows successful author candidate replay: `Choose`, `JumpFresh`
and checkpoint `Reenter` preserve the recording setting, with no duplicated
append-only traces from earlier rebuild attempts. Failed candidates leave the
previous decisions/state intact and throw their error. This is opt-in in-memory
diagnostic data, not persisted in player saves or scenario files.

Custom hosts can pass an `Action<RuleDecision>` through the five-argument
`StorySession` constructor. Callbacks are synchronous, must not mutate/reenter
the engine, and their exceptions propagate. The normal constructor and
`IStorySession` contract remain unchanged.

## History and saves

`StoryHistory` starts a session and owns its transcript and choice history. `Choose`, `Back`, and `Forward`
maintain a linear undo/redo cursor; choosing after Back discards redo. Rebuilding fresh sessions restores
trigger edge state and deterministic dice, not just event-backed state. Manifest `undo: false` disables
navigation, not persistence.

`Save()` returns bounded UTF-8 JSON bytes. `StoryHistory.Restore(story, bytes)` creates and validates a
separate candidate, including redo choices. Only replace an active history after this succeeds.
The v1 format includes story ID/version, SHA-256 source/extension fingerprint, seed, choices and cursor.
It also has an optional `character` selection for opt-in creation stories. Older
fixed-protagonist v1 saves without it remain readable; creation stories require a
valid selection. Schema/source changes still invalidate mismatched fingerprints.
The limits are 2 MiB, 10,000 choices and 512 characters per choice key. Unknown fields, missing fields,
unsupported formats and incompatible stories fail explicitly. No migration is attempted.

Built-in compiler options provide a compatibility identity. Custom C# hosts must set
`CompilerOptions.CompatibilityId` and change it whenever registered extension behavior changes;
otherwise saving is disabled. Extensions must themselves be deterministic. A matching source fingerprint
cannot prove an arbitrary delegate is deterministic.

`Saves/ISaveStore` lists, reads and writes named slots scoped by story ID. Writes refuse replacement by
default. `FileSaveStore` stores desktop files under per-user local application data (`Talewright/saves`),
using uppercase hexadecimal UTF-8 slot names (distinct even on case-insensitive filesystems)
and hashed story directories. Replacements use a flushed same-directory temporary
file and atomic rename. Failed reads/writes throw explicit errors; the store never interprets or migrates
the save payload. Names are limited to 64 UTF-8 bytes, without control characters or surrounding spaces.

## Character initialization

For stories with `StoryDefinition.Creation`, supply a validated `CharacterSelection`
to `StoryHistory` or the `character` argument of `StorySession`. Missing/illegal
choices throw before creating a session; fixed stories reject unexpected creation
input. Selections are snapshotted. `CharacterIdentityChanged` and ordered
`StatChanged` events precede scene entry. The static world stays unchanged;
`IGameStateView.GetCharacterDefinition` returns the effective identity. Initial
trigger priming uses the fully initialized character, preserving rising-edge
semantics rather than treating creation as an in-story stat change.
