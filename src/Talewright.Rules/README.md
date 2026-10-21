# Talewright.Rules — the rules engine

Evaluates conditions, applies effects, resolves skill checks, fires triggers and selects storylets, all against
the read-only `IGameStateView` from Core, writing changes back only as events through `EventLog`.

Everything here operates on **typed trees** built by the compiler. Nothing parses strings, nothing uses reflection.

## Folders

### `Expressions/`

| File | Contents |
|---|---|
| `Expr.cs` | The expression AST. `ExprType` (Number, Bool, Text, Tier). Nodes: `NumberLiteral`, `BoolLiteral`, `TextLiteral`, `TierLiteral`, `StatRef`, `StandingRef`, `StandingTierRef`, `FlagRef`, `VarRef`, `HasItem`, `Visited`, `NamedPredicate`, `NameRef` (+`NamePart`), `PronounRef` (+`PronounForm`), `FactionNameRef`, `ItemNameRef`, `UnaryExpr`, `BinaryExpr` (+`UnaryOp`, `BinaryOp`). |
| `ExpressionEvaluator.cs` | `Evaluate(expr, context) → Value`, plus `IsTrue` and `EvaluateNumber`. `EvaluationContext` = state + named predicates. Tier comparisons use tier *order*, not names. |
| `NamedPredicateRegistry.cs` | C# conditions callable from stories as `@Name`. `Register("IsNight", state => …)`. |

### `Effects/`

`Effects.cs`: `ChangeStat`, `ChangeStanding`, `ChangeVariable` (each with `AssignOp` `=`/`+=`/`-=` and an
`Expr` amount), `SetFlag`, `GiveItem`, `TakeItem`. `EffectExecutor.Execute(All)` evaluates each against the
*current* state (so later effects see earlier ones) and appends events via `StateChanges`.

### `Checks/`

`SkillCheckResolver.cs`: `SkillCheck(Actor, Skill, Difficulty)`, `RandomnessMode` (`Off`, `ChecksOnly`).
`Resolve` rolls the ruleset's die with the seeded dice (appending `DiceRolled`), delegates to
`Ruleset.SkillCheckRule`, and appends `SkillCheckResolved`. With randomness off it passes no roll and the rule
decides (generic: take 10).

### `Triggers/`

`TriggerEngine.cs`: `TriggerDefinition(Id, When, Effects, Once)`. `Run(log)` fires triggers whose condition has
**become** true since the last run (rising edge), re-checking until nothing new fires, capped at `MaxPasses = 8`.
`Once` triggers never fire twice (checked via `TriggerFired` events). `Prime(state)` records conditions that are
already true so they don't fire on load.

### `Storylets/`

`StoryletSelector.cs`: `IStorylet` (Id, Requires, Tags, Priority, Once). `SelectEligible(storylets, tag,
context)` → those with the tag whose requirements hold and that aren't spent `Once` storylets, by descending
priority then declaration order. This is the "hub" mechanism now and the open-world location mechanism later.
