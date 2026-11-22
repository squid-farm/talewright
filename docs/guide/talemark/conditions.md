# Conditions: reveal content only when something is true

Use the author's existing state when available. "The festival happened" and "the player found
the letter" are different facts: do not conflate them, and do not set the festival flag just to make a
condition pass. If the story provides no way to identify the event, explain which state is needed.

`{if condition}reader text{else}other text{/if}` selects text; `{else}` is optional.
For whole steps, put `{if ...}`, `{else}` and `{/if}` on their own lines.
A block picks its branch once when reached, then runs its steps in order. An inline condition is
evaluated when its prose line renders. Nest `{if}` blocks for additional tests; there is no
`elseif` keyword.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: festival, title: The Letter, start: hall, talemark: 1 }
characters:
  player: { first-name: Ada }
flags: [festival-happened]
```

`story.tale`:

```tale
=== hall
{if flag:festival-happened}The festival is over.{else}The festival is about to begin.{/if}
* [Attend the festival] { set flag:festival-happened } -> study
* [Visit the study now] -> study

=== study
{if flag:festival-happened}
You find a letter on the desk.
{else}
The desk is empty.
{/if}
* [Return to the hall] -> hall
* [Leave] -> END
```

The first route reveals the letter; visiting before the festival does not. In an existing scene,
insert just the conditional around the intended prose, not this whole sample. This does not prevent
finding the letter again on later visits; use a separate discovery flag if requested.

## Expression toolbox

| Test | Form |
|---|---|
| Flag / opposite | `flag:festival-happened` / `not flag:festival-happened` |
| Holds an item | `player has key` |
| Has at least two items | `player:inventory:coin:count >= 2` |
| Entered a scene / visit count | `player visited hall` / `hall:player:visits > 1` |
| Owning scene visit count | `scene:player:visits == 1` |
| Text / number variable | `var:weather == "rain"` / `var:clues >= 3` |
| Stat / faction | `player:hp > 0` / `faction:guild:tier >= friendly` |
| Combine | `flag:festival-happened and (var:clues >= 3 or player has key)` |

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
