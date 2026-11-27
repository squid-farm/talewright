# Story files

A story is a folder with one root `story.talewright.yaml` and one or more `.tale` files.
The YAML manifest declares the world; Talemark contains the narrative and executable story logic.
Do not put Talemark scene headers inside YAML.

- Root `game:` holds `id`, `title`, `start` and optional settings such as `version`, `talemark: 1`, `seed`,
  `randomness: off` (no dice), `undo: false` or `autosave: false`.
  Pin `talemark: 1` to preserve the current integer behavior.
- `characters:` must declare `player`. Declare every other speaker, item and faction before use.
  `ruleset: rpg` under `game:` opts into built-in RPG stats and levelling; without it, no stats
  exist unless declared under `game.ruleset.stats`.
- `flags: [name]` starts a boolean flag false. A mapping can set initial values.
  `vars: { count: 0, weather: "clear" }` declares number and text variables, not booleans.
  In Talemark 2, `chosen: item:rope` initializes an item variable; quote it for text instead.
  Number variables can keep fractions in Talemark 2, available starting with v0.8.0.
- Put additional declarations in subfolders in files ending exactly `.talewright.yaml`.
  Only the root manifest can contain `game:`; never put another `story.talewright.yaml` in a
  subfolder. Names are story-wide, not scoped by file or folder. Duplicate declarations are errors.
  Keep the single ordered `triggers:` list together.
- IDs are case-sensitive. Start with an ASCII letter or `_`; use letters, digits and `_`, with
  single dots or hyphens between groups. Prefer lowercase kebab-case. Dots group words in an ID;
  colons access properties. Scene IDs cannot match character IDs; `scene` is reserved for current
  scene references and `inventory` cannot be an item ID.

## Complete example

`story.talewright.yaml`:

```yaml
game:
  id: night-ferry
  title: The Night Ferry
  version: 0.1.0
  talemark: 1
  start: dock
characters:
  player: { first-name: Ada, inventory: [coin] }
  ferryman: { first-name: Wick }
items:
  coin: { name: silver coin }
flags: [paid]
vars: { weather: "clear" }
```

`story.tale`:

```tale
=== dock
@title The Dock

~ var:weather = "foggy"
> ferryman: The {var:weather} hides the far bank.

* [Pay with the coin] (requires player has coin) { take player coin; set flag:paid } -> crossing
* [Leave] -> END

=== crossing
{if flag:paid}The ferryman waves you aboard.{else}The boat stays at the dock.{/if}
You have {player:inventory:coin:count} coins.
{item:coin#the#capitalize} was useful.
-> END
```

Scenes begin with `=== id`; metadata goes directly below the header. Each scene needs a choice,
divert or hub. No fallthrough connects scenes merely because they share a file.
Run `talewright validate <story-folder>` on the saved folder, not on an individual `.tale` file.
