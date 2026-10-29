# Sample stories

Complete stories to play, read and copy. The desktop player and the
[web player](https://play.trytalewright.com/) both include them.

## The Lost Relic (`LostRelic/`)

A four-chapter adventure with three endings. Mara Burns (or your created
explorer) and guide Tomas Okafor look for the Relic of Ysh while the Explorers'
Guild watches. Features build from conversations and supplies at camp to checks
on the trail, branching temple routes and consequences on the return journey.
Optional encounters do useful work in the story rather than sending you to a
separate workshop.

**Artwork disclosure:** The images used in *The Lost Relic* are AI-generated.
This version reuses the existing four JPEG assets.

Opening the sample presents **character creation**: editable names, she/they/he
pronouns, Explorer/Scholar presets and bounded Strength/Courage choices.
**Start story** keeps the authored Mara defaults. Scholar starts with Lucidity 3;
solving the journal adds 2 for either preset. Lucidity is an unlinked skill here;
its governing attribute and dream encounters belong to a later sample update.

### Chapter progression and organization

| Chapter | File / scene prefix | What builds on the previous chapter |
|---|---|---|
| 1 — Camp | `chapters/01-camp.tale` / `camp.*` | Arrival, speech, basic storylets, trader, register, journal and watchword |
| 2 — Trail | `chapters/02-trail.tale` / `trail.*` | Player/NPC river checks and rope gate; optional night camp with one encounter offered at a time and a fixed dawn exit |
| 3 — Temple | `chapters/03-temple.tale` / `temple.*` | Glyph/forcing/side-gap entrances converge on an arrival check, optional idol and relic decision |
| 4 — Endings | `chapters/04-endings.tale` / `ending.*` | Guild standing, nested conditions and conditional diverts lead to three endings |

`story.talewright.yaml` holds the game/rules, player, Tomas, trail items, guild
and all triggers. The four chapter files and `images/` are the rest of the sample;
there is no supporting character configuration or dream folder in this version.
Folders do not add scope or prefixes: the chapter scene IDs are authored explicitly.
See [Organizing your story](../docs/guide/organizing-your-story.md) for when to
split settings, how global IDs work and why triggers stay together.

### Feature map

| Feature | Where |
|---|---|
| Character creation, presets, legal stat choices, restore default name | Root manifest; Explorer retains authored Strength, omitted Courage defaults to the first option (3), Scholar starts with Lucidity 3 |
| Ruleset extension/removal, custom bounded stats | Root manifest adds Courage (custom, 0–10) and Lucidity (skill, 0–10); disables Performance |
| Built-in/custom pronoun sets, NPC stats | Player creation and Tomas's explicit pronouns/stats in the root manifest |
| Basic hub, `@once`, `@priority`, stable equal-priority order | `camp.hub`; trader and register tie at priority 10, journal follows at 5 |
| Intersection hub and empty-hub fallback | Trader `camp.supplies` uses `all(camp,records)` to offer the unsigned register; if it is already signed, Continue returns to camp |
| Give items, counted inventory, item stat modifiers | Trader gives rope, charm (+2 WIS) and two rations; relic adds +2 CHA |
| Arithmetic, assignment/subtraction, numeric equality/inequality | Trader's `ledger` computes `((6 + 2) * 3 / 2 - 1) - 1 = 10`; `supplies` increments |
| All five pronoun forms, names, chained modifiers, literal braces | `camp.register`: uppercase/full names, subject/object/possessive/independent/reflexive, `#capitalize`, `#a#capitalize`, escaped braces |
| Pronoun agreement selector and possessives | Opening uses `{player:pronoun#one-many("has", "have")}` for singular they as well as she/he; `{player:last-name#possessive}` renders "Burns's" |
| Recurring-name substitutions | `{guide:first-name}`, `{item:relic}`, `{item:journal}` and other references keep prose tied to settings; scene titles use neutral wording because titles are literal |
| Text declarations, copying, empty text, equality/inequality | `camp.watchword*`: record a phrase, keep/change/retire it; `watch-note` keeps the copied value |
| Journal check, flag gate, XP and Lucidity progression | `camp.journal*`: success gives 50 XP, `knows-glyphs` and +2 Lucidity; the flag opens the temple's glyph route |
| Choice skill checks for player and NPC | `trail.river`: swim or ask the guide; rope offers a safe route |
| Union hub, cap, changing eligibility, `not`, visits | `trail.night`: `any(night,watch)`, `@hub-limit 1`; supper, jaguar, signal and bell; **Press on at dawn** always remains available |
| Counted take, clear flag, choice fall-through | `trail.supper*`: eat one of two rations, clear `packing-open`, then follow the scene divert |
| Stat clamping, custom-stat trigger, conditional entry effect | `trail.jaguar`: Courage saturates at 10, `courage-earned` sets `steady`, Courage >=6 grants 5 XP |
| Text comparison, conditional divert with fallback | `trail.signal`: a matching nonempty watchword reaches `trail.found`; a changed, retired or unrecorded phrase reaches `trail.wait` |
| Repeatable edge trigger | `trail.bell`: ring/silence/ring counts two alarms; root `watch-bell` has `once: false` |
| Item/flag/faction gates and converging routes | River rope, temple glyphs, force/side-gap alternatives and guild hand-over |
| Entry skill check | `temple.hall`: Perception leads to the idol niche or directly to the altar |
| Faction tiers and trigger, XP and level-up | Guild standing throughout; keeping the relic fires `guild-wary` |
| Nested block/inline conditions, `and`/`or`, parentheses, `visited(...)` | `ending.keep` |
| Conditional choices and unconditional fallback | `ending.return`: guild/keeper choices when carrying the relic, otherwise `ending.empty-handed` |
| Titles, speech, formatting, comments, image media | Throughout; camp/worktable art at camp and night watch, door/altar art in the temple; images render in the player |
| Save/load, deterministic Back/Forward, restart and title | Shared player; created identity, stats, text variables, counted items and trigger progress survive replay |

### Quick acceptance route

1. Try Scholar, change the name/pronouns, then **Use default name**. It restores
   the name without resetting other selections. Start and check the opening
   agreement; repeat with she, they and he.
2. Continue to camp. **Haggle for supplies** gives two rations and balances the
   ledger at 10. **Sign the expedition register** and inspect the pronoun forms.
   Alternatively, sign first, then trade, to see the trader's empty-hub fallback.
3. Study the journal: success gives +2 Lucidity and the glyph route; failure
   leaves forcing and the side gap available. Ask the guide about the temple.
4. Agree on a night watchword, record it, and keep, change or retire it. The
   ledger retains the recorded phrase. Save, Back, Forward and load preserve both values.
5. Set out. Cross by rope, swim, or let the guide try a ford. On the far bank,
   make camp or climb straight to the temple. At night, eat one ration, face
   the jaguar (Courage clamps at 10), and follow the signal. A kept watchword
   answers correctly; other phrases fall back to waiting. Ring, silence and
   ring the tripwire bell; the counter reaches 2. **Press on at dawn** also
   lets you skip any remaining encounters.
6. Enter by glyphs, force or side gap; search the hall, then take or leave the
   relic. Try all three endings and the guild's response to keeping it.
7. Close/reopen and load a save. Publish the folder as a story ZIP, standalone
   web site or desktop game; creation remains part of each build.

Version **`0.4.0`** restructures the sample and intentionally rejects older
sample saves, including `0.3.0`. Incompatible loads report an error visibly
without replacing the active playthrough or saved bytes. There is no migration.
This version has no playable dream sequence.

### Features that cannot all be enabled in one story

- `randomness: checks-only` and `undo: true` are active. A fixed `seed` is
  commented in the manifest. In a **copy**, set a seed or `randomness: off`
  for reproducible/take-10 checks; use `undo: false` to hide history controls.
- `ruleset.base: none` is an alternative to Generic. Custom player pronouns
  without a picker are another alternative; the guide already uses explicit fields.
- C# named predicates, custom text modifiers, rulesets and build-time validation
  need a compiled host. See the [C# example](../examples/CSharpStory/README.md);
  the no-code CLI does not discover or execute arbitrary C#.
- CLI validation/packaging and author-debugger state inspection, editing, replay
  and guarded reload operate **on** this sample; they are tools, not directives.
  Try overriding `watchword` and `watch-note`, including empty text.
  See the [CLI guide](../docs/reference/cli.md).
- Autosave, save migration and point-buy are not implemented or claimed.

**Play it** in the [web player](https://play.trytalewright.com/) or with the
[desktop tools](../docs/guide/getting-started.md#optional-install-the-desktop-tools):
`dotnet run --project apps/desktop -- samples/LostRelic`.

## Use a sample as a starting point

Copy `LostRelic/`, change `id` and `title` in `story.talewright.yaml`, and edit.
The [first story tutorial](../docs/guide/first-story.md) and
[syntax reference](../docs/reference/talemark-syntax.md) explain the rest.

## For contributors

Samples are not part of the engine or NuGet packages. The desktop/web hosts embed
them, and [GitHub Pages](../docs/developer/releasing.md) republishes them after
Build and tests pass on `main`, checking assets and sample opening at its base path.
Lost Relic has targeted runtime and player acceptance tests; broader player
regressions use their own C# story fixtures.
