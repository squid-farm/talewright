# Names, recurring text and custom skills

These small stories show how to keep recurring details in one place, check your own stats and add
a skill that fits your setting. Each example uses two files in its own story folder. Check and play them using
the steps in [Your first story](first-story.md).

## Change a character or ship name in one place

Keep a character's **id**, such as `captain`, separate from the name readers see.
The id is the stable name used by your story's dialogue and references.
The displayed name, such as Rowan, is stored under `first-name`.

For other recurring text, such as a ship name, declare a text variable under `vars:`.
Both branches below read the same character name and ship name.

**story.talewright.yaml**

```yaml
game:
  id: harbour-names
  title: A Ship by Any Name
  start: harbour

characters:
  player: { first-name: Alex }
  captain: { first-name: Rowan }

vars:
  ship-name: "Morning Star"
```

**story.tale**

```tale
=== harbour
{captain:first-name} waits aboard the {var:ship-name}.

* [Visit the bridge] -> bridge
* [Check the cargo] -> cargo

=== bridge
> captain: Welcome to the bridge of the {var:ship-name}.
{captain:first-name} points out your route.
-> END

=== cargo
Crates fill the hold of the {var:ship-name}.
> captain: Everything is ready.
{captain:first-name} hands you the cargo list.
-> END
```

Change `first-name: Rowan` to `first-name: Morgan` and `"Morning Star"` to `"Evening Tide"`
in the settings file. Save, check and start a fresh run. Both branches now use the new names
without changing their prose or the `captain` id.

`{captain:first-name}` reads the character's current name; `{var:ship-name}` reads the variable's
current text. A text variable can also change during play, for example
`~ var:ship-name = "Evening Tide"`. Assigning it to another variable copies its value at that moment;
it does not keep the two linked.

These substitutions reuse **values**, not whole paragraphs or scenes. They are not reusable prose
templates or custom item properties. See [text variables](../reference/talemark-syntax.md#text-variables)
for the supported operations.

## Your own stats with skill checks

Skill checks work on any stat your story declares. You don't need `ruleset: rpg` and its built-in
stats. Declare **Nerve** and roll against it:

**story.talewright.yaml**

```yaml
game:
  id: rope-bridge
  title: The Rope Bridge
  start: bridge
  ruleset:
    stats:
      - { id: nerve, name: Nerve }

characters:
  player:
    first-name: Ada
    stats: { nerve: 3 }
```

**story.tale**

```tale
=== bridge
The bridge sways high above the river.

* [Cross the rope bridge] skill-check nerve dc 12
  success: -> far-side
  failure: -> river

=== far-side
You step onto solid rock.
-> END

=== river
A plank snaps and you drop into the cold water.
-> END
```

The check rolls a d20, adds Nerve and compares the total with the DC: a total of 12 or more
succeeds, so with Nerve 3 any roll of 9 or more gets Ada across. With `randomness: off` the die is
always 10, so this check totals `10 + 3 = 13` and succeeds. Dice are only rolled for checks; a story
that never uses `skill-check` never rolls.

A `ruleset` without `base` adds only the stats you list, so `nerve` is the only stat here. Its
`category` defaults to `custom`, which adds the stat's value to the roll. Leave it as `custom` for
ordinary stats such as nerve, charm or sanity.

> **Watch out for `category: attribute`.** Attributes use d20 attribute maths: a check adds
> `floor((score - 10) / 2)`, not the score. On a 0–10 scale an attribute of 5 rolls at −3, and even
> 10 adds nothing. Use `category: attribute` only for d20-style scores around 10, such as an
> attribute that a skill links to (see [link a skill to a custom attribute](#link-a-skill-to-a-custom-attribute)).

## Add a skill for your setting

For a space adventure, add **Piloting** rather than using an unrelated built-in skill.
Declare its id, display name, limits and starting value under `game.ruleset.stats`.
Use `category: skill` so it can link to an attribute, as shown in
[link a skill to a custom attribute](#link-a-skill-to-a-custom-attribute).

**story.talewright.yaml**

```yaml
game:
  id: piloting-lesson
  title: Through the Asteroids
  start: approach
  ruleset:
    base: rpg
    stats:
      - id: piloting
        name: Piloting
        category: skill
        min: 0
        max: 10
        default: 0

characters:
  player:
    first-name: Alex
    stats: { piloting: 3 }
```

**story.tale**

```tale
=== approach
An asteroid turns slowly across your flight path.

* [Fly through the gap] skill-check piloting dc 12
  success: -> clear
  failure: -> detour

=== clear
You thread the gap and reach open space.
-> END

=== detour
You turn away in time, taking the longer route.
-> END
```

`base: rpg` turns on the RPG rules and skills, then adds `piloting`.
`stats: { piloting: 3 }` gives the player a starting Piloting value of 3; characters without an
explicit value use the skill's `default`. The check uses the lowercase id `piloting`, not the
display name `Piloting`. This custom skill has no linked attribute, so the RPG rules add its
skill value to the roll without an attribute bonus. Try both outcomes with the
[Author Debugger](testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger).

If a check reports an unknown skill, first compare the spelling with its declaration.
Then check that it is under `game.ruleset.stats` and that the story's chosen ruleset includes it. Changing a character's stat value alone does not declare a new skill.
See the [ruleset settings](../reference/talemark-syntax.md#1-storytalewrightyaml) for the full options.

### Link a skill to a custom attribute

For a wilderness adventure, let **Tracking** use a custom **Grit** attribute:

```yaml
game:
  id: tracking-lesson
  title: Follow the Trail
  start: trail
  randomness: off
  ruleset:
    base: rpg
    stats:
      - id: grit
        name: Grit
        category: attribute
        min: 1
        max: 30
        default: 10
      - id: tracking
        name: Tracking
        category: skill
        min: 0
        max: 10
        default: 0
        governing-attribute: grit
characters:
  player:
    first-name: Alex
    stats: { grit: 15, tracking: 3 }
```

```tale
=== trail
* [Follow the tracks] skill-check tracking dc 14
  success: -> found
  failure: -> lost
=== found
You find the trail.
-> END
=== lost
The trail fades.
-> END
```

The RPG check adds **skill value + floor((attribute score - 10) / 2)** to the
die result. Grit 15 contributes `floor(2.5) = 2`, so this check totals
`10 + 3 + 2 = 15` and succeeds. Grit 9 contributes `-1`, not zero. With
`governing-attribute` omitted, the total is `10 + 3 = 13` and the check fails.
`randomness: off` fixes the die at 10 for this example; normal checks roll a d20.
Try the same check in the Author Debugger to inspect the recorded total.

`governing-attribute` is optional. You can use it on any `category: skill` stat, including
built-in skills you redeclare. It names exactly one attribute, which must be declared (before or
after the skill) with `category: attribute` and not disabled. Item modifiers count towards both
values in the check.
