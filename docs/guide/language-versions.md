# Talemark language versions

`game.talemark` chooses how your story handles numbers. It's separate from `game.version`, the
version number you choose for your own story.

Starting with Talewright v0.8.0, you can choose Talemark 1 or Talemark 2. During v0, a story without
a `talemark` setting uses Talemark 1. Starting with the first 1.0 preview, the planned default is
Talemark 2. That default will stay at 2; it won't silently move to a newer language version later.
Talemark 1 will remain available throughout Talewright 1.x.

To keep the integer math your story uses today, add `talemark: 1` under `game:` in the root
`story.talewright.yaml`:

```yaml
game:
  id: my-story
  title: My Story
  talemark: 1
  start: beginning
```

To use fractional numbers instead, change that line to `talemark: 2`. Write the version as a plain
integer, not a quoted string, decimal, exponent or hexadecimal value. An editor's YAML schema may
accept alternate spellings that it normalizes to the same number; the compiler rejects them.
Malformed values and versions the player doesn't support are errors.

Starting with v0.8.0, an unmarked story shows an informational notice suggesting `talemark: 1`.
It's not a warning, so validation and publishing still succeed with `--warnings-as-errors`. Pinning
a version keeps your choice clear when the default changes.

## What changes in Talemark 2?

Talemark 1 uses whole numbers. Division drops the fraction toward zero, and arithmetic is limited
to the range of a 32-bit signed integer. When a result goes beyond that range, it stops at the
nearest limit. Talemark 2 keeps exact fractions instead: `1 / 4` and `.25` have the same value.
In both versions, division by zero returns `0` without interrupting play.

In Talemark 2, number variables and resource stats can keep fractions. Items can too, if you
declare them with `divisible: true`. Other values, including attributes, skills, faction standing,
XP, levels, ordinary item counts, visits and check difficulty, stay whole. At those whole-number
boundaries, incoming fractional amounts round to the nearest whole number, with halves away from
zero. A fractional resource used in a check is rounded for that check; the stored resource doesn't
change.

Players see numbers rounded to at most two decimal places. Halves round away from zero and trailing
zeros are omitted, but the stored value stays exact. For example, `1 / 3` displays as `0.33`, and
`0.995` displays as `1`. Comparisons still use the exact values. `#one-many` uses the displayed
value, so `0.995` selects the wording for one even though it doesn't compare equal to `1`.

## Moving an existing story

You don't have to switch to Talemark 2 when you update Talewright. If you want your existing story
to keep its integer behavior, pin `talemark: 1` before opening it with a player that changes the
default. A published game keeps the player it was bundled with.

When you're ready to try Talemark 2:

1. Keep a copy of the original story and its compatible player. Export copies of your saves before
   changing anything; [backing up saves](playing-and-saving.md#backing-up-and-moving-saves) shows how.
2. Set `talemark: 2` in the root manifest. Check divisions and calculations that relied on
   whole-number results or 32-bit limits. Review conditions and choices that depend on those results.
3. Check values that must stay whole, and mark an item `divisible: true` only if fractional
   quantities make sense for it. Test the story with the player and publishing target you plan to use.
4. Test saved progress too. A supported checkpoint can carry state into the updated story, but it
   starts a new Back boundary: Back and Forward can't cross it, and undone choices are discarded.
   Some updates can't continue a save.

Older replay-only saves that need the old arithmetic can't be migrated automatically by the player.
Keep the original story, save and compatible player so you can still open them. A failed load leaves
the save and active game untouched. See [updating a story with existing saves](playing-and-saving.md#updating-a-story-with-existing-saves)
for more about checkpoints and save compatibility.
