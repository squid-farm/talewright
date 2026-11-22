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

Version `2` is not selected or generated until the exact-fraction feature set is complete. This guide
will document its rollout separately before that behavior is available.
