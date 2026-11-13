# Writing stories

A tour of writing stories in **Talemark**, with a short example for each feature.
The [Talemark syntax reference](../reference/talemark-syntax.md)
has every rule and option.

For highlighting, suggestions and live checks as you type, use VS Code with the
[Talewright extension](../../tooling/vscode-talewright/README.md). See
[Getting started](getting-started.md) to set up your editor and check your story.

## Organizing your files

- Put scenes in as many `.tale` files and subfolders as you like. Talewright reads them all.
- Scene ids can use dots to group them, such as `camp.hub` or `temple.entrance`. Lowercase words
  joined by hyphens are the recommended style, not the only accepted spelling.
- Lines starting with `//` are comments that players never see.

Keep your settings in `story.talewright.yaml` while your story is small, and split related
characters and items into smaller files as it grows.
[Organizing your story](organizing-your-story.md) shows both layouts, and how the
[Lost Relic sample](../../samples/LostRelic) is arranged.

## Scenes run top to bottom

Each line sees only the changes above it. Put an effect before the prose that should show its result:

```tale
=== counter
Before: {var:turns}.
~ var:turns += 1
After: {var:turns}.
* [Again] -> counter
* -> END
```

On the first visit this shows "Before: 0" and "After: 1". A block `{if}` chooses its branch once;
changing its condition inside that branch does not skip the remaining lines.
Triggers run after each whole effect batch, and change notes appear where the batch happens.

Keep choices last. Every choice and every choice-check outcome needs its own `->` destination.
A scene-level `->` means **leave now**, not "use this if there are no choices"; later lines do not run.
For an early exit, put the conditional `->` before the choices. Give hubs an explicit exit choice.
The textless `* -> END` is a choice labelled **Continue**, offered in order alongside the other
choices. It only supports a divert. To use a requirement or effects, give the choice a label:

```tale
* [Open the door] (requires flag:ready) -> hall
{if flag:ready}
* -> garden
{/if}
* [Continue] { set flag:left } -> garden
```

The destination is required; write `-> scene.id` or `-> END`.
To keep a choice visible when its requirement is false, append `, shown`. Optional double-quoted
reader text replaces the label only while unavailable and supports substitutions and formatting:

```tale
* [Ramen - 100cr] (requires player:inventory:credits:count >= 100, shown "You need {100 - player:inventory:credits:count} more credits.") -> shop
```

Without `shown`, a choice whose requirement is false stays hidden. An empty literal `shown ""` is
allowed but warns that the unavailable choice will have no text.

## Characters and pronouns

Declare every character under `characters:` in `story.talewright.yaml` or a supporting
settings file. The `player` character is required.

```yaml
characters:
  player: { first-name: Mara, last-name: Burns, pronouns: she }
  guide:  { first-name: Tomas, pronouns: he }
```

Use them in prose, and Talewright takes care of the grammar:

```tale
{player:first-name#possessive} torch gutters.     → Mara's torch gutters.
{player:last-name#possessive} reputation          → Burns's reputation
{player:pronoun#capitalize} hesitates.            → She hesitates.
{player:pronoun:object#capitalize} again?         → Her again?
{player:pronoun:possessive-pronoun:independent}   → hers
> guide: Careful.                                 → dialogue spoken by Tomas
```

