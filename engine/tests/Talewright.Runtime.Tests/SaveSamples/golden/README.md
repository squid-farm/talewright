# Golden saves

These saves were written by the engine from the sample sources as they were checked in. `GoldenSaveTests` loads each one against the current sources and checks the restored scene, cursor, state and replay evidence. Tests only read these files. They never regenerate them.

Loading a save replays every saved choice, then compares the result with the replay evidence stored in the save. If an engine change alters gameplay (rule maths, trigger timing, evaluation order, compiler binding or emitted events), these saves stop loading, and saves that players already have would stop loading too.

## What each save covers

All three Lost Relic saves come from one playthrough of [`samples/LostRelic`](../../../../../samples/LostRelic) with seed `42`, randomness `checks-only`, and the default character from the `explorer` creation preset:

1. Continue, Haggle for supplies, Sign the expedition register, Continue. This is a camp hub selection; `camp.hub` is visited twice.
2. Set out for the temple, Tie off the rope and cross carefully, Make camp before the climb, Eat a ration, Share supper by the fire, Continue, Stand your ground. Courage reaches 10 and the `courage-earned` trigger fires.
3. Catch your breath, Press on at dawn, Force the door. The d20 strength check rolls 10 and fails, leading to `temple.side`.

| File | Written as | Covers |
| --- | --- | --- |
| `lost-relic-quick.twsave` | Quick Save after step 2 | Character creation, hub selection, the trigger firing, visit counts |
| `lost-relic-slot.twsave` | Named save after step 3, then Back | Redo: cursor 13 of 14 choices. Going forward replays the skill-check roll |
| `lost-relic-autosave.twsave` | Autosave after going forward again | The skill-check roll and its failure route |
| `csharp-story.twsave` | Save at the ending of [`examples/CSharpStory`](../../../../../examples/CSharpStory), seed `7` | Replay identity of the extensions registered in `StoryExtensions.Create()` |

The Lost Relic declares no saving trigger (`save: true`), so these fixtures include no trigger checkpoint file. The trigger's firing is covered by the replayed state and evidence. Trigger checkpoints use the same save bytes, wrapped by `TriggerSaveCodec`.

Each save has a fixed summary time, and its SHA-256 is pinned in `GoldenSaveTests`.

## Changing these files

Changing, removing or regenerating a golden save is a compatibility break. It needs:

- an explicit decision by the project lead;
- a version bump;
- migration notes for players with existing saves.

Edits to the Lost Relic or CSharpStory sources must keep these saves loading. If an edit can't, it falls under the same rule.

If a golden-save test fails after an engine change, assume the engine change is wrong. Do not update the fixture to make it pass.
