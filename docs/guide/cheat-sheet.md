# Author cheat sheet

[Full Talemark reference](../reference/talemark-syntax.md) |
[Error codes](../reference/diagnostics.md) |
[VS Code extension](../../tooling/vscode-talewright/README.md)

## Story files

`story.talewright.yaml`:

```yaml
game:
  id: my-story
  title: My Story
  version: 0.1.0
  start: camp
  ruleset: rpg # built-in stats and levelling
characters:
  player: { first-name: Alex, inventory: {credits: 3} }
  guide: { first-name: Sam }
items:
  key: { name: brass key }
  credits: { name: credits }
factions:
  guild: { name: Explorers, standing: 0 }
flags: [met-guide]
vars: { weather: "sunny" }
```

`story.tale`:

```tale
=== camp
@title Camp

> guide: Welcome, {player:first-name}.
~ set flag:met-guide; give player credits
* [Ask about the key] (requires flag:met-guide) -> gate
* [Leave] -> END

=== gate
{item:key#the#capitalize} lies ahead.
You have {player:inventory:credits:count} credits.
-> END
```

## Scenes and choices

| Syntax | Use |
|---|---|
| `=== camp` | Start a scene with ID `camp`. |
| `@title Camp` | Set the displayed title and hub choice label. |
| `@tags camp, night` | Tag a scene for hubs. |
| `@requires flag:met-guide` | Require a condition for a hub to offer the scene. |
| `@requires flag:met-guide, shown "Ask me about the guide first."` | Keep an unavailable hub scene visible with optional reader text instead of its title. |
| `@priority 10` / `@once` | Sort higher in hubs / offer only until visited. |
| `@hub camp` | Offer available scenes tagged `camp`. |
| `@hub camp and night` / `@hub camp or night` | Match all / any tags; do not mix `and` and `or`. |
| `@hub-limit 3` | Limit generated hub choices, not authored choices. |
| `> guide: Hello.` | Dialogue from character `guide`. |
| `* [Go] -> gate` | Offer a choice and divert to `gate`. |
| `* [Open] (requires player has key) { take player key } -> gate` | Choice with a condition and effect. |
| `* [Buy] (requires player:inventory:credits:count >= 3, shown "You need 3 credits.") { take player credits 3 } -> shop` | Keep an unavailable choice visible with optional reader text instead of its label. |
| `* -> END` | Textless **Continue** choice. |
| `-> gate` / `-> END` | Divert immediately / finish the story. |
| `// comment` | Comment line; not shown to players. |

Put metadata directly after `===`. Scenes run top to bottom. After the first choice, use only
choices, choice-only conditions, comments or blank lines.

## Prose, images and substitutions

