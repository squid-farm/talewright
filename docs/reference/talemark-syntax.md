# Talemark syntax reference

A story is a **folder** containing one `story.talewright.yaml`, optional supporting `*.talewright.yaml` files
in subfolders, and any number of `*.tale` files.
**Talemark** is the story-authoring language that powers Talewright stories, written in `.tale` files.
It lets you mix prose with bold, italic and code formatting, dialogue, choices and story rules. The
YAML manifest is a separate settings file for the story's characters and world; it is not Talemark
source. Talemark supports some Markdown-style formatting, but a `.tale` file is not a general
Markdown document.

New to Talewright? Start with the [author guide](../guide/README.md). The examples below are a quick
start; the rest of this page is the full reference. You can use the examples as patterns and look up
individual features when you need them.

## Quick start: the shape of a story

A story uses two kinds of files:

- `story.talewright.yaml` is the settings file (also called the **manifest**). It gives your story a
  title, tells Talewright which scene to start with, and names the player character.
- A `.tale` file holds the scenes: the words, dialogue and choices the player sees.

For example, the manifest can start like this:

```yaml
game:
  id: my-story
  title: My Story
  version: 0.1.0
  start: crossroads
characters:
  player:
    first-name: Alex
```

Create a `.tale` file beside the manifest, then write a scene like this:

```tale
=== crossroads
@title The Crossroads

Two paths leave the village.

* [Take the forest path] -> forest
* [Go home] -> END

=== forest
@title The Forest

The trees close behind you.

-> END
```

`=== crossroads` starts a **scene** (one part or place in your story); its name is the scene ID.
`@title` is the heading players see. Write ordinary story text on its own lines. A line beginning
with `*` makes a **choice**; the words in brackets are what players click, and `->` says where that
choice goes. `-> END` finishes the story.

