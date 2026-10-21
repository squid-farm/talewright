# Samples

Example stories. They are **not** part of the engine: engine code never references them. The two hosts embed
everything under `samples/` as built-in stories. Integration smoke tests compile the sample and open it
from the bundled library; detailed player tests use stable, programmatically defined test stories instead.

## The Lost Relic (`LostRelic/`)

A short treasure hunt (19 scenes in 3 chapter files, three endings) written to exercise every M1
feature. Mara Burns, a treasure hunter, and her guide Tomas Okafor look for the Relic of Ysh while the
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

Play it: `dotnet run --project src/Talewright.Host.Desktop -- samples/LostRelic`.

## Writing your own

Copy `LostRelic/`, edit `story.yaml` and the `.tale` files (see [TALE-SYNTAX.md](../docs/TALE-SYNTAX.md)), then
open the folder with the desktop app or zip it and use "Open a story file" on the web. Compile errors are listed
with file and line.

## Published samples

The [GitHub Pages player](https://squid-farm.github.io/talewright/) includes every
bundled story. It is deployed after Build and all test jobs pass on a `main` push;
the workflow verifies published assets, sample opening, and a choice at the Pages
base path. Sample story contents are embedded in the hosts, never in either NuGet
package. See [repository setup](../README.md#github-actions-and-releases).
