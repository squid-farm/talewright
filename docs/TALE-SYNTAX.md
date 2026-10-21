# `.tale` Syntax — v0

A story is a **folder** containing one `story.yaml` and any number of `*.tale` files (subfolders allowed).
`.tale` is a Markdown superset: prose is CommonMark inline Markdown, plus a small set of line-level directives.

Everything is compiled **once** (at load time for no-code stories, at build time for C# story projects in M2)
into a typed model. All references are validated against the story schema; mistakes are reported as
diagnostics with `file:line:column`, never as runtime surprises.

---

## 1. `story.yaml`

```yaml
id: lost-relic # kebab-case, unique
title: The Lost Relic
version: 0.1.0
start: camp.arrival # first scene id
randomness: checks-only # off | checks-only (default)
seed: 1234 # optional; omitted = random per playthrough

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
vars: { torches-lit: 0 }

triggers:
  - id: guild-turns
    when: faction:explorers-guild <= hostile
    do: set flag:guild-hostile
    once: true
```

Custom pronoun set:

```yaml
pronouns:
  {
    subject: xe,
    object: xem,
    possessive: xyr,
    possessive-pronoun: xyrs,
    reflexive: xemself,
    plural: false,
  }
```

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
| `=== id`                                           | Starts a scene. Ids are kebab-case segments separated by dots.                                 |
| `@title text`                                      | Display title (used for hub choices).                                                          |
| `@requires expr`                                   | Eligibility predicate (storylets / hubs).                                                      |
| `@tags a, b`                                       | Tags for hubs.                                                                                 |
| `@priority n`                                      | Higher sorts first in hubs (default 0).                                                        |
| `@once`                                            | Storylet is ineligible after it was visited once.                                              |
| `@hub tag`                                         | At this scene, offer every eligible scene tagged `tag` as a choice.                            |
| blank-line separated text                          | Paragraphs (inline Markdown).                                                                  |
| `> who: text`                                      | Speaker line; `who` must be a character id.                                                    |
| `![alt](media-id)`                                 | Media placeholder (ignored by the M1 player; reserved for images).                             |
| `~ effect`                                         | Effect applied when the scene is entered (before rendering).                                   |
| `* [label] (requires: expr) { effects } -> target` | Choice. `(requires…)` and `{effects}` are optional. Hidden when `requires` is false.           |
| `-> target`                                        | Scene divert. With no choices, the player sees a single **Continue**. `-> END` ends the story. |
| `{if expr}` / `{else}` / `{/if}`                   | Conditionals — on their own lines (block) or inside a paragraph (inline).                      |

Metadata lines are only recognized directly after the `===` header. Lines starting with `//` are comments.

**Where a scene goes next.** A choice with its own `-> target` (or a skill check) goes there. A choice with
_no_ target — e.g. `* [Take the torch] { give player torch }` — **falls through** to the scene's own
`-> target` lines (like an ink "gather"), and so does a check outcome without `-> target`. A scene must have
at least one way out (choices, `->`, a `::: skill-check` block or `@hub`), otherwise it's a compile error.

**Conditional structure.** Choices, `~` effects and `->` diverts inside a block `{if}` only apply when the
condition holds (a choice's own `requires` is combined with it using `and`). With several `->` lines, the
first whose condition holds wins, so put conditional diverts before an unconditional fallback.

**Literal braces.** Write `\{` and `\}` for literal braces in prose. A line starting with `*` followed
by a space or `[` is a choice; `*emphasis*` at the start of a line is prose.

## 3. Substitutions

`{reference#modifier#modifier…}`

| Reference                                                                                   | Value                                     |
| ------------------------------------------------------------------------------------------- | ----------------------------------------- |
| `player:first-name`, `player:last-name`, `player:name`                                      | Names (`name` = first + last)             |
| `player:pronoun.subject` / `.object` / `.possessive` / `.possessive-pronoun` / `.reflexive` | Pronouns                                  |
| `player:<stat>`                                                                             | Effective stat value (base + modifiers)   |
| `faction:<id>`                                                                              | Player standing with the faction (number) |
| `faction:<id>.tier`                                                                         | Standing tier name (`friendly`, …)        |
| `faction:<id>.name`, `item:<id>`                                                            | Display names                             |
| `var:<id>`, `flag:<id>`                                                                     | Story variables / flags                   |

Any character id can replace `player`.

| Modifier            | Effect                                                                                       |
| ------------------- | -------------------------------------------------------------------------------------------- |
| `#possessive`       | Grammatically correct English possessive (Chicago style): `Mara's`, `Burns's`, `the guards'` |
| `#capitalize`       | First letter upper case                                                                      |
| `#upper` / `#lower` | Case                                                                                         |
| `#a`                | Prefix with `a`/`an`                                                                         |

## 4. Expressions

```
or / and / not            boolean logic
== != < <= > >=           comparison
+ - * /                   integer arithmetic
player has rope           inventory check (any character id)
visited(temple.hall)      has the scene ever been entered
true false 12 "text"      literals
hostile … allied          reputation tier names (compare with faction:<id>)
@CanPickLock              named predicate registered from C# (Tier 1, M2)
```

Types are checked at compile time (number, bool, text, tier).

## 5. Effects

Separated by `;` inside `{ }`, one per `~` line.

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

Or as a block that resolves when the scene is entered:

```tale
::: skill-check perception dc 12
success: -> temple.hidden-door
failure: -> temple.hall
:::
```

Optional actor: `skill-check guide survival dc 10`. Resolution is defined by the ruleset
(generic: `d20 + attribute modifier + skill rank ≥ DC`). Dice are seeded and recorded as events.
With `randomness: off`, checks are deterministic (`10 + modifiers ≥ DC`).

## 7. Diagnostics

Every compiler message has a stable code and a `file:line:column` location.

| Range    | Area                 | Examples                                                                                                                                                                                                                                                                                                                    |
| -------- | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `TW1xxx` | Syntax               | `TW1005` unbalanced `{if}`/`{else}`/`{/if}`, `TW1009` missing `success:`/`failure:`, `TW1011` malformed choice                                                                                                                                                                                                              |
| `TW2xxx` | References and types | `TW2001` unknown character/stat/item/faction, `TW2002` unknown scene, `TW2003` type mismatch, `TW2004` flag/var never set, `TW2005` unknown `@predicate`, `TW2006` unknown `#modifier`                                                                                                                                      |
| `TW3xxx` | Story structure      | `TW3002` dead-end scene, `TW3003` fall-through with nowhere to fall, `TW3005` skill check without a check rule, `TW3007` missing start scene, `TW3010` duplicate scene; warnings `TW3008` empty hub, `TW3009` unreachable scene (only reported once there are no errors, so one broken divert doesn't bury you in warnings) |
| `TW4xxx` | story.yaml           | `TW4001` invalid YAML, `TW4002` missing required key, `TW4003` bad value, `TW4004` unknown ruleset, `TW4005` unknown stat/item; warning `TW4006` unknown key; `TW4007` duplicate trigger id                                                                                                                                 |
