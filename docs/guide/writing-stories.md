# Writing stories

A tour of what you can do, with a short example for each. The [Tale syntax reference](../reference/tale-syntax.md)
has every rule and option.

For VS Code highlighting and scene/choice snippets, install the local
[Tale extension](../../tooling/vscode-talewright/README.md). It helps you edit;
use `talewright validate` to check story correctness.

## Organizing your files

- Put scenes in as many `.tale` files and subfolders as you like. Talewright reads them all.
- Scene ids can use dots to group them, such as `camp.hub` or `temple.entrance`. Ids are lowercase words
  joined by hyphens.
- Lines starting with `//` are comments that players never see.

The [Lost Relic sample](../../samples/LostRelic) uses one file per chapter.

## Characters and pronouns

Declare every character in `story.talewright.yaml`. The `player` character is required.

```yaml
characters:
  player: { first-name: Mara, last-name: Burns, pronouns: she }
  guide:  { first-name: Tomas, pronouns: he }
```

Use them in prose, and Talewright takes care of the grammar:

```tale
{player:first-name#possessive} torch gutters.     → Mara's torch gutters.
{player:last-name#possessive} reputation          → Burns's reputation
{player:pronoun.subject#capitalize} hesitates.    → She hesitates.
> guide: Careful.                                 → dialogue spoken by Tomas
```

Pronouns can be `she`, `he`, `they`, `it`, or a [custom set](../reference/tale-syntax.md#1-storytalewrightyaml).
See [substitutions](../reference/tale-syntax.md#3-substitutions) for everything you can insert and the
`#modifiers` you can apply.

## Stats and skills

By default, stories use a familiar d20-style ruleset:

| Kind | Stats |
|---|---|
| Attributes (1–30, default 10) | `str`, `dex`, `con`, `int`, `wis`, `cha` |
| Skills (0–10, default 0) | `athletics`, `acrobatics`, `sleight-of-hand`, `stealth`, `arcana`, `history`, `investigation`, `nature`, `religion`, `animal-handling`, `insight`, `medicine`, `perception`, `survival`, `deception`, `intimidation`, `performance`, `persuasion` |
| Resources | `hp` (default 10) |
| Progression | `xp`, `level` (1–20) |

You can add your own stats (`courage`, `sanity`, ...), remove ones you don't want, or start from an
empty ruleset. See [`ruleset` in story.talewright.yaml](../reference/tale-syntax.md#1-storytalewrightyaml).

Change stats with effects: `~ player:hp -= 2` on its own line when a scene starts, or
`{ player:xp += 50 }` on a choice. Values stay within their minimum and maximum.

## Items

```yaml
items:
  rope:  { name: coil of rope }
  charm: { name: jade charm, modifiers: { wis: 2 } }   # carrying it gives +2 Wisdom
```

```tale
~ give player rope
* [Climb down] (requires: player has rope) -> cellar
* [Trade the charm] { take player charm; give player rope } -> market
```

## Variables and flags

Use **flags** for yes/no facts and **variables** for numbers. They don't need to be declared: setting one
creates it.

```tale
~ set flag:met-the-queen
~ var:bribes += 1

{if flag:met-the-queen}The guards bow.{/if}
* [Bribe again] (requires: var:bribes < 3) -> gate
```

Checking a flag or variable that nothing ever sets is reported as a mistake (usually a typo). If it's
intentional, declare it in `story.talewright.yaml`: `flags: [alarm-raised]` or `vars: { bribes: 0 }`.

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

Standing has no upper or lower limit; these tiers just label the ranges. They come from the built-in
ruleset and can't be changed in `story.talewright.yaml` yet. A developer can change them with a
[custom C# ruleset](../developer/csharp-extensions.md).

```tale
* [Shove the door] { faction:explorers-guild -= 10 } -> hall
{if faction:explorers-guild >= friendly}The guild speaks well of you.{/if}
```

## Skill checks

Attach a check to a choice:

```tale
* [Climb the wall] skill-check athletics dc 14
  success: -> upper
  failure: { player:hp -= 2 } -> fall
```

Or make it happen as soon as a scene starts:

```tale
::: skill-check perception dc 12
success: -> hidden-door
failure: -> hall
:::
```

A check rolls a d20 and adds the skill and its attribute bonus. Another character can make the check:
`skill-check guide survival dc 10`. Results are shown to the player in the story.

Want the same rolls every time? Set `seed: 1234` in `story.talewright.yaml`. For no dice at all, set
`randomness: off`, which treats every roll as a 10.

## Conditions

Use conditions in `requires:`, `{if}` and triggers:

```text
player:dex >= 12 and not flag:alarm-raised
player has rope or player:athletics > 3
visited(temple.hall)
faction:explorers-guild <= unfriendly
```

See [expressions](../reference/tale-syntax.md#4-expressions) for the full list.

## Conditional text

Inline, inside a sentence:

```tale
The camp hums{if var:supplies > 0}, crates stacked by the fire{/if}.
```

Or as a block, which can also contain choices, effects and `->` lines:

```tale
{if faction:explorers-guild >= friendly}
> guide: I'll do what I can.
{else}
> guide: Between us, the guild expects you to fail.
{/if}
```

## Hubs and storylets

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

Every scene tagged `camp` whose `@requires` is true appears as a choice, using its `@title`. `@once`
removes it after the first visit, and `@priority` controls the order. You can also match several tags
with `@hub all(...)` / `@hub any(...)`, or cap how many show with `@hub-limit 3`.
See [scenes](../reference/tale-syntax.md#2-scenes).

## Triggers

Triggers fire automatically when a condition becomes true, wherever the player is:

```yaml
triggers:
  - id: guild-turns
    when: faction:explorers-guild <= hostile
    do: set flag:guild-hostile
    once: true
```

## Character creation

Let players choose their name, pronouns, a preset and some starting stats before the story begins:

```yaml
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
[character creation](../reference/tale-syntax.md#optional-character-creation) for the limits and defaults.

## Other options

- `undo: false` in `story.talewright.yaml` hides the **Back** and **Forward** buttons. Saves still work.
- `![description](image-id)` reserves a spot for an image. Images aren't displayed yet
  ([#35](https://github.com/squid-farm/talewright/issues/35)).
