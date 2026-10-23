# Talewright.Core — the RPG engine

The heart of Talewright: who exists, what they have, and what has happened. It knows nothing about text,
templates, rules syntax or UI, and has **no package dependencies**.

See [architecture §3.1](../../../docs/developer/architecture.md#31-talewrightcore--the-rpg-engine) for how it fits in.

## Key ideas

- **Event-sourced.** `GameState` is only ever changed by `Apply(GameEvent)`, and only `EventLog` calls that.
  The whole game is `fold(initial state, events)`, which gives replay, saves (M2) and undo (`Truncate`) for free.
- **Ruleset-defined.** There are no built-in stats. A `Ruleset` declares stats, reputation tiers and pluggable
  rules (skill checks, leveling, modifiers). The default d20 set lives in `Talewright.Rulesets.Generic`.
- **Strongly typed ids.** `StatId`, `ItemId`, `FactionId` … are distinct types, so they can't be mixed up.

## Files

| File | Contents |
|---|---|
| `Identity/Ids.cs` | `EntityId` (with `EntityId.Player`), `StatId`, `ItemId`, `FactionId`, `SceneId`, `FlagId`, `VarId`: readonly record structs over a string. |
| `Rulesets/StatDefinition.cs` | `StatDefinition` (id, name, category, min/max/default, governing attribute for skills), `StatCategory`, `ReputationTier`. |
| `Rulesets/RuleInterfaces.cs` | `ISkillCheckRule` (+ `SkillCheckRequest`/`SkillCheckResult`), `ILevelingRule`, `IStatModifier`. Implement these to change game mechanics. |
| `Rulesets/Ruleset.cs` | Immutable ruleset: stats, tiers, rules. `TierFor(standing)`, `FindTier(id)`, `ToBuilder()`. `Ruleset.Empty`. |
| `Rulesets/RulesetBuilder.cs` | Fluent builder: `AddStat`, `RemoveStat`, `ReplaceStat`, `WithTiers`, `WithSkillCheckRule`, `WithLevelingRule`, `AddModifier`, `Build`. Removing the level stat also drops the leveling rule. |
| `World/Definitions.cs` | `CharacterDefinition`, `ItemDefinition` (stat modifiers while held), `FactionDefinition`, `WorldDefinition` (everything static + the ruleset). |
| `World/Pronouns.cs` | Pronoun sets (`She`, `He`, `They`, `It`, custom) with subject/object/possessive/possessive-pronoun/reflexive and a plural flag for verb agreement. |
| `Events/GameEvents.cs` | Every state change as a record. Values are absolute (`OldValue`, `NewValue`), so a log is readable on its own. |
| `State/IGameStateView.cs` | Read-only query interface used by every other layer. |
| `State/GameState.cs` | The projection. `CreateInitial(world, seed)`, `Apply(event)`, and the queries. Effective stat = base + `IStatModifier`s + held item modifiers, clamped. |
| `State/EventLog.cs` | Owns the state and event list: `Append`, `AppendRange`, `Replay`, `Truncate`, `Appended` event. |
| `State/StateChanges.cs` | Computes the events for a change (clamping; no events if nothing changes). Includes `CheckLevel`. Prefer these over hand-built events. |
| `Randomness/DeterministicDice.cs` | `Roll(seed, index, sides)`: pure SplitMix64. The index is `state.RollCount`. `NewSeed()` for a random seed. |

## Using it directly

```csharp
var ruleset = new RulesetBuilder("romance")
    .AddStat(new StatDefinition(new StatId("affection"), "Affection", StatCategory.Custom, 0, 100))
    .Build();

var world = new WorldDefinition(ruleset, characters, items, factions, flags, variables);
var log = new EventLog(world, seed: 42);

log.AppendRange(StateChanges.AdjustStat(log.State, EntityId.Player, new StatId("affection"), +5));
var affection = log.State.GetStat(EntityId.Player, new StatId("affection"));   // 5
```

## Adding a new kind of state

1. Add the data to `GameState` (private field + a query on `IGameStateView`).
2. Add a `GameEvent` record in `GameEvents.cs`.
3. Handle it in `GameState.Apply`.
4. Add a helper in `StateChanges` that produces the event.
5. Teach `EventNarrator` (Runtime) to describe it, if players should be told.
