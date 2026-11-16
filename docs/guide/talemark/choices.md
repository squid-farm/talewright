# Choices, requirements and diverts

Scenes execute top to bottom. A choice offers a reader action; a scene-level divert leaves
immediately. Later steps after a reached divert do not execute. There is no implicit gather,
fallback or fallthrough to another scene.

Write `* [reader label] (requires condition) { effects } -> destination`.
The requirement and effects are optional; either may precede the other, but both must be before
the terminal divert. Each choice needs its own `-> scene-id` or `-> END`.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: locked-gate, title: The Gate, start: gate }
characters:
  player: { first-name: Ada, inventory: { coin: 2 } }
items:
  coin: { name: silver coin }
flags: [gate-open]
```

`story.tale`:

```tale
=== gate
@title The Gate

{if flag:gate-open}
-> garden
{/if}
The keeper asks for two coins.
* [Pay] (requires player:inventory:coin:count >= 2, shown "You need two coins.") { take player coin 2; set flag:gate-open } -> garden
* [Walk away] -> END

=== garden
@choice-label Enter the garden

You are inside.
* [Return to the gate] -> gate
* -> END
```

The guard at `gate` is an early exit before any choices. After paying, returning skips the demand.
The second choice provides a way out regardless of the player's inventory.

## Flow rules and defaults

- After the first choice, write only choices, choice-only `{if}` blocks, comments or blank lines.
  Put narrative, `~` effects, explicit check blocks and conditional early diverts before choices.
  Never put a trailing `-> END` after choices as a fallback.
- A false requirement hides the choice. Add `, shown` to show a disabled choice with its normal
  label, or `, shown "reader text"` to replace its label only while unavailable.
  Choice effects run only when selected, not when the choices are displayed.
- `* -> garden` omits authored text: it uses the destination's `@choice-label`, otherwise
  **Continue**. `* -> END` always uses **Continue**. An empty `[]` is not this shortcut.
  Use the labeled form when adding choice clauses; keep textless choices simple.
- `-> garden` on its own leaves now and automatically enters `garden`; each reached scene's
  rendered text remains in order. A conditional scene divert does the same. `-> END` presents the
  final frame. Do not declare a scene called `END`.
- An entry skill-check outcome targeting another scene still presents its result with **Continue**;
  an outcome targeting `END` finishes in that frame. A choice check follows its selected outcome
  without an extra pause. Explicit choices, including `* -> garden` and `* -> END`, wait for the reader.
- `@title` is a plain-text heading. `@choice-label` is reader text used by bare scene choices
  and hub offers. Metadata belongs directly under the scene header.
- A scene without a possible exit is invalid. The compiler does not prove that some requirement
  will hold during play: all-disabled/all-hidden choices can still strand a player. Test the routes.
- `//` starts a comment only at the beginning of a trimmed line; it is not a trailing comment marker.
