# Sample stories

Complete stories to play, read and copy. The desktop player and the
[web player](https://squid-farm.github.io/talewright/) both include them.

## The Lost Relic (`LostRelic/`)

A short treasure hunt (19 scenes in 3 chapter files, three endings) that uses almost every feature.
Mara Burns, a treasure hunter, and her guide Tomas Okafor look for the Relic of Ysh while the
Explorers' Guild watches.

| Feature | Where |
|---|---|
| Hub with storylets (`@priority`, `@once`) | `camp.hub` and `camp.supplies` / `camp.guide` / `camp.journal` in `01-camp.tale` |
| Skill checks on choices (player and NPC actor) | `trail.river` in `02-temple.tale` |
| Entry skill check (resolved on arrival) | `temple.hall` |
| Gated choices (`requires:` item, flag, faction tier), items modifying stats | rope at the river, glyph door, guild hand-over; `charm` (+2 WIS), `relic` (+2 CHA) |
| Faction standing, tiers and a trigger | Explorers' Guild; trigger `guild-wary` fires at unfriendly or worse |
| Pronouns and possessives | prose throughout (`{player:last-name#possessive}` → "Burns's") |
| Inline and block `{if}` conditional prose, XP and level-up | throughout all three chapters |
| Nested `{if}` (block inside block, inline inside block), `and` / `or` with parentheses, `visited(...)` | `ending.keep` in `03-relic.tale` |

**Play it** in the [web player](https://squid-farm.github.io/talewright/), or from a source checkout:
`dotnet run --project src/Talewright.Host.Desktop -- samples/LostRelic`.

## Use a sample as a starting point

Copy `LostRelic/`, change `id` and `title` in `story.yaml`, and start editing. The
[first story tutorial](../docs/guide/first-story.md) explains the basics, and the
[Tale syntax reference](../docs/reference/tale-syntax.md) covers everything else.

## For contributors

Samples aren't part of the engine: engine code never references them, and they aren't included in NuGet
packages. The desktop and web hosts embed everything under `samples/` as built-in stories, and
[GitHub Pages](../docs/developer/releasing.md) republishes them on every `main` push. Integration smoke
tests open the sample; detailed player tests use their own stories defined in C#.
