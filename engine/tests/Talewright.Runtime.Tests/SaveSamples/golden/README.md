# Golden saves

The quick, named and autosaves were written by the engine from the sample sources as they were checked in. The trigger checkpoint uses the test-only source overlay described below. `GoldenSaveTests` loads each save against its corresponding source and checks the restored scene, cursor, state and replay evidence. Tests only read these files. They never regenerate them.

Loading a save replays every saved choice, then compares the result with the replay evidence stored in the save. If an engine change alters gameplay (rule maths, trigger timing, evaluation order, compiler binding or emitted events), these saves stop loading, and saves that players already have would stop loading too.

## What each save covers

All three Lost Relic saves come from one playthrough of [`samples/LostRelic`](../../../../../samples/LostRelic) with seed `42`, randomness `checks-only`, and the default character from the `explorer` creation preset:

1. Continue, Haggle for supplies, Sign the expedition register, Continue. This is a camp hub selection; `camp.hub` is visited twice.
2. Set out for the temple, Tie off the rope and cross carefully, Make camp before the climb, Eat a ration, Share supper by the fire, Continue, Stand your ground. Courage reaches 10 and the `courage-earned` trigger fires.
3. Catch your breath, Press on at dawn, Force the door. The d20 strength check rolls 10 and fails, leading to `temple.side`.

| File | Written as | Covers |
| --- | --- | --- |
| `lost-relic-quick.twsave` | Quick Save after step 2 | Character creation, hub selection, the trigger firing, visit counts |
| `lost-relic-trigger.twtrigger` | Trigger checkpoint after step 2 | The `courage-earned` checkpoint envelope and restoration of the same playthrough |
| `lost-relic-slot.twsave` | Named save after step 3, then Back | Redo: cursor 13 of 14 choices. Going forward replays the skill-check roll |
| `lost-relic-autosave.twsave` | Autosave after going forward again | The skill-check roll and its failure route |
| `csharp-story.twsave` | Save at the ending of [`examples/CSharpStory`](../../../../../examples/CSharpStory), seed `7` | Replay identity of the extensions registered in `StoryExtensions.Create()` |

The checked-in Lost Relic does not mark `courage-earned` as a saving trigger. To exercise the trigger-checkpoint envelope without changing the sample story's behavior, the checkpoint test compiles a test-only source overlay that adds `save: true` to that existing trigger. It replays the quick-save choices, then pins and restores the resulting `TriggerSaveCodec` envelope. The other Lost Relic saves use the unchanged sample source.

Each save has a fixed summary time, and its SHA-256 is pinned in `GoldenSaveTests`.

## Changing these files

Changing, removing or regenerating a golden save is a compatibility break. It needs:

- an explicit decision by the project lead;
- a version bump;
- migration notes for players with existing saves.

Edits to the Lost Relic or CSharpStory sources normally keep these saves loading.
An explicitly approved incompatible Lost Relic update requires a version bump and
a player-facing migration note; regenerate only the affected Lost Relic fixtures.
The 0.5.0 update intentionally makes 0.4.0 Lost Relic saves incompatible because
the dream now reads glyph-mark inventory counts instead of the separately stored
`marks` variable. There is no automatic migration.

If a golden-save test fails after an engine change, assume the engine change is wrong. Do not update the fixture to make it pass.
