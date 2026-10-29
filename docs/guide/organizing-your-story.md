# Organizing your story

Start small. You can keep all your story settings and characters in one file,
then split them up when scrolling starts to get in the way. Neither layout gives
your story extra features: choose whichever is easier to write.
To keep a name or short recurring text consistent across branches, use a character substitution
or text variable rather than copying the value into every passage.
See [names and recurring text](authoring-recipes.md#change-a-character-or-ship-name-in-one-place).

## A small story: two files

Make a folder called `night-ferry` with these two files:

```text
night-ferry/
  story.talewright.yaml
  story.tale
```

**story.talewright.yaml**

```yaml
game:
  id: night-ferry
  title: The Night Ferry
  start: dock

characters:
  player: { first-name: Ada, inventory: [coin] }
  ferryman: { first-name: Wick }

items:
  coin: { name: silver coin }
```

**story.tale**

```tale
=== dock
@title The Dock

> ferryman: Crossing tonight, {player:first-name}?

* [Pay with your silver coin] (requires: player has coin) { take player coin } -> crossing
* [Walk away] -> END

=== crossing
@title The Crossing

The boat slides into the dark.
-> END
```

That's enough to play. Follow [Your first story](first-story.md) for checking,
playing, and adding more choices.

## As it grows: move related things together

Keep `story.talewright.yaml` at the top of your story folder. Put extra settings
files in subfolders, and give them names ending in `.talewright.yaml`.

Here's the same story split into smaller files:

```text
night-ferry/
  story.talewright.yaml
  characters/
    player.talewright.yaml
  harbour/
    ferry.talewright.yaml
  chapters/
    story.tale
```

**story.talewright.yaml**

```yaml
game:
  id: night-ferry
  title: The Night Ferry
  start: dock
```

**characters/player.talewright.yaml**

```yaml
characters:
  player: { first-name: Ada, inventory: [coin] }
```

**harbour/ferry.talewright.yaml**

```yaml
characters:
  ferryman: { first-name: Wick }

items:
  coin: { name: silver coin }
```

Move the earlier `story.tale` into `chapters/` without changing its text.
Ada still carries the coin, Wick still speaks, and the choices still lead to
the same scenes.

When you separate the playable character, `characters/player.talewright.yaml`
is a useful name. It isn't compulsory. A file can contain several characters,
or mix characters, items, factions, flags, and variables, as the ferry file does.
You don't need a folder for every kind of thing.

## Names don't change how the story works

- **Move, don't copy.** After moving a character or item into another file,
  remove its old declaration. Giving two characters the same id is an error,
  not a way to replace one.
- **Keep `game:` in the top-level file.** Title, starting scene, rules, and
  character-creation settings stay under `game:` in `story.talewright.yaml`.
- **Keep one `story.talewright.yaml`.** Don't put another file with that name
  in a subfolder; it won't create a separate story.
- **Keep names unique across the story.** A character named `ferryman` is still
  `ferryman` wherever you put its file. A folder doesn't make characters private,
  add a prefix to their names, or create a scene you can send a choice to.
  Write a scene in a `.tale` file to make a new destination.
- **Use the full ending.** `ferry.talewright.yaml` is read; `ferry.yaml` and
  `ferry.talewright.yml` aren't. Keep `.talewright.yaml` lowercase.
  Reserve that ending for Talewright story settings, not notes or files for
  other tools.

Talewright reads all the story's `.tale` files and supporting settings files.
You don't need to list them or arrange filenames so one loads first. For example,
the player's inventory can name an item in a different file.

## Keep triggers together: order matters

**Put your `triggers:` list in `story.talewright.yaml`, in the order you want
Talewright to check it.** Triggers can change facts that later triggers check,
so changing their order can change what happens.

For example, the first trigger below sets a flag that the second one checks.
Add this whole list to either layout's top-level file:

```yaml
triggers:
  - id: notice-payment
    when: not player has coin
    do: set flag:paid
    once: true
  - id: count-payment
    when: flag:paid
    do: var:crossings += 1
    once: true
```

Only one file may contain `triggers:`. You can move the whole list to a supporting
file, but don't split it across files or use filenames to try to control the order.

## After moving files

Check and reopen your story as you normally would. If you're using the Author
Debugger, restart the story after changing settings files. Keep the whole folder
together when [sharing your story](sharing-your-story.md).

## The Lost Relic: chapters and a related cast

[The Lost Relic](../../samples/LostRelic) keeps shared expedition settings in
one root file and builds features across four chapters:

```text
LostRelic/
  story.talewright.yaml
  chapters/
    01-camp.tale
    02-trail.tale
    03-temple.tale
    04-endings.tale
  dream/
    dream.talewright.yaml
    dream.tale
  images/
```

The root holds the game/rules, player, Tomas, trail items, guild and all triggers,
including the dream's three-mark trigger. The dream settings hold the six-character
cast, glyph item, variables and promise flag beside their scenes.
Camp introduces conversations, supplies and the register; the trail adds checks
and an optional night watch; the temple brings routes together; endings show
their consequences. Scene prefixes (`camp.`, `trail.`, `temple.`, `ending.`)
match that organization because the author wrote them, not because folders
create a namespace.

The optional dream route in the temple uses this supporting configuration, but
its folder creates no private scope: camp can refer to `{ines:name}`, and the
dream can refer to `{guide:first-name}`. Duplicate IDs across either file are
errors, not replacement declarations. Keep all triggers in the root rather than
adding another `triggers:` list under `dream/`. The split ferry example above
uses the same rules. Do not copy Tomas into another file while leaving his root
declaration in place.
See the [sample feature map](../../samples/README.md#feature-map) for exact locations.

For all available settings, see the [syntax reference](../reference/talemark-syntax.md#1-storytalewrightyaml).
