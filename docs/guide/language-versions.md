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
represent the same exact value, and numeric variables may start with decimal values. Version 2 is
still under development: resource and inventory behavior, exact save preservation, and player
presentation are not complete. The current player rejects saves when a version-2 numeric variable
is fractional or outside the int32 range. Do not select version 2 for an author-facing story until
those parts ship together. The compiler and debugger support below exists for implementation and
testing only.
