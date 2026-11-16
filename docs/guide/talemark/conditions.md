# Conditions: reveal content only when something is true

Use the author's existing state when available. "The murder happened" and "the player discovered
the body" are different facts: do not conflate them, and do not set the murder flag just to make a
condition pass. If the story provides no way to identify the event, explain which state is needed.

`{if condition}reader text{else}other text{/if}` selects text; `{else}` is optional.
For whole steps, put `{if ...}`, `{else}` and `{/if}` on their own lines.
A block picks its branch once when reached, then runs its steps in order. An inline condition is
evaluated when its prose line renders. Nest `{if}` blocks for additional tests; there is no
`elseif` keyword.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: mystery, title: The Locked Study, start: hall }
characters:
  player: { first-name: Ada }
flags: [murder-happened]
```

`story.tale`:

```tale
=== hall
{if flag:murder-happened}The house is silent.{else}Music fills the house.{/if}
* [Wait until midnight] { set flag:murder-happened } -> study
* [Visit the study now] -> study

=== study
{if flag:murder-happened}
You discover the body.
{else}
The host greets you, very much alive.
{/if}
* [Return to the hall] -> hall
* [Leave] -> END
```

The first route reveals the body; visiting before midnight does not. In an existing scene, insert
just the conditional around the intended prose, not this whole sample. This does not prevent
repeated discovery on later visits; use a separate discovery flag if requested.

## Expression toolbox

| Test | Form |
|---|---|
| Flag / opposite | `flag:murder-happened` / `not flag:murder-happened` |
| Holds an item | `player has key` |
| Has at least two items | `player:inventory:coin:count >= 2` |
| Entered a scene / visit count | `player visited hall` / `hall:player:visits > 1` |
| Current scene visit count | `scene:player:visits == 1` |
| Text / number variable | `var:weather == "rain"` / `var:clues >= 3` |
| Stat / faction | `player:hp > 0` / `faction:guild:tier >= friendly` |
| Combine | `flag:murder-happened and (var:clues >= 3 or player has key)` |

Examples in the table require those names to exist in the story; do not add them merely to copy
an expression. Conditions are boolean: use a comparison for a number, not `if var:clues`.
Use `==` for equality, not effect assignment `=`. Comparisons are `== != < <= > >=`;
ordered comparisons work on numbers or faction tiers, not text.

Integers support `+ - * /`; put spaces around subtraction so `var:clues - 1` is not read as the
name `clues-1`. Multiplication/division bind before addition/subtraction, then comparisons, then
`not`, `and`, `or`. Use parentheses when mixing tests. `and`/`or` short-circuit.
Text literals use double quotes; single quotes are not Talemark string delimiters.
History of arbitrary choices is not queryable: explicitly record facts with flags or variables.

A condition never changes state. For choice availability, put it inside `(requires ...)` rather
than hiding unrelated prose. `@requires` is for generated hub offers, not a guard on direct entry.
