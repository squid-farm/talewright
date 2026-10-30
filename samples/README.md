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
This version includes eleven JPEG illustrations: the original four plus river,
night-watch, jaguar and dream-hall scenes and three Sleeper variants.

Opening the sample presents **character creation**: editable names, she/they/he
pronouns, Explorer/Scholar presets and bounded Strength/Courage choices.
**Start story** keeps the authored Mara defaults. Scholar starts with Lucidity 3;
solving the journal adds 2 for either preset. Will governs Lucidity using the
existing generic formula: **die + Lucidity + floor((Will - 10) / 2)**.
Scholar starts with Will 14 (+2); Explorer uses Will 10 (+0).

### Chapter progression and organization

| Chapter | File / scene prefix | What builds on the previous chapter |
|---|---|---|
| 1 — Camp | `chapters/01-camp.tale` / `camp.*` | Arrival, speech, basic storylets, trader, register, journal and watchword |
| 2 — Trail | `chapters/02-trail.tale` / `trail.*` | Player/NPC river checks and rope gate; optional night camp with one encounter offered at a time and a fixed dawn exit |
| 3 — Temple | `chapters/03-temple.tale` / `temple.*` | Glyph/forcing/side-gap entrances and an optional **Close your eyes** dream route converge on an arrival check, optional idol and relic decision |
| 4 — Endings | `chapters/04-endings.tale` / `ending.*` | Guild standing, dream visits, promises and Lucidity affect three endings |

`story.talewright.yaml` holds the game/rules, player, Tomas, trail items, guild
and all triggers, including `dream-ready`. `dream/dream.talewright.yaml` holds
the six dream characters, glyph item, `marks`/`sum` variables and dream promise
flag; `dream/dream.tale` holds their scenes. The four chapter files and
`images/` complete the sample.
Folders do not add scope or prefixes: the chapter scene IDs are authored explicitly.
Dream IDs are global too: `{ines:name}` works at camp. Declaring the same ID in
two settings files is an error, not an override. Keep the single trigger list in
the root to preserve its order.
See [Organizing your story](../docs/guide/organizing-your-story.md) for when to
split settings, how global IDs work and why triggers stay together.

### Feature map

| Feature | Where |
|---|---|
| Character creation, presets, legal stat choices, restore default name | Root manifest; Explorer retains authored Strength, omitted Courage defaults to the first option (3), Scholar starts with Lucidity 3 |
| Ruleset extension/removal, custom bounded stats and governing attribute | Root manifest adds Courage (custom, 0–10), Will (attribute, 1–30) and Lucidity (skill, 0–10, `governing-attribute: will`); disables Performance |
| Built-in/custom pronoun sets, NPC stats | Player creation and Tomas's explicit pronouns/stats in the root manifest |
| Basic hub, `@once`, `@priority`, stable equal-priority order | `camp.hub`; trader and register tie at priority 10, journal follows at 5 |
| Intersection hub and empty-hub fallback | Trader `camp.supplies` uses `all(camp,records)` to offer the unsigned register; if it is already signed, Continue returns to camp |
| Give items, counted inventory, item stat modifiers | Trader gives rope, charm (+2 WIS) and two rations; relic adds +2 CHA |
| Arithmetic, assignment/subtraction, numeric equality/inequality | Ines in `dream.ines` computes `((6 + 2) * 3 / 2 - 1) - 1 = 10` and pays off Tomas's rumor; `supplies` still increments at camp |
| All five pronoun forms, names, chained modifiers, literal braces | `camp.register`: uppercase/full names, subject/object/possessive/independent/reflexive, `#capitalize`, escaped braces |
| Explicit item wording helpers | Opening uses `#a` and `#the`; `camp.register` uses `#a#capitalize`, `#this` and item `#one-many`. Defaults stay singular; see the [plural/uncountable table](../docs/reference/talemark-syntax.md#item-name-grammar) for item declarations. Inventory quantity never changes name grammar. |
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
| Dream intersection hub and cap | `dream.hub`: `all(dream,keeper)`, `@hub-limit 2` offers at most two eligible keepers **per visit**, not two total visits; a fixed waking choice remains |
| Custom pronouns and watchword responses | The Spiral uses xe/xem with singular agreement; current, changed, retired and unrecorded watchwords produce distinct responses |
| NPC check and alternate player checks | The Watcher uses it/its; its Perception roll changes prose, then player Lucidity, Stealth or Courage >=6 gets past; failed checks allow another attempt or waking |
| Plural agreement and NPC inventory transfers | The Hands use plural name agreement and they; each keeper gives one glyph, then the Hands take one as the exit price after the Sleeper |
| Counted glyphs, mirrored variable and trigger gate | `glyph-mark` inventory and `marks` stay equal; root `dream-ready` fires at 3; all 3 must be held to approach the Sleeper, 2 remain after paying |
| Faction gate, standing changes, tier prose | The Archivist's friendly/neutral/hostile prose; promise requires neutral or better (+5), refusal costs 10 standing |
| Lucidity tiers and visited ending state | The Sleeper uses <2 / 2–4 / >=5 prose; `visited(dream.sleeper)`, promise, Lucidity and guild standing change the altar and all three endings |
| State-dependent artwork | `dream.sleeper` selects exactly one low/middle/high Lucidity illustration alongside its prose; descriptive alt text reflects what is visible |
| Titles, speech, formatting, comments, image media | Camp/worktable art, river crossing, night watch, jaguar, temple door/altar and dream illustrations; the dream-hall image appears on arrival, not on every hub visit |
| Save/load, deterministic Back/Forward, restart and title | Shared player; created identity, stats, text variables, counted items and trigger progress survive replay |

### Quick acceptance route

1. Try Scholar, change the name/pronouns, then **Use default name**. It restores
   the name without resetting other selections. Start and check the opening
   agreement; repeat with she, they and he.
2. Continue to camp. **Haggle for supplies** gives two rations.
   **Sign the expedition register** and inspect the pronoun forms.
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
6. Try **Close your eyes** at the temple. Speak to Ines (sum 10), answer the
   Spiral, face the Watcher, hear the guild ghost and accept the Hands' final
   mark. Three marks unlock the Sleeper; paying the Hands on exit leaves two.
   Try Scholar with/without the journal and Explorer for the three Lucidity
   tiers. Wake early to return to the still-sealed door; glyphs, force and side
   gap remain available. No dream visit is required to finish.
7. Search the hall, then take or leave the relic. Try all three endings with and
   without meeting the Sleeper, and with/without the Archivist's promise.
8. Close/reopen and load a save. Publish the folder as a story ZIP, standalone
   web site or desktop game; creation remains part of each build.

Version **`0.4.0`** restructures the sample and intentionally rejects older
sample saves, including `0.3.0`. Incompatible loads report an error visibly
without replacing the active playthrough or saved bytes. There is no migration.
The dream addition retains version `0.4.0`; its changed content fingerprint also
rejects pre-dream `0.4.0` saves visibly. Artwork updates also change the content
fingerprint; there is no save migration.

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
[desktop tools](../docs/guide/getting-started.md#3-get-the-story-checking-tools-cli):
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