| Syntax | Use |
|---|---|
| `**bold**` / `*italic*` | Prose formatting; `__bold__` and `_italic_` also work. |
| `\{` / `\}` / `\\` | Literal `{`, `}` or `\` in reader text. |
| `{image("key.png", "A brass key")}` | Image at `images/key.png` with alt text. |
| `{image("ornament.png")#decorative}` | Decorative image without descriptive alt text. |
| `{player:first-name}` / `{player:name}` | Character name; any character ID can replace `player`. |
| `{player:pronoun}` / `{player:pronoun:object}` / `{player:pronoun:reflexive}` | Pronoun forms. |
| `{player:pronoun:possessive-pronoun:determiner}` / `{player:pronoun:possessive-pronoun:independent}` | Possessives such as **their** / **theirs**. |
| `{player:hp}` | Effective character stat. |
| `{item:key}` / `{faction:guild}` | Item / faction display name. |
| `{player:inventory:credits:count}` | Quantity held by a character; 0 when not held. |
| `{faction:guild:standing}` / `{faction:guild:tier}` | Numeric standing / named tier. |
| `{var:weather}` / `{flag:met-guide}` | Variable / flag value. |
| `{scene:player:visits}` / `{camp:player:visits}` | Player visits to the current / named scene. |
| `{player:inventory:arrow:count}` | Number of arrows held by the player. |
| `{item:key#the#capitalize}` | Chain text manipulators: **The brass key**. |
| `{player:inventory:arrow:count#one-many("arrow", "arrows")}` | Select wording from a number (first string only for exactly 1). |
| `{player:pronoun#one-many("is", "are")}` | Select wording from pronoun agreement. |
| `{player:first-name#bold}` | Format a substitution. |

Other manipulators: `#upper`, `#lower`, `#possessive`, `#a`, `#this`, `#that`, `#italic`.
Put text manipulators before `#bold` or `#italic`. `#one-many` selects text for a number,
pronoun or item name; it does not print that name. Image paths are relative to `images/`.
Line breaks render as written; backticks are ordinary text.

## Conditions and effects

| Syntax | Use |
|---|---|
| `flag:met-guide` / `not flag:met-guide` | Test a flag / its opposite. |
| `player has key` / `player visited camp` | Test inventory / whether the player visited a scene. |
| `player:inventory:credits:count >= 3 and faction:guild:tier == friendly` | Combine conditions with `and`, `or`, `not`. |
| `==` `!=` `<` `<=` `>` `>=` | Comparisons. |
| `+` `-` `*` `/` | Integer arithmetic; put spaces around subtraction. |
| `{if flag:met-guide}Hello.{else}Who are you?{/if}` | Inline condition; the markers also work on separate lines. |
| `~ set flag:met-guide` / `~ clear flag:met-guide` | Set / clear a flag. |
| `~ give player key 2` / `~ take player key` | Give / take items. |
| `~ give player credits` / `~ var:weather = "rain"` | Change inventory / set text. |
| `~ player:hp -= 2` / `~ faction:guild:standing += 5` | Change a stat / faction standing. |
| `~ set flag:met-guide; give player credits` | Run effects as one batch. |

Variables are numbers or text; their first declaration or assignment fixes the type. Conditions
must be boolean, not numbers.

Trigger in `story.talewright.yaml`:

```yaml
triggers:
  - id: notice
    when: flag:met-guide
    do: set flag:greeted
    once: true
    save: true
```

`once` and `save` are optional; `save: true` keeps a trigger checkpoint.

## Skill checks

Expressions:

```tale
{if skill-check perception dc 12, hidden}You spot the clue.{/if}
~ var:roll = skill-check perception
{if var:roll >= 10}You notice it.{/if}
```

An expression check without `dc` returns a number; with `dc` it returns the ruleset's boolean result.
The same expression resolves once per scene visit. `, hidden` hides its check note, not its event.

Explicit choice outcome:

```tale
* [Climb] skill-check athletics dc 12
  success: -> wall
  failure: -> ground
```

Explicit check at a point in a scene:

```tale
::: skill-check perception dc 12
success: -> secret
failure: -> hall
:::
```

## VS Code defaults

With the Talewright extension installed. Keys can be customized; on macOS, function keys may need
`Fn`. A comma means press the shortcuts in sequence.

| Action | Windows / Linux | macOS |
|---|---|---|
| Command Palette | `Ctrl+Shift+P` or `F1` | `Cmd+Shift+P` or `F1` |
| Trigger suggestions | `Ctrl+Space` | `Ctrl+Space` |
| Quick Fix | `Ctrl+.` | `Cmd+.` |
| Go to definition | `F12` | `F12` |
| Find all references | `Shift+Alt+F12` | `Shift+Option+F12` |
| Rename name everywhere | `F2` | `F2` |
| Scenes in this file | `Ctrl+Shift+O` | `Cmd+Shift+O` |
| Search scenes in workspace | `Ctrl+T` | `Cmd+T` |
| Problems panel | `Ctrl+Shift+M` | `Cmd+Shift+M` |
| Next / previous problem | `F8` / `Shift+F8` | `F8` / `Shift+F8` |
| Show hover | `Ctrl+K`, `Ctrl+I` | `Cmd+K`, `Cmd+I` |
| Toggle `//` comment | `Ctrl+/` | `Cmd+/` |
| Toggle word wrap | `Alt+Z` | `Option+Z` |

**Snippets:** Use **Insert Snippet** from the Command Palette, then choose `tale-scene`,
`tale-choice`, `tale-if`, `tale-image`, `tale-choice-check` or `tale-check`. `Tab` moves between
fields; prefix-plus-`Tab` needs VS Code's optional Tab Completion setting.

**Talewright commands:** type `Talewright` in the Command Palette. They have no shortcuts.

| Command | Does |
|---|---|
| **New Story** | Create a starter story. |
| **Validate Story** | Check the saved story for mistakes. |
| **Run Story** | Play from the beginning (also needs the .NET 10 Runtime). |
| **Open Author Debugger** | Test any scene with the state you choose. |
| **Play From This Scene** | Open the Author Debugger at the scene your cursor is in. |
| **Publish Story ZIP** | Save a story file to share. |
| **Set Up CLI** | Find or download the CLI that the commands above need. |
| **Toggle Substitution Preview Hints** | Show or hide the grey previews after `{...}`. |

`F5` doesn't open the Author Debugger.
