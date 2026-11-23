# Talemark language versions

`game.talemark` pins the arithmetic and language behavior used by a story. It is separate from
`game.version`, which remains an author-defined story version.

During Talewright v0, pin a story to the existing behavior:

```yaml
game:
  id: my-story
  title: My Story
  talemark: 1
  start: beginning
```

Version `1` preserves the current integer behavior: int32 saturation, division toward zero and
division by zero returning `0`.

When `game.talemark` is omitted, Talewright v0 uses version 1 and shows an informational migration
notice. Pinning `1` is recommended so a future default or player cannot change this story's arithmetic
accidentally. The key is not a warning: validation and publishing succeed with `--warnings-as-errors`.
Malformed values and values this release does not support are errors.
Write the version as a plain integer, not a quoted string, decimal, exponent or hexadecimal value.
An editor's YAML schema may accept alternate spellings that it normalizes to the same number;
the compiler rejects them.

Before adopting another language version, test the complete story and saved progress with that player
and publishing target. Keep a copy of the original save and use only supported, validated migration
paths. Older players continue to run stories bundled with their published player; an unmarked story
loaded by a future player may follow that future player's documented omission default.

Version `2` adds exact decimal arithmetic and numeric variables. For example, `1 / 4` and `.25`
represent the same exact value, and numeric variables may start with decimal values or exponent
notation. Saves preserve exact numeric variables, resources and divisible inventory. Version 2 is
still under development, including player presentation; do not select it for an author-facing story
until its features ship together.

When a story changes arithmetic versions, a supported checkpoint can carry its state forward and
starts a new Back boundary. Older replay-only saves need a validated replay against their original
story; the player does not automatically find that story. Keep the original story and save, and test
your migration. Some updates cannot be migrated.
