# Talemark brief for AI writing partners

> **Measured token count:** 1090 (`cl100k_base`, tiktoken 0.14.0; this document, including this line).

Copy or attach this provider-neutral brief when asking an AI assistant to help write or edit a
Talewright story. It summarizes current authoring practices; it is not a complete language
specification and does not guarantee generated content is correct.

## Read the existing story first

- Inspect the whole story folder, including its manifest, `.tale` files and existing declarations.
  Reuse its scene IDs, characters, items, flags and variables. Add new ones only when the request
  requires them, and update the appropriate manifest or supporting configuration file.
- Preserve the author's prose, meaning and structure except where the request asks for a change.
  Make the smallest relevant edit; do not replace files or rewrite unrelated passages.
- Do not invent syntax, operators, registrations or behavior. The [syntax reference](../reference/talemark-syntax.md)
  and [operator registry](../reference/talemark-operators.md) describe what is implemented today.
  The registry's **Planned changes** section is not implemented syntax.

## Keep file roles and story flow clear

- `story.talewright.yaml` is the story's settings file (manifest): it owns `game:` and commonly
  declares characters, items, factions, flags and starting variables. `.tale` files contain scenes,
  prose, dialogue, choices and story logic. YAML is not Talemark.
- A scene begins with `=== scene-id`. Metadata belongs directly below its scene header. Scenes run
  from top to bottom until a divert, choice or end.
- `> character-id:` starts dialogue. Plain lines are reader-facing prose. Talemark is not Markdown:
  only its documented formatting and inline constructs are interpreted.
- `* [label] ... -> scene-id` offers a choice. Every choice needs its own destination or `END`.
  After the first choice, use only more choices, choice-only conditions, comments or blank lines.
- `-> scene-id` is an immediate divert; later lines in that scene do not run. `-> END` ends the
  story. `* -> END` is instead a textless **Continue** choice the player selects.

## Keep expressions, display and effects distinct

- Expressions read values and calculate results; they do not change story state. A condition must
  produce `true` or `false`—numbers are not implicitly true or false.
- `(requires condition)` controls whether a choice is available. An optional `shown` clause can keep
  an unavailable choice visible. A choice's `{ effects }` run only when that choice is selected.
- `~ effect` applies effects at that point in a scene. Effects change state; substitutions and text
  manipulators only display values and never change state.
- Use `:` for property access and `#` for text manipulation in a substitution. Manipulators chain
  left to right. Variables keep the type set by their first declaration or assignment.
- Prose supports Talemark's documented bold/italic forms and escapes for literal braces and
  backslashes. Other Markdown syntax is not implicitly supported; backticks are ordinary text.

For exact syntax, examples and edge cases, use the [author cheat sheet](cheat-sheet.md), the
[Talemark syntax reference](../reference/talemark-syntax.md), and the [operator registry](../reference/talemark-operators.md).
The reference's [manifest section](../reference/talemark-syntax.md#1-storytalewrightyaml) explains
configuration fields; [Organizing your story](organizing-your-story.md) covers supporting files.

## Minimal complete example

`story.talewright.yaml`:

```yaml
game:
  id: night-ferry
  title: The Night Ferry
  version: 0.1.0
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

## Validate the result

Run `talewright validate <story-folder>` after editing, and fix compiler errors before handing back
the story. See [Testing your story](testing-your-story.md#2-check-it-from-the-command-line) for
other ways to validate. Validation checks the source, but it does not play every route; playtest
important branches and preserve the author's intended outcomes.