Pronouns can be `she`, `he`, `they`, `it`, or a [custom set](../reference/talemark-syntax.md#1-storytalewrightyaml).
Select stored forms with `:subject`, `:object`, `:reflexive`,
`:possessive-pronoun:determiner` or `:possessive-pronoun:independent` after
`player:pronoun`. These properties also work in comparisons and text assignments.
See [substitutions](../reference/talemark-syntax.md#3-substitutions) for everything you can insert and the
`#manipulators` you can apply.
For a complete two-branch example, see [change a name in one place](authoring-recipes.md#change-a-character-or-ship-name-in-one-place).

For wording that changes with a character's chosen pronouns, supply both forms:
`{player:pronoun#capitalize} {player:pronoun#one-many("has", "have")} arrived.`
The first form is singular agreement; the second is plural agreement, including
singular they. This selector also works on item names using their declared grammar,
not on character names or arbitrary text.
For quantity-dependent wording, apply it to the count expression:
`{player:inventory:arrow:count} {player:inventory:arrow:count#one-many("arrow remains", "arrows remain")}.`
The first wording is selected only when the number is exactly 1.
Scene titles are plain text and can't use substitutions, so use neutral titles such as "Ask your
guide about the temple"; put `{guide:first-name}` in prose and choice labels instead.
For recurring item names, use `{item:relic}` rather than repeating the display name in every
passage. Talewright doesn't work out other verb forms for you; write them with `#one-many`.

## Stats and skills

### Turning on RPG rules

A story has no stats or levelling until you add them. [Skill checks](#skill-checks) work on any stat
you declare. Add `ruleset: rpg` to the `game` section to use a familiar d20-style ruleset with
ready-made stats and levelling:

```yaml
game:
  ruleset: rpg
```

It gives every character these stats:

| Kind | Stats |
|---|---|
| Attributes (1–30, default 10) | `str`, `dex`, `con`, `int`, `wis`, `cha` |
| Skills (0–10, default 0) | `athletics`, `acrobatics`, `sleight-of-hand`, `stealth`, `arcana`, `history`, `investigation`, `nature`, `religion`, `animal-handling`, `insight`, `medicine`, `perception`, `survival`, `deception`, `intimidation`, `performance`, `persuasion` |
| Resources | `hp` (default 10) |
| Progression | `xp`, `level` (1–20) |

You can add your own stats (`courage`, `sanity`, ...) or remove ones you don't want. A `ruleset`
section without `base: rpg` declares only your own stats. They work in skill checks, but there is no
levelling. See [`ruleset` in story.talewright.yaml](../reference/talemark-syntax.md#1-storytalewrightyaml)
and [your own stats with skill checks](authoring-recipes.md#your-own-stats-with-skill-checks).
For a complete declaration and check, see [add a skill for your setting](authoring-recipes.md#add-a-skill-for-your-setting).
A skill can also draw on an attribute you declare; see
[link a skill to a custom attribute](authoring-recipes.md#link-a-skill-to-a-custom-attribute).
The Lost Relic sample adds a custom Courage stat and a Lucidity skill linked to a Will attribute.

Change stats with effects: `~ player:hp -= 2` on its own line where the change should happen, or
`{ player:xp += 50 }` on a choice. Values stay within their minimum and maximum.

### Change a built-in stat

Rename Symbol in VS Code can't rename a built-in stat such as `hp`, because the ruleset owns its ID.
To show a different name to players, keep the ID and redeclare the stat in `ruleset.stats` with a new
`name`. A redeclaration replaces the whole definition, so repeat the range and default too:

```yaml
game:
  ruleset:
    base: rpg
    stats:
      - { id: hp, name: Vigor, category: resource, min: 0, max: 999, default: 10 }
```

Your story still writes `player:hp`; players see "Vigor".

To use a different ID, add your own stat, disable the built-in one, and replace `hp` with your ID in
your story files and character `stats:` by hand:

```yaml
game:
  ruleset:
    base: rpg
    disable: [hp]
    stats:
      - { id: vigor, name: Vigor, category: resource, min: 0, max: 999, default: 10 }
```

Keep `xp` and `level` if you want automatic leveling, since leveling looks them up by ID. Built-in
skills use the attributes (`str`, `dex`, ...) as their governing attribute. If you replace an
attribute, redeclare the skills that used it with
[`governing-attribute`](authoring-recipes.md#link-a-skill-to-a-custom-attribute) set to your new ID.

## Items

```yaml
items:
  rope:  { name: coil of rope }
  charm: { name: jade charm, modifiers: { wis: 2 } }   # carrying it gives +2 Wisdom
  pants: { name: pants, name-agreement: plural }
  armor: { name: armor, uncountable: true }
```

```tale
~ give player rope
* [Climb down] (requires player has rope) -> cellar
* [Trade the charm] { take player charm; give player rope } -> market
```

For prose, `{item:pants#that#capitalize} {item:pants#one-many("is", "are")} expensive.`
gives "Those pants are expensive." `#a` produces "some pants" or "some armor";
`#the` always adds "the". Uncountable armor still takes "this armor is".
The defaults are singular agreement and countable names, independent of inventory
quantity. Copying an item's name into a text variable copies text only.
See the [item-name grammar table](../reference/talemark-syntax.md#item-name-grammar).

### Read and spend item counts

Use `{player:inventory:credits:count}` in prose, arithmetic and conditions to read the
quantity held by any character. It returns 0 when the character holds none and is
read-only; change the inventory with `give` and `take` instead of maintaining a
second variable for the same items.

Declare credits, debt and goods as separate items. Both credits and debt are
nonnegative quantities:

```yaml
items:
  credits: {name: credits}
  debt: {name: debt}
  udon: {name: udon}
characters:
  player:
    first-name: Ada
    inventory: {credits: 75}
```

Guard a purchase so it cannot spend more credits than the player holds:

```tale
* [Udon - 50cr] (requires player:inventory:credits:count >= 50, shown "You need 50 credits.") { take player credits 50; give player udon } -> shop
```

For a 100,000-credit purchase, offer full payment or let the player pay what they
have and record the rest as positive debt. The guards keep the debt quantity positive;
effects run in order, and the later `take` sees the state left by the earlier `give`:

```tale
* [Pay in full] (requires player:inventory:credits:count >= 100000) { take player credits 100000 } -> loan.paid
* [Pay what you have and owe the rest] (requires player:inventory:credits:count < 100000) { give player debt 100000 - player:inventory:credits:count; take player credits 100000 } -> loan.promised
```

## Variables and flags

Use **flags** for yes/no facts and **variables** for numbers. They don't need to be declared: setting one
creates it.

```tale
~ set flag:met-the-queen
~ var:bribes += 1

{if flag:met-the-queen}The guards bow.{/if}
* [Bribe again] (requires var:bribes < 3) -> gate
```

Checking a flag or variable that nothing ever sets is reported as a mistake (usually a typo). If it's
intentional, declare it in `story.talewright.yaml`: `flags: [alarm-raised]` or `vars: { bribes: 0 }`.
Flags in a list start as false. Use a mapping to choose starting values, such as
`flags: { alarm-raised: false, guide-trusts-you: true }`. Flags set by effects but not declared also start false.

Variables can also hold text. Declare a quoted initial value in the manifest, then
assign and display it in Talemark:

```yaml
vars:
  weather: "sunny"
```

```text
~ var:weather = "stormy"
The weather is {var:weather}.
{if var:weather == "stormy"}Find shelter.{/if}
```

Text stays text throughout the story; it cannot be mixed with numbers or used in
arithmetic. See [text variables](../reference/talemark-syntax.md#text-variables) for
empty values, copying, and supported operations.

You can also introduce text directly with `~ var:weather = "stormy"` without a
YAML declaration. It is available throughout the story and starts as empty text
until that effect executes. Its type stays fixed, and assignments copy values
rather than link variables. For declaration order and forward references, see
[declaring variables in Talemark](../reference/talemark-syntax.md#declaring-variables-in-talemark).
Use a text variable for details such as a ship name that appears in several branches:
[the recurring-name recipe](authoring-recipes.md#change-a-character-or-ship-name-in-one-place) shows how.

## Factions

```yaml
factions:
  explorers-guild: { name: "Explorers' Guild", standing: 5 }
```

Standing is a number. Its tier name is what players see and what you'll usually compare against:

| Tier | Standing |
|---|---|
| `hostile` | −31 or lower |
| `unfriendly` | −30 to −10 |
| `neutral` | −9 to 9 |
| `friendly` | 10 to 49 |
| `allied` | 50 or higher |

Standing has no upper or lower limit; the tiers just name the ranges. Every story has them, with
or without RPG rules, and you can't change them in `story.talewright.yaml`. (A programmer can change
them with a [custom C# ruleset](../developer/csharp-extensions.md), but you never need to.)

```tale
* [Shove the door] { faction:explorers-guild:standing -= 10 } -> hall
{if faction:explorers-guild:tier >= friendly}The guild speaks well of you.{/if}
```

## Skill checks

Use `skill-check [actor] stat [dc number-expression][, hidden]` in a condition or numeric effect. With
`dc`, it produces the ruleset's success result; without `dc`, it produces the numeric total:

```tale
{if skill-check perception dc 12, hidden}
You spot the loose stone.
{/if}

~ var:roll = skill-check perception
{if var:roll >= 10}You notice the mark.{/if}
{if var:roll >= 15}You identify it.{/if}

* [Climb the wall] (requires skill-check athletics dc 14)
  { var:attempt = skill-check athletics, hidden }
  -> upper

* [Climb with an explicit outcome] skill-check athletics dc 14
  success: -> upper
  failure: { player:hp -= 2 } -> fall
```

An explicit check can also resolve at a particular point in the scene:

```tale
::: skill-check perception dc 12
success: -> hidden-door
failure: -> hall
:::
```

An entry-check outcome without `->` continues with the next line. An outcome with a target leaves
immediately. A check expression is hidden only when you add `, hidden`; the check event and result
still exist, but no check note is shown.

Checks work on any declared stat. With the built-in rulesets, a check rolls a d20 and adds a modifier;
the ruleset determines whether it beats the DC. The modifier depends on the stat's category:

- `attribute`: `floor((score - 10) / 2)`, so 14 adds +2 and 5 adds −3.
- Any other category: the stat's value, plus its linked attribute's modifier if it has one. Stats
  you declare are `custom` unless you say otherwise, so Nerve 3 adds 3. Built-in RPG skills such as
  `athletics` are linked to an attribute.

For stats on a small scale, keep the default `custom` category; see
[your own stats with skill checks](authoring-recipes.md#your-own-stats-with-skill-checks).
Another character can make the check: `skill-check guide survival dc 10`. An expression check resolves
once per visit, and the same result is reused if it is evaluated again. Different authored checks,
including nested checks of the same stat, are independent. `and`, `or`, and `not` short-circuit from
left to right. Checks are not allowed in trigger `when` conditions.

Set `seed: 1234` in `story.talewright.yaml` for repeatable rolls. With `randomness: off`, the ruleset
provides a deterministic no-dice result. [The full reference](../reference/talemark-syntax.md#6-skill-checks)
covers numeric checks, hidden notes, replay and custom rulesets.

## Conditions

Use conditions in choice requirements such as `(requires expression)`, `{if}` and triggers:

```text
player:dex >= 12 and not flag:alarm-raised
player has rope or player:athletics > 3
player visited temple.hall
faction:explorers-guild:tier <= unfriendly
```

See [expressions](../reference/talemark-syntax.md#4-expressions) for the full list.

## Conditional text

Inline, inside a sentence:

```tale
The camp hums{if var:supplies > 0}, crates stacked by the fire{/if}.
```

Or as a block, which can also contain choices, effects and `->` lines:

```tale
{if faction:explorers-guild:tier >= friendly}
> guide: I'll do what I can.
{else}
> guide: Between us, the guild expects you to fail.
{/if}
```

## Hubs

Instead of wiring every option by hand, tag scenes and let a **hub** offer whichever ones are currently
available:

```tale
=== camp
@title The Camp
@hub camp

* [Set out for the temple] -> trail

=== camp.supplies
@title Haggle for supplies
@tags camp
@once
...
-> camp
```

Every scene tagged `camp` whose `@requires` is true appears as a choice, using its `@title`. A scene
can add `, shown "reader text"` to `@requires` to stay visible while unavailable; that reader text
replaces its title only while the requirement is false. `@once`
removes it after the first visit, and `@priority` controls the order alongside written choices.
Written choices have priority 0; generated choices use their scene's `@priority` (also 0 by default).
You can match every listed tag with `@hub camp and records`, or any listed tag with
`@hub night or watch`; use one operator per selector. `@hub-limit 3` caps generated choices only.
Equal-priority written choices stay in authored order before generated choices.
See [scenes](../reference/talemark-syntax.md#2-scenes).

## Triggers

Triggers fire automatically when a condition becomes true, wherever the player is:
Keep one `triggers:` list in `story.talewright.yaml`. **Order matters:** earlier
triggers can change what later ones do. Don't split the list between files.

```yaml
triggers:
  - id: guild-turns
    when: faction:explorers-guild:tier <= hostile
    do: set flag:guild-hostile
    once: true
    save: true
```

`save: true` keeps a checkpoint when this trigger fires. The checkpoint appears in the player's
Saves list with this trigger's ID; saving triggers do not add Talemark syntax.

## Character creation

Let players choose their name, pronouns, a preset and some starting stats before the story begins:

```yaml
game:
  creation:
    name: true
    pronouns: [they, she, he]
    stats:
      str: [8, 12, 16]
    presets:
      explorer: { label: Explorer, stats: { str: 12, hp: 15 } }
      scholar:  { label: Scholar,  stats: { str: 8,  hp: 10 } }
```

Without a `creation` section, the story starts with the `player` you wrote. See
[character creation](../reference/talemark-syntax.md#optional-character-creation) for the limits and defaults.

## Images

Put artwork in an `images/` folder inside your story folder. PNG, JPEG (`.jpg` or `.jpeg`) and
WebP are supported. For example, with a file at `images/places/harbour.png`, write:

```tale
{image("places/harbour.png", "Sailing boats at a foggy harbour")}
```

The path starts **inside** `images/`, so do not include `images/` in the call.
Match the filename's letter case and use `/` between folders. Images work in prose, dialogue,
choice labels, formatting and conditional text. They display in browser and desktop players,
including story ZIPs and published games.

The second argument is **alt text**: a description for readers who cannot see the image and for
cases where the image cannot load. It accepts the same substitutions as the rest of the story.
`{image("places/harbour.png", "{guide:first-name} at the harbour")}` shows the guide's name.
Write `\{` for a literal brace. The path stays a filename, not a substitution.
Use `\"` for a quote inside an argument and `\\` for a backslash.

Checking your story catches missing image files, unsupported formats and unsafe paths.
It warns if alt text is missing or blank; the fallback is a description based on the filename.
Use an explicit extension when possible: without one, Talewright tries `.png`, `.jpg`, `.jpeg`,
then `.webp`, and warns if several files match.
The check does not inspect whether the image's contents can actually be displayed, so play the
story to check the artwork as well.

To change artwork with the game state, put image calls inside ordinary conditions. For example,
the Lost Relic's `dream.sleeper` scene shows one of three views of the same place depending on the
player's Lucidity, each with its own alt text.

Images fit the width of the window. One that fails to load shows its alt text and an error, and the
story carries on. Markdown's `![description](path)` image syntax doesn't work in Talemark.
Before sharing, see [artwork and hosting size](sharing-your-story.md#artwork-and-hosting-size).
See [image syntax](../reference/talemark-syntax.md#images) for the complete rules.

## Other options

- `game.undo: false` in `story.talewright.yaml` hides the **Back** and **Forward** buttons. Saves still work.
- `game.character-sheet` controls what players see on the character sheet. The sheet shows only what your
  story declares: stats, inventory when you declare items, and standing when you declare factions. To keep
  tracking something without showing it, hide its section or an individual stat. `character-sheet: false`
  hides the whole sheet:

  ```yaml
  game:
    ruleset: rpg
    character-sheet:
      stats:
        str: flag:interview.str-known
        wis: false
      inventory: false
      standing: false
  flags: [interview.str-known]
  ```

  Unlisted stats remain visible. Stat conditions are checked against the current state; `shown: false` hides
  the entire stats section. Hidden stats, items and standing still work in conditions and effects, but the
  engine omits its own generated disclosures of hidden stats. Authored story text is unchanged. See
  [the manifest reference](../reference/talemark-syntax.md#1-storytalewrightyaml) for details.
