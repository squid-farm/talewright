# Glossary

These are the terms Talewright uses for stories and Talemark. The docs, error messages and editor
help should use these words. Each concept has one word; if another doc uses a different word for
one of these concepts, the glossary term is the one to use.
For the design rules behind new syntax, see the
[syntax principles](../developer/language-changes.md#syntax-principles).

## Story structure

| Term | Meaning | Example |
|---|---|---|
| story | Everything in one game: the manifest, `.tale` files and images, kept together in one folder. | `my-story/` |
| manifest | `story.talewright.yaml`, the story's settings file: title, start scene, characters, items, factions, flags, variables and triggers. Supporting `*.talewright.yaml` files can hold some of these declarations. | `start: crossroads` |
| scene | A part of the story with prose and choices, starting with `=== id`. `@title` gives it an optional heading. | `=== crossroads` |
| hub | A scene that offers other scenes with a tag as choices. | `@hub camp` |
| location | Planned, not yet available ([#163](https://github.com/squid-farm/talewright/issues/163)). There is no location syntax today; use scenes and hubs. | — |
| visit | The player's entry into a scene, including a hub. Talewright counts visits automatically. | `player visited camp` |
| visit count | The number of times the player has entered a scene. `scene:player:visits` counts entries into the current scene. The count increases on entry, before the scene's text runs. | `camp:player:visits` |
| choice | An option offered to the reader, on a line starting with `*`. | `* [Go home] -> END` |
| divert | `->`: moving to another scene, or ending the story with `-> END`. | `-> forest` |
| id | An author-chosen name, unique within its kind. Scene ids cannot match character ids, `scene` is reserved, and `inventory` cannot be an item id. | `temple.entrance` |

## Rules and state

| Term | Meaning | Example |
|---|---|---|
| condition | An expression that is true or false. Numbers are never conditions; compare them instead. | `player:dex >= 12` |
| requirement | A condition that a hub checks before offering a scene (`@requires`), or that a choice needs to appear (`(requires …)`). | `(requires player has rope)` |
| effect | A change to the world: `set`, `clear`, `give`, `take` or an assignment. | `~ give player rope` |
| trigger | A manifest rule that runs effects when its condition becomes true. | `when: player:hp <= 0` |
| flag | An on/off fact the author sets and clears. A flag reference is the value itself. | `set flag:door-open` |
| variable | A named number or text value the author sets. A variable reference is the value itself. | `var:coins += 1` |
| character | A person or group in the story, declared under `characters:`. | `guide:first-name` |
| player | The character the reader controls. Every story declares one. | `{player:first-name}` |
| stat | A number belonging to a character, read as `character:stat`. Held items can change it. | `player:dex` |
| skill | A stat declared with `category: skill`, used by skill checks. | `player:athletics` |
| skill check | A roll against a difficulty, with `success:` and `failure:` outcomes. | `skill-check athletics dc 14` |
| item | Something characters can hold. `item:id` shows its name. Custom item properties are planned ([#293](https://github.com/squid-farm/talewright/issues/293)). | `give player rope` |
| faction | A group in the story. `faction:id` shows its name. | `faction:guild` |
| standing | The player's numeric standing with a faction. | `faction:guild:standing += 5` |
| tier | The named band a faction standing falls in, such as `hostile` or `friendly`. | `faction:guild:tier >= friendly` |
| reference | A name for a story thing. | `item:lantern` |
| property | A value of a referenced thing, written after it with `:`. | `faction:guild:standing` |

## Reader text

| Term | Meaning | Example |
|---|---|---|
| reader text | Prose, dialogue, choice labels and other text the reader sees. | `> guide: Careful.` |
| paragraph | Reader text separated by blank lines. Line breaks inside a paragraph render as written. | Two lines with no blank line between them show as two lines. |
| substitution | `{…}` in reader text, replaced with a value when shown. | `{player:first-name}` |
| manipulator | `#name` at the end of a substitution. It changes how the value is written or displayed, never the world. | `{item:rope#the#capitalize}` |
| shorthand | A reserved-character form of a common manipulator: `*` or `_` for `#italic`, `**` or `__` for `#bold`. | `**{player:first-name}**` |

## Tools and engine

| Term | Meaning | Example |
|---|---|---|
| diagnostic | An error or warning Talewright reports, with a code and a location. See [error codes](diagnostics.md). | `TW1014` |
| storylet | Engine term — not used in author docs. | — |
