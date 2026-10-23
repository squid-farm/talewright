# Sample stories

Complete stories to play, read and copy. The desktop player and the
[web player](https://squid-farm.github.io/talewright/) both include them.

## The Lost Relic (`LostRelic/`)

A playable kitchen-sink reference for the supported no-code authoring features:
30 scenes across four chapter files, with three endings. Mara Burns (or your
created explorer) and guide Tomas Okafor look for the Relic of Ysh while the
Explorers' Guild watches. Optional camp encounters demonstrate mechanics without
forcing a tutorial detour into every playthrough.

Opening the sample now presents **character creation**, including editable names,
built-in pronoun choices, presets and bounded stat choices. **Start story** keeps
the authored Mara defaults. The workshop and field exercises are available from
the first camp hub; no edits or temporary copy are needed.

| Feature | Where |
|---|---|
| Character creation, presets, legal stat choices, restore default name | `story.yaml`; Explorer/Scholar, strength and courage. Explorer retains authored strength; omitted courage defaults to the first option, 3, not the later zero |
| Generic ruleset extension/removal, custom bounded stat | `story.yaml`: adds Courage (0–10) and disables unused Performance; field exercise saturates courage at 10 |
| Built-in and explicit custom pronoun sets; NPC stats | player creation and guide declaration in `story.yaml`; register prose in `workshop.identity` |
| Hub with storylets (`@priority`, `@once`) | `camp.hub` and `camp.supplies` / `camp.guide` / `camp.journal` in `01-camp.tale` |
| Intersection hub, cap, stable equal-priority order, empty-hub fallback | `workshop.hub`: `all(workshop,lesson)`, two offers at a time; ledger precedes register on a priority tie; completion returns to camp |
| Union hub, eligibility changes, `not`, visits | `field.hub`: `any(field,practice)`, one offer; signal becomes eligible after resolve; completion returns to camp |
| Skill checks on choices (player and NPC actor) | `trail.river` in `02-temple.tale` |
| Entry skill check (resolved on arrival) | `temple.hall` |
| Gated choices (`requires:` item, flag, faction tier), items modifying stats | rope at the river, glyph door, guild hand-over; `charm` (+2 WIS), `relic` (+2 CHA) |
| Counted give/take inventory, set/clear flags, choice fall-through | `workshop.packing`: gives two rations; eating removes one and clears the packing flag, then follows the scene divert |
| Explicit and auto-declared flags/variables; arithmetic and comparisons | manifest `packing-open`/`supplies`; `workshop.ledger` auto-declares `ledger`, uses `+ - * /`, assignment/subtraction, `==` and `!=` |
| Faction standing, tiers and a trigger | Explorers' Guild; trigger `guild-wary` fires at unfriendly or worse |
| Custom-stat trigger, conditional entry effect | `courage-earned` sets `steady`; `field.resolve` grants conditional XP |
| Repeatable edge-trigger behavior | `field.bell`: ring/silence/ring increments the alarm counter twice; `watch-bell` is not once-only |
| Pronouns and possessives | prose throughout (`{player:last-name#possessive}` → "Burns's") |
| All five pronoun references, full names, chained text modifiers and literal braces | `workshop.identity`: subject/object/possessive/possessive-pronoun/reflexive, `#upper`, `#capitalize`, `#a#capitalize`, escaped braces; existing prose uses `#lower` and `#possessive` |
| Inline and block `{if}` conditional prose, XP and level-up | throughout the chapters |
| Nested `{if}` (block inside block, inline inside block), `and` / `or` with parentheses, `visited(...)` | `ending.keep` in `03-relic.tale` |
| Conditional diverts with unconditional fallback | `field.signal`, and `ending.return` when no relic choices are available |
| Scene titles, speech, Markdown, comments, media placeholder | throughout; opening image is intentionally a **reserved placeholder**, not rendered media |
| Save/load, deterministic Back/Forward, restart and title | available through the shared player; created identity and selected stats survive replay |

### Quick acceptance route

1. Open the sample; try Scholar, change the name/pronouns, then **Use default name**.
   Confirm it restores the name without resetting the other selections; start.
2. Continue to camp. Visit the **expedition workshop**. Pack lunch, eat one ration,
   balance the ledger (10), and sign the register. Finish all lessons to return.
3. Try the **field exercises**. Courage clamps at 10; the signal appears after the
   first exercise. Save here, advance, load, and try Back/Forward. Finish with the
   watch bell: ring, silence, ring again, then leave the drill.
4. Return to camp; haggle for supplies, speak to Tomas, and study the journal.
   Cross the river, explore the temple, and choose an ending. Try keeping the relic
   to see the negative-standing trigger; try handing it over for the guild ending.
5. Close/reopen and load your save. Publish this same folder as a story ZIP,
   standalone web site or universal desktop game; creation is part of each build.

Version `0.2.0` intentionally invalidates saves from the earlier predefined-player
sample: character creation and story content changed. Incompatible loads should
be rejected visibly without replacing your active playthrough or saved bytes.

### Features that cannot all be enabled in one story

- `randomness: checks-only` and `undo: true` are active. A fixed `seed` is shown
  commented in the manifest. In a **copy**, use a fixed seed or `randomness: off`
  to compare reproducible/take-10 checks; use `undo: false` to hide history controls.
  These are alternate settings, not additional simultaneously active mechanics.
- `ruleset.base: none` is an alternative to Generic, not something to combine with
  this Generic-based expedition. Custom player pronouns without a picker are
  another alternative; the guide already demonstrates explicit pronoun fields.
- C# named predicates, custom text modifiers, rulesets and build-time validation
  require an explicit compiled host. The companion
  [C# story example](../examples/CSharpStory/README.md) demonstrates those features;
  the no-code CLI intentionally does **not** discover or run arbitrary C#.
- CLI validation/packaging and author-debugger setup, inspection and guarded
  reload operate **on** this sample. They are tools, not Tale directives.
  See the [CLI guide](../docs/reference/cli.md).
- Media rendering, autosave, save migration, point-buy and live debugger state
  editing are not implemented features and are not claimed by this sample.

**Play it** in the [web player](https://squid-farm.github.io/talewright/), or with the [desktop tools](../docs/guide/getting-started.md#install-the-desktop-tools):
`dotnet run --project apps/desktop -- samples/LostRelic`.

## Use a sample as a starting point

Copy `LostRelic/`, change `id` and `title` in `story.yaml`, and start editing. The
[first story tutorial](../docs/guide/first-story.md) explains the basics, and the
[Tale syntax reference](../docs/reference/tale-syntax.md) covers everything else.

## For contributors

Samples aren't part of the engine: engine code never references them, and they aren't included in NuGet
packages. The desktop and web hosts embed everything under `samples/` as built-in stories, and
[GitHub Pages](../docs/developer/releasing.md) republishes them after Build and all test jobs pass on a
`main` push, verifying assets, sample opening and a choice at the Pages base path. Integration smoke
tests open the sample; detailed player tests use their own stories defined in C#.
