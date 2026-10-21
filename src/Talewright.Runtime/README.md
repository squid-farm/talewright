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
