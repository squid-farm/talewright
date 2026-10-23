# Talewright.Rulesets.Generic — the default ruleset

A familiar d20-flavoured set of stats and rules, used by stories that say `ruleset: { base: generic }`. It is
**opt-out**: a story can disable parts of it (`disable: [cha, hp]`) or use `base: none` and define everything.

## What it defines (`GenericRuleset.Create()`)

| Kind | Stats |
|---|---|
| Attributes (1–30, default 10) | `str`, `dex`, `con`, `int`, `wis`, `cha` |
| Skills (0–10 ranks, default 0) | 18 skills, each governed by an attribute: athletics (STR); acrobatics, sleight-of-hand, stealth (DEX); arcana, history, investigation, nature, religion (INT); animal-handling, insight, medicine, perception, survival (WIS); deception, intimidation, performance, persuasion (CHA). See `GenericRuleset.Skills`. |
| Resource | `hp` (0–999, default 10) |
| Progression | `xp`, `level` (1–20) |
| Reputation tiers | hostile (< −30), unfriendly (≥ −30), neutral (≥ −9), friendly (≥ 10), allied (≥ 50) |

## Rules

- **`D20SkillCheckRule`**: total = d20 + skill ranks + governing attribute modifier (`(score − 10) / 2`, rounded
  down); success if total ≥ DC. When the story has `randomness: off`, the roll is 10 ("take 10"). If the stat being
  checked is an attribute, its own modifier is used.
- **`ExperienceLevelingRule`**: level *n* needs 100 × *n* × (*n* − 1) / 2 XP (0, 100, 300, 600 …), capped at 20.
  The runtime asks after every change, so granting XP is enough to level up.

## Files

| File | Contents |
|---|---|
| `GenericRuleset.cs` | `GenericRuleset` (ids, skill table, `Create`, `AttributeModifier`), `D20SkillCheckRule`, `ExperienceLevelingRule`. |

## Writing your own ruleset

Copy the shape of `Create()`: a `RulesetBuilder`, your stats, tiers and rules. To make it available to `.tale`
stories, register it in `CompilerOptions.Rulesets` (name → factory). Tests for this project live in
`engine/tests/Talewright.Core.Tests`.
