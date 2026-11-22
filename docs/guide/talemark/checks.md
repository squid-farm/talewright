# Skill checks and explicit outcomes

`skill-check [actor] stat [dc number-expression][, hidden]` uses a declared stat.
Actor defaults to `player`. Without `dc` the expression returns a number; with `dc` it returns
boolean success according to the ruleset. `, hidden` hides the note, not the event or result.

Without `game.ruleset`, no stats exist by default. Declare a custom stat as below, or opt into
`game.ruleset: rpg` for built-in stats. A custom skill needs no C# registration when declared in
YAML. Do not invent predicate or manipulator registrations.

## Complete example

`story.talewright.yaml`:

```yaml
game:
  id: evidence
  title: The Evidence
  talemark: "1"
  start: study
  seed: 42
  ruleset:
    stats:
      - { id: deduction, name: Deduction }
characters:
  player: { first-name: Ada, stats: { deduction: 3 } }
flags: [clue-found]
vars: { roll: 0 }
```

`story.tale`:

```tale
=== study
~ var:roll = skill-check deduction, hidden
{if var:roll >= 10}You notice an ink stain.{/if}
{if var:roll >= 15}You recognize the signature.{/if}
* [Examine the lock] skill-check deduction dc 12
  success: { set flag:clue-found } -> evidence
  failure: -> missed

=== evidence
The lock was forced.
::: skill-check deduction dc 10, hidden
success: { set flag:clue-found }
failure: { clear flag:clue-found }
:::
{if flag:clue-found}You preserve the tool marks.{/if}
-> END

=== missed
The mechanism defeats you.
-> END
```

## Timing and branch rules

- Numeric assignment stores one total for several comparisons; those comparisons do not reroll.
  An expression check resolves once per authored occurrence per scene visit. A new visit can
  produce a new result. Separate check occurrences are separate checks even for the same stat.
- Checks can appear in `{if}`, choice `(requires ...)`, hub `@requires`, numeric assignments and
  trigger `do`, but not trigger `when`. Boolean short-circuiting can skip an expression check.
- Choice checks need a reader label, both `success:` and `failure:` outcomes, and an explicit
  target on **each** outcome. The chosen check follows its outcome without an extra **Continue**
  pause. Do not add a trailing shared target or fall through after choices.
- A `::: skill-check ...` block runs at that line, ends with `:::`, and needs both outcomes.
  Each outcome can apply effects. Without a divert it continues at the following scene step;
  a scene target pauses for **Continue**, while `-> END` finishes in the current frame. Put these
  blocks before the first choice.
- Built-in `none` and `rpg` checks use d20 plus modifiers. Non-attribute custom stats add their
  value; `category: attribute` instead adds `floor((score - 10) / 2)`. Do not mark an ordinary
  additive skill as an attribute. `randomness: off` supplies the ruleset's no-dice result;
  a fixed `seed` instead makes dice repeatable for the same story and playthrough.
- A requirement check can hide or disable an option. If the author wants the player to choose an
  attempt and then see success/failure consequences, use an explicit choice check instead.