To check or play your story without installing the desktop tools, zip its folder and open the zip in
the [web player](https://play.trytalewright.com/). For the full walkthrough, see
[Your first story](../guide/first-story.md).

## How to use this reference

The sections below cover all settings and Talemark features, including optional advanced features.
If you are writing a straightforward story, you can start with the example above and the
[writing guide](../guide/writing-stories.md). You do not need hubs, custom C# rules, or every setting
in this reference to write or share a story.

The whole story is checked when it loads. Mistakes are reported with a file name, line and column,
plus an [error code](diagnostics.md), so you can fix them before playing. C# story projects are an
optional developer feature; see [C# extensions](../developer/csharp-extensions.md).

Identifiers are names for scenes, characters, items and other story parts. Use letters, numbers,
underscores, and single dots or hyphens between words; start with a letter or underscore. For example,
`temple.entrance` and `hidden-door` are valid names. A dot groups words in a name; it does not mean
"look inside" another value.
Lowercase hyphenated words (kebab-case) are a style recommendation, not a parser restriction.
The naming lint rules TWL001/TWL002 warn about style; they do not make an otherwise valid name
illegal.

The [Talemark operator registry](talemark-operators.md) is a deeper technical reference for symbols,
calculation order and quoting rules. Most authors will not need it for a first story.

---

## 1. `story.talewright.yaml`

The root manifest owns `game:`. Supporting configuration files may declare `characters`, `items`,
`factions`, `flags`, and numeric or text `vars`, using the same section shapes as the root.
Each declaration kind has one story-wide namespace: paths do not assign scope, ownership, or precedence.
All declarations are collected before references resolve, so a character can use an item declared
in a different file. The required `player` character may also be in a supporting file.

Configuration filenames end in case-sensitive `.talewright.yaml`; unrelated YAML and `.yml` files
are ignored. No additional configuration file is allowed beside the root manifest.
The basename `story.talewright.yaml` is reserved for that single root manifest;
using it in a subfolder is an error, not a nested story.
Reserve the `.talewright.yaml` ending for story configuration rather than unrelated tool files.
For practical layouts, see [Organizing your story](../guide/organizing-your-story.md).
Supporting files cannot contain `game:`. Duplicate identities are errors naming both sources;
there are no includes, overrides, or filename ordering rules. Only one file may declare `triggers:`,
and its list runs in authored order. **Trigger order affects behavior**; keep the list in the root
manifest unless there is a specific reason to move it. Each configuration file contains one YAML mapping document.
Folder, ZIP, embedded, and published stories use the same inputs.

Moving declarations preserves their meaning, but paths and text remain part of the source fingerprint.
Reorganization is a source change, not a guarantee of compatibility with existing saves.
The debugger requires an explicit restart after configuration additions, edits, renames, or deletions.

```yaml
game:
  id: lost-relic # unique; kebab-case is the recommended style
  title: The Lost Relic
  version: 0.1.0
  start: camp.arrival # first scene id
  randomness: checks-only # off | checks-only (default)
  seed: 1234 # optional; omitted = random per playthrough
  undo: true # default; false hides Back/Forward without disabling saves
  ruleset:
    base: generic # generic | none
    disable: [cha] # remove stats from the base ruleset
    stats: # add stats
      - id: courage
        name: Courage
        category: attribute # attribute | skill | resource | progression | custom
        min: 0
        max: 10
        default: 0

characters:
  player: # the id "player" is required
    first-name: Mara
    last-name: Burns
    pronouns: she # she | he | they | it, or a custom set (see below)
    stats: { dex: 14, athletics: 2 }
    inventory: [rope, torch] # or { torch: 3 }
  guide:
    first-name: Tomas
    pronouns: he

factions:
  explorers-guild: { name: "Explorers' Guild", standing: 0 }

items:
  rope: { name: coil of rope }
  relic: { name: Relic of Ysh, modifiers: { wis: 2 } } # held items modify stats

flags: [door-open] # optional; flags/vars are also auto-declared when an effect sets them
vars:
  torches-lit: 0
  weather: "sunny"

triggers:
  - id: guild-turns
    when: faction:explorers-guild <= hostile
    do: set flag:guild-hostile
    once: true
```

For a complete custom skill declaration, character value and check, see
[Add a skill for your setting](../guide/authoring-recipes.md#add-a-skill-for-your-setting).

Every stat declared with `category: skill`, including a replacement for a built-in
skill, may set optional `governing-attribute: attribute-id`. Its target must resolve to an enabled
`category: attribute` stat after `disable` and all stat declarations/replacements.
Declaration order does not matter. Unknown, disabled or non-attribute targets are
errors, as is using the key on any category other than `skill` (the default
category is `custom`).

For generic checks, the total is **die roll + skill value +
floor((governing attribute score - 10) / 2)**. Scores and skill values include
effective modifiers such as held items. An unlinked skill uses only its skill
value; omitting the key on a replacement stat leaves that replacement unlinked.
This does not change the generic formula or allow multiple governing attributes.
With `randomness: off`, the die result is 10.

Custom pronoun set:

```yaml
pronouns:
  {
    subject: xe,
    object: xem,
    possessive-pronoun: { determiner: xyr, independent: xyrs },
    reflexive: xemself,
    agreement: singular,
  }
```

Custom pronoun sets must declare `agreement: singular` or `agreement: plural`.
Built-ins supply this: `she`, `he` and `it` use singular agreement; `they` uses
plural agreement, including when referring to one person ("they are").
Omitted `pronouns` defaults to built-in `they`.

Character `name-agreement: singular|plural` describes the displayed name, independently
of pronoun agreement. It defaults to `singular` and controls name possessives:
`the pack` is singular (`the pack's`), while `the goblins` is plural (`the goblins'`).
For example, `characters: { goblins: { first-name: the goblins, name-agreement: plural, pronouns: they } }`
declares a plural displayed name. A person named Burns using `they` still has a
singular displayed name, rendering `{player:last-name#possessive}` as `Burns's`.
Pronoun possessives use the declared forms (`their` / `theirs`), not name grammar.

Neither field says how many people a character represents. Singular-agreement
pronouns can describe one or more people; plural agreement is not a population
count. Group identity and composition are not modeled by these settings.

**Breaking pre-alpha change:** both the old pronoun-set `plural` key and the
character-root `plural` key are errors, even alongside the new fields. Replace
the pronoun setting with an explicit `agreement: singular|plural` and the
character setting with `name-agreement: singular|plural`, choosing each for its
grammar rather than population. There are no aliases, automatic migration,
fallback or precedence rules for old settings.

### Optional character creation

Without `creation`, the authored protagonist starts unchanged. With it, the shared
player requires a legal selection before play. The `player` declaration remains
required and provides identity, inventory and other stat defaults.

```yaml
game:
  creation:
    name: true
    pronouns: [they, she, he]
    stats:
      str: [8, 12, 16]
    presets:
      explorer:
        label: Explorer
        stats: {str: 12, hp: 15}
      scholar:
        label: Scholar
        stats: {str: 8, hp: 10}
```

All fields are optional. The first listed pronoun and preset are the defaults.

| Field | Rules |
|---|---|
| `name: true` | Players may edit first and last names (trimmed, at most 80 characters each, first name required, no control characters). Fields start with the authored `player` names; **Use default name** restores them. |
| `pronouns` | Distinct built-in keywords. Omit to keep an authored custom pronoun set. |
| `stats` | Up to 32 stats, each with 1–100 distinct whole-number options within the ruleset's bounds. |
| `presets` | Up to 16. Each sets starting stats within ruleset bounds; values for selectable stats must be listed options. |

**Starting values.** For each selectable stat, the default is the preset's value if the preset sets one,
otherwise the authored `player` value. If that isn't a listed option, or neither exists, the first option
is used. An explicit `0` counts as a value. The player's own picks override the preset.

**During play.** Creation happens before the first scene and is replayed identically by Back, Forward
and loading. Saves keep the created character, and Restart keeps it too. Reopening the story from the
library asks again; loading a save then switches to the saved character.

Not included: point-buy, class designers or race systems.

## 2. Scenes

```tale
=== temple.entrance
@title The Sealed Door
@requires player:dex >= 12 or player has rope
@tags temple
@priority 10
@once

Prose paragraph with **strong**, *emphasis* and `code`.
{player:first-name#possessive} torch gutters as cold air spills out.

> guide: Careful, {player:first-name}. Nobody has opened this in a thousand years.

~ var:torches-lit += 1

* [Read the glyphs] (requires: player:int >= 12) -> temple.glyphs
* [Shove the door] { faction:explorers-guild -= 5 } -> temple.hall
```

| Element                                            | Meaning                                                                                        |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `=== id`                                           | Starts a scene. IDs use the shared identifier rules; dots may group words, as in `camp.arrival`. |
| `@title text`                                      | Display title (used for hub choices).                                                          |
| `@requires expr`                                   | A rule that decides whether a scene is available when using a hub.                             |
| `@tags a, b`                                       | Tags for hubs.                                                                                 |
| `@priority n`                                      | Higher sorts first in hubs (default 0).                                                        |
| `@once`                                            | A scene offered by a hub disappears after the player visits it once.                          |
| `@hub tag`                                         | At this scene, offer each available scene with the tag as a choice.                            |
| `@hub all(tag, tag)`                               | Offer scenes that have every listed tag.                                                      |
| `@hub any(tag, tag)`                               | Offer scenes that have at least one listed tag.                                               |
| `@hub-limit N`                                    | Cap generated storylet choices at positive integer N; requires `@hub`.                         |
| blank-line separated text                          | Paragraphs (bold, italic and code formatting).                                                  |
| `> who: text`                                      | Speaker line; `who` must be a character id.                                                    |
| `{image("path", "alt text")}`                      | Image media in any narrative-template context; alt text is optional. Lookup and display are separate features. |
| `~ effect`                                         | Effect batch applied when this line is reached.                                               |
| `* [label] (requires: expr) { effects } -> target` | Choice. `(requires…)` and `{effects}` are optional. Hidden when `requires` is false.           |
| `* (requires: expr) { effects } -> target`         | Textless choice displayed as **Continue**. The requirement and effects are optional.           |
| `-> target`                                        | Leave now; later lines do not run. Shows **Continue**, or a final frame for `-> END`.          |
| `{if expr}` / `{else}` / `{/if}`                   | Conditionals — on their own lines (block) or inside a paragraph (inline).                      |

Metadata lines are only recognized directly after the `===` header. Lines starting with `//` are comments.

**Scenes run top to bottom.** Each line sees the state produced by the lines above it, never by
lines below it. Prose and substitutions render when reached. For example:

```tale
=== drawer
{if not flag:searched}
The drawer is still full.
~ set flag:searched
~ give player coin
You take the coin.
{/if}
* [Leave] -> hall
```

The block condition is decided once. Setting `searched` inside the block does not skip its remaining
lines. Change notes such as "Gained:" appear at the effect line, between the surrounding prose.

**Where a scene goes next.** Every choice, including each outcome of a choice skill check, needs
its own `-> target` or `-> END`. Choices are the scene's final block: after the first choice, only
more choices, conditions containing only choices, comments and blank lines are allowed. Choice
requirements and enclosing conditions are evaluated when reached; choice effects run when chosen.

Omit a choice's label to show **Continue**, including alongside other choices:

```tale
* [Open the door] (requires: flag:ready) -> hall
* (requires: flag:ready) { set flag:left } -> garden
* -> END
```

An omitted label needs an explicit `->` destination and cannot be used with a choice skill check.
An explicitly empty `[]` is not the textless shortcut. **Continue** is currently English for both
textless choices and automatic scene continuations.

A scene-level `->` leaves immediately, showing the prose already rendered with a single **Continue**
(or as the final frame for `-> END`). Put conditional early exits **before** the choices:

```tale
{if faction:gang <= -31}
-> the-gang-hates-you
{/if}
* [Ask for work] -> gang.work
* [Leave] -> street
```

There is no implicit fallback or gather. A scene without a possible choice, divert or hub is a dead end.
Validation warns when its choices may all be unavailable: add an unconditional choice such as
`* -> END`, or make every continuing branch offer one. Hub offers may be empty and do not count as
an unconditional choice. See [TW3013](diagnostics.md#tw3xxx-story-structure) for details.
These are breaking pre-v1 execution rules; the replay fingerprint changed, not the save format.

**Advanced: hub selection.** A scene may have only one `@hub` and one
`@hub-limit`. A hub checks tags, requirements and `@once`, then sorts matching scenes by priority
before applying its limit. Multiple matching tags never duplicate a choice. Explicit choices are
not capped. Generated choices follow written choices when the scene reaches its end. Give a hub an
explicit exit choice; a scene-level `->` leaves immediately instead of offering the hub. These details
matter when building a story around hubs; you can skip them when using direct choices.

For exact selection behavior: tags must be distinct and nonempty; the hub scene itself is never
offered. Higher `@priority` scenes come first, with ties kept in declaration order. The limit applies
only to generated hub choices, not choices written directly in the scene. When checking whether a
scene can ever be reached, Talewright considers all matching tag scenes, even if the current limit
would hide some; their requirements may change later.

**Conditional structure.** A block `{if}` selects one branch once, when reached. Its nested steps run in
order without retesting that condition. Inline conditions are evaluated when their prose line renders.
Several conditional diverts may precede the choices; the first reached divert ends execution.

**Literal braces.** Write `\{` and `\}` for literal braces in prose. A line starting with `*` followed
by a space or `[` is a choice; `*emphasis*` at the start of a line is prose.

## 3. Substitutions

`{reference#modifier#modifier…}`

| Reference                                                                                   | Value                                     |
| ------------------------------------------------------------------------------------------- | ----------------------------------------- |
| `player:first-name`, `player:last-name`, `player:name`                                      | Names (`name` = first + last)             |
| `player:pronoun`                                                                            | Subject pronoun (`she`, `they`)           |
| `player:<stat>`                                                                             | Effective stat value (base + modifiers)   |
| `faction:<id>`                                                                              | Player standing with the faction (number) |
| `faction:<id>:tier`                                                                         | Standing tier name (`friendly`, …)        |
| `faction:<id>:name`, `item:<id>`                                                            | Display names                             |
| `var:<id>`, `flag:<id>`                                                                     | Story variables / flags                   |

Every property separator is `:`. For example, use `faction:explorers-guild:name` and
`faction:explorers-guild:tier`. Dots within an ID remain part of that name.

Any character id can replace `player`.

| Modifier            | Effect                                                                                       |
| ------------------- | -------------------------------------------------------------------------------------------- |
| `#possessive`       | Grammatically correct English possessive (Chicago style): `Mara's`, `Burns's`, `the guards'` |
| `#capitalize`       | First letter upper case                                                                      |
| `#upper` / `#lower` | Case                                                                                         |
| `#a`                | Prefix with `a`/`an`; use `some` for plural or uncountable names                               |
| `#the`              | Prefix with `the`                                                                            |
| `#this` / `#that`   | Prefix with `this`/`that`, or `these`/`those` for plural agreement                               |

**Pronoun properties.** `{player:pronoun}` is shorthand for `{player:pronoun:subject}`.
Choose a stored form with property access, then chain text modifiers after the value:

| Write                                         | `she`     | `they`       |
| --------------------------------------------- | --------- | ------------ |
| `{player:pronoun}`                            | she       | they         |
| `{player:pronoun:subject}`                    | she       | they         |
| `{player:pronoun:object}`                     | her       | them         |
| `{player:pronoun:possessive-pronoun:determiner}`                 | her       | their        |
| `{player:pronoun:possessive-pronoun:independent}`  | hers      | theirs       |
| `{player:pronoun:reflexive}`                  | herself   | themselves   |
| `{player:pronoun:reflexive#capitalize}`       | Herself   | Themselves   |

The determiner goes before a noun (`her book`); the independent possessive stands
alone (`hers`). Custom sets use their declared `possessive-pronoun` values.
These are ordinary text expressions, usable in comparisons and text assignments:

```tale
{if player:pronoun:object == "her"}She recognizes the voice.{/if}
~ var:remembered-word = (player:pronoun:possessive-pronoun:independent)
```

`#possessive` is a text transformation, not a way to select stored pronoun data.
This is a breaking pre-alpha syntax change with no compatibility aliases or migration handling.

**Pronoun agreement.** Supply two wordings with `#one-many("singular", "plural")`:

```tale
{player:pronoun#capitalize} {player:pronoun#one-many("has", "have")} to admit it.
{guide:pronoun#one-many("was", "were")#capitalize} it worth the journey?
```

The first quoted string is selected for singular agreement; the second for plural
agreement. Selection reads the character's effective pronouns at render time,
including the player's character-creation choice. Built-in `she`, `he` and `it`
select the first string; `they` selects the second, including singular they.
Custom sets use their required `agreement` setting.

The selector outputs only the chosen text, not the pronoun. Both strings are
required (empty strings are allowed); authors control the wording, including
nonstandard grammar. Other text modifiers can follow it, as with `#capitalize`.
Inside these quoted arguments, `\"` means a quote and `\\` means a backslash.
Other backslash escapes are errors. Commas, apostrophes, braces and `#` inside a quoted string are text.

In VS Code, hover a selector to see which wording the story's default pronouns choose:

![A hover on #one-many("has", "have") showing the input "she", the selected result "has" and a description of the agreement selector](../images/vscode-hover-substitution.png)

The selector accepts **pronoun and item-name references only**. Item references use
their `name-agreement`, as described below. Character names, text variables,
population and item quantities never select a branch. Unsupported
targets, missing arguments and extra arguments produce TW2006; an unclosed quote
can leave the substitution unclosed (TW1004). This adds no general multi-argument
modifier support or grammar inference.

### Item-name grammar

Declare the displayed name's grammar, not how many inventory units exist.
Items default to `name-agreement: singular` and `uncountable: false`; shorthand
such as `sword: sword` keeps those defaults.

```yaml
items:
  sword: sword
  amulet: {name: amulet}
  pants: {name: pants, name-agreement: plural}
  handcuffs: {name: handcuffs, name-agreement: plural}
  armor: {name: armor, uncountable: true}
  boots: {name: pair of boots}
```

| Name | `#a` | `#the` | `#this` | `#that` | `#one-many("is", "are")` |
|---|---|---|---|---|---|
| sword | a sword | the sword | this sword | that sword | is |
| amulet | an amulet | the amulet | this amulet | that amulet | is |
| pants | some pants | the pants | these pants | those pants | are |
| handcuffs | some handcuffs | the handcuffs | these handcuffs | those handcuffs | are |
| armor | some armor | the armor | this armor | that armor | is |
| pair of boots | a pair of boots | the pair of boots | this pair of boots | that pair of boots | is |

```tale
{item:pants#that#capitalize} {item:pants#one-many("looks", "look")} expensive.
```

This renders "Those pants look expensive." `#one-many` outputs only the chosen
string, singular first. `uncountable` changes `#a` to `some`, not agreement:
armor still takes "this" and "is". `#possessive` also respects name agreement:
`handcuffs'` and `armor's`.

The original item reference retains its grammar throughout a modifier chain:
`{item:pants#upper#one-many("is", "are")}` still gives "are". Inventory quantity
does not matter: one unit of pants remains plural; five swords do not change
the singular displayed name. Assigning `~ var:word = item:pants` copies only the
text "pants", not item identity or grammar. `{var:word#that}` therefore gives
"that pants", and `#one-many` on that variable is invalid.

Choose helpers explicitly. They do not infer context, inflect nouns, count items,
or add pair/suit/unit wording. Author exceptional phrasing directly; `#a` retains
the existing a/an rules, not a pronunciation dictionary. These helpers take no
arguments; only `#one-many` accepts its exact two quoted strings.

### Images

```tale
You unfold {image("maps/old_map-v2", "An old map of the northern coast.")}.

> guide: Look at {image("symbols/crown", "The king's crest")}.
* [Take {image("items/coin", "A silver coin")}] -> END
```

Both arguments are literal double-quoted strings, separated by a comma. The path is required,
relative to the story's `images/` folder, with an optional extension. It is a file path, not a
story identifier, so quote it even when it contains no spaces. Alt text is optional:
`{image("maps/old_map-v2")}`.

Inside either argument, `\"` means a quote and `\\` means a backslash. Other backslash escapes
are errors. Apostrophes, commas, parentheses, brackets and braces are literal within the quotes.
Alt text is not evaluated as a template: `"{player:name}"` remains that literal text.
The parser preserves strings after escape decoding, including empty or whitespace-only alt text.
Whitespace is allowed around `image`, parentheses and arguments.

Images work wherever narrative templates work: prose, dialogue, choice labels, formatting and
conditions. There is no image-specific surface allowlist or author image-size limit. Plain-string
fields such as scene titles do not gain template support. To show the syntax literally in prose,
escape its outer braces: `\{image("camp")\}`. Code formatting alone does not escape a template.
The character reference `{image:name}` still means the `name` property of the character `image`.

Compilation, `talewright validate` and editor analysis check path safety, supported formats,
file existence, ambiguous extensions and missing alt through the shared resolver's
[TW2010–TW2014 diagnostics](diagnostics.md#tw2xxx-names-and-types).
The editor suggests paths from `images/` in the first quoted argument and refreshes
suggestions and diagnostics when files change. Lookup reads paths, not image bytes.
Omitted, empty or whitespace-only alt warns and uses sentence-case filename fallback.
Explicit nonblank alt is preserved after escape decoding. Extensionless paths select
`.png`, `.jpg`, `.jpeg`, `.webp` in that order, warning if more than one matches.
Compiled media stores the selected path including `images/` and the effective alt text.
The native desktop folder player and native folder Author Debugger display these images,
as do the built-in samples in the desktop and web players. Images load only when their
mounted occurrence approaches the viewport. They fit the available width, retain their
aspect ratio, and use the effective alt text. Dialogue, choices, chosen-choice history,
styles and conditionals use the same renderer.

Folder images are **live files**, not a byte snapshot. Within an accepted story generation,
occurrences of the same resolved filename share a read and Blob URL while any loaded holder
remains. Alt text and style stay independent. After the last holder releases it, a later
occurrence reads again and can see edits or fail after deletion. This avoids duplicate copies,
but does not bound retention of many distinct images; no persistent cache or eviction is added. The
debugger refreshes image occurrences on accepted source/image changes; rejected source
keeps the last accepted story and images. Unavailable or undecodable images show their
effective alt and a visible, accessible failure. There is no remote fallback.

ZIP stories and browser uploads retain their compressed archive for lazy image access through
the same renderer. Untrusted archives enforce defensive expansion accounting, distinct from
folder authoring and the browser upload convenience cap. A later image failure preserves the
story and shows effective alt plus an accessible error.
Published web and desktop hosts also use the shared renderer and include the actual image assets
and catalogue. Web publications keep images as external files under `story/images/`, resolved
relative to the application's base path, including deployment in a subfolder.
Serve a published web game through a web server rather than opening its HTML as a local file.
Desktop publications use the adjacent story folder. Keep the complete published output together.
See [sharing and artwork guidance](../guide/sharing-your-story.md#artwork-and-hosting-size) for
hosting-size warnings and platform verification limits.

The old `![alt](path)` form is an error with a quoted `{image("path", "alt")}` replacement.
The proposed `@image(...)` form was not shipped and is not supported.

## 4. Expressions

```
or / and / not            boolean logic
== != < <= > >=           comparison
+ - * /                   integer arithmetic
player has rope           inventory check (any character id)
visited(temple.hall)      has the scene ever been entered
true false 12 "text"      literals
hostile … allied          reputation tier names (compare with faction:<id>)
@CanPickLock              named predicate registered from C# (see C# extensions)
```

Types are checked at compile time (number, bool, text, tier).
Put spaces around subtraction: `player:hp - 3` subtracts three, whereas
`player:hp-3` refers to a stat named `hp-3`. Hyphens and dots are allowed inside identifiers.
See the [operator registry](talemark-operators.md#names-and-values) for exact naming rules.

## 5. Effects

### Text variables

Declare text with a quoted initial value under `vars` in `story.talewright.yaml`:

```yaml
vars:
  weather: "sunny"
  remembered-weather: ""
  numeric-label: "12"
```

Use the same variable references in Talemark:

```text
~ var:remembered-weather = var:weather
~ var:weather = "stormy"
The weather is {var:weather}.
{if var:weather == "stormy"}Find shelter.{/if}
{if var:weather != var:remembered-weather}It has changed.{/if}
```

A variable's initial value fixes its type. Text assignment copies the value at that
moment, not a live link to the source variable. Empty text and non-ASCII text are
preserved. Quoted numbers such as `"12"` are text, not numbers.
Text supports `=` and case-sensitive `==` / `!=`, not arithmetic, `+=`, `-=`,
ordered comparisons, concatenation, or automatic conversion to/from numbers.
### Declaring variables in Talemark

An assignment can introduce a variable without a manifest declaration:

```text
~ var:weather = "stormy"
~ var:remembered-weather = var:weather
```

Declarations are discovered across the whole story before play, so references in
earlier scenes or other files work. Implicit text starts as `""`; implicit numbers
start at zero. Discovery does not execute the assignment: an unvisited scene cannot
change the weather. Assignments only copy values when their effects execute.

Manifest declarations establish types first. Otherwise, the first assignment
establishes the type: `.tale` files are inspected in ordinal path order, effects in
source order (left to right within a list), then trigger effects in manifest order.
That order does not depend on filesystem enumeration or the reader's choices.
Later incompatible assignments are errors, not type changes.

Types can flow through forward references and assignment chains. Arithmetic and
compound assignments require integers. When a chain has no resolvable type (for
example, two variables only assigned from each other), the compiler reports
TW2007: add an explicit manifest initial value to establish the type.

Talemark strings decode `\"` and `\\` exactly once:

```tale
~ var:nickname = "Joel \"big buck\" Bennet"
~ var:path = "C:\\stories"
```

Those values contain ordinary quotes and a single backslash. Spell every literal backslash
as `\\`, including when updating older stories. Other escapes such as `\n` are errors;
this does not change YAML quoting or ordinary dialogue. Punctuation inside the quotes,
including braces, commas and `#`, remains text. See the
[operator registry](talemark-operators.md#reader-text-quotes-and-escapes) and runnable
[TextVariables example](../../examples/TextVariables).

Separate effects with `;` on a `~` line or inside a choice/outcome's `{ }`.
Triggers and leveling settle after the whole batch, before the next line. For example,
`~ var:gold -= 10; var:gold += 7` never exposes the temporary zero to triggers.
Writing those effects on two `~` lines creates two batches instead.

```
player:hp -= 3            stat arithmetic (= += -=), clamped to min/max
faction:explorers-guild += 5
var:torches-lit = 2
set flag:door-open        / clear flag:door-open
give player relic         / give player torch 2
take player rope
```

## 6. Skill checks

Attached to a choice (outcome lines indented below it):

```tale
* [Climb the collapsed wall] skill-check athletics dc 14
  success: -> temple.upper
  failure: { player:hp -= 2 } -> temple.fall
```

Or as a block that resolves at its position in the scene:

```tale
::: skill-check perception dc 12
success: -> temple.hidden-door
failure: -> temple.hall
:::
```

An outcome's effects run there as one batch. An outcome with `->` leaves immediately; without it,
an entry check continues with the next line. Multiple checks and checks inside block conditions
are allowed, but all must precede the final choices. Choice-check outcomes always require targets.

Optional actor: `skill-check guide survival dc 10`. Resolution is defined by the ruleset
(generic: `d20 + attribute modifier + skill rank ≥ DC`). Dice are seeded and recorded as events.
With `randomness: off`, checks are deterministic (`10 + modifiers ≥ DC`).

## 7. Diagnostics

Every compiler message has a stable code (`TW1xxx` syntax, `TW2xxx` names and types, `TW3xxx` story
structure, `TW4xxx` story.talewright.yaml) and a `file:line:column` location. See [error codes](diagnostics.md)
for what each one means and how to fix it.
