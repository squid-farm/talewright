# Talewright.Runtime — the engine loop

This namespace is a module in the [Talewright engine](../README.md), not a separate package.

Runs a compiled `StoryDefinition`. It is the module that coordinates every headless layer: Core state, Rules
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

Full description in [ARCHITECTURE.md §5](../../../docs/ARCHITECTURE.md#5-the-engine-loop-in-detail).

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
planned save format (M2).
