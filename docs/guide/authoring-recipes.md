# Names, recurring text and custom skills

These small stories show how to keep recurring details in one place and add a skill that fits
your setting. Each example uses two files in its own story folder. Check and play them using
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

## Add a skill for your setting

For a space adventure, add **Piloting** rather than using an unrelated built-in skill.
Declare its id, display name, limits and starting value under `game.ruleset.stats`.
Use `category: skill` so it can be used in a skill check.

**story.talewright.yaml**

```yaml
game:
  id: piloting-lesson
  title: Through the Asteroids
  start: approach
  ruleset:
    base: generic
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

`base: generic` keeps the standard rules and skills, then adds `piloting`.
`stats: { piloting: 3 }` gives the player a starting Piloting value of 3; characters without an
explicit value use the skill's `default`. The check uses the lowercase id `piloting`, not the
display name `Piloting`. This custom skill has no linked attribute, so the generic rules add its
skill value to the roll without an attribute bonus. Try both outcomes with the
[Author Debugger](testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger).

If a check reports an unknown skill, first compare the spelling with its declaration.
Then check that it is under `game.ruleset.stats`, uses `category: skill`, and that the story's
chosen ruleset includes it. Changing a character's stat value alone does not declare a new skill.
See the [ruleset settings](../reference/talemark-syntax.md#1-storytalewrightyaml) for the full options.
