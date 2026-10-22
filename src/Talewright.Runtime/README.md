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

Full description in [ARCHITECTURE.md §5](../../docs/ARCHITECTURE.md#5-the-engine-loop-in-detail).

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
No cross-source reload, live edits, portable scenarios or player-save compatibility
bypass is implemented here. Source-aware reload must separately verify a new
story's reconstruction boundary before adoption.

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
