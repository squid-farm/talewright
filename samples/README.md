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

**Artwork disclosure:** The eleven JPEG illustrations in *The Lost Relic* are
AI-generated.

Opening the sample presents **character creation**: editable names, she/they/he
pronouns, Explorer/Scholar presets and bounded Strength/Courage choices.
**Start story** keeps the authored Mara defaults. Scholar starts with Lucidity 3;
solving the journal adds 2 for either preset. Will governs Lucidity using the
usual RPG formula: **die + Lucidity + floor((Will - 10) / 2)**.
Scholar starts with Will 14 (+2); Explorer uses Will 10 (+0).

### Chapter progression and organization

| Chapter | File / scene prefix | What builds on the previous chapter |
|---|---|---|
| 1 — Camp | `chapters/01-camp.tale` / `camp.*` | Arrival, speech, basic storylets, trader, register, journal and watchword; an initially doubtful guide and a continued watchword conversation |
| 2 — Trail | `chapters/02-trail.tale` / `trail.*` | Player/NPC river checks and a visibly locked rope gate; optional night camp with one encounter offered at a time, a locked/unlocked bell and a fixed dawn exit |
| 3 — Temple | `chapters/03-temple.tale` / `temple.*` | Glyph/forcing/side-gap entrances (plus a hidden-check climb) and an optional **Close your eyes** dream route converge on an arrival check, optional idol and an altar reading |
| 4 — Endings | `chapters/04-endings.tale` / `ending.*` | A cleared return screen; guild standing, dream visits, promises and Lucidity affect three endings |

`story.talewright.yaml` holds the game/rules, player, Tomas, trail items, guild
and all triggers, including `dream-ready`. `dream/dream.talewright.yaml` holds
the six dream characters, glyph item, `sum` variable and dream promise
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
| Intersection hub and explicit exit | Trader `camp.supplies` uses `camp and records` to offer the unsigned register; **Return to camp** remains available even when the hub is empty |
| Give items, counted inventory, item stat modifiers | Trader gives rope, charm (+2 WIS) and two rations; relic adds +2 CHA |
| Arithmetic, assignment/subtraction, numeric equality/inequality | Ines in `dream.ines` computes `((6 + 2) * 3 / 2 - 1) - 1 = 10` and pays off Tomas's rumor; `supplies` still increments at camp |
| All five pronoun forms, names, chained modifiers, literal braces | `camp.register`: uppercase/full names, subject/object/possessive/independent/reflexive, `#capitalize`, escaped braces |
| Explicit item wording helpers | Opening uses `#a` and `#the`; `camp.register` uses `#a#capitalize`, `#this` and item `#one-many`. Defaults stay singular; see the [plural/uncountable table](../docs/reference/talemark-syntax.md#item-name-grammar) for item declarations. Inventory quantity never changes name grammar. |
| Pronoun agreement selector and possessives | Opening uses `{player:pronoun#one-many("has", "have")}` for singular they as well as she/he; `{player:last-name#possessive}` renders "Burns's" |
| Recurring-name substitutions | `{guide:first-name}`, `{item:relic}`, `{item:journal}` and other references keep prose tied to settings; scene titles use neutral wording because titles are literal |
| Text declarations, copying, empty text, equality/inequality | `camp.watchword*`: record a phrase, keep/change/retire it; `watch-note` keeps the copied value |
| Journal check, flag gate, XP and Lucidity progression | `camp.journal*`: success gives 50 XP, `knows-glyphs` and +2 Lucidity; the flag opens the temple's glyph route |
| Choice skill checks for player and NPC | `trail.river`: swim or ask the guide; rope offers a safe route |
| Initial flag values (mapping form) | Root `flags:` starts `guide-doubtful: true` and `packing-open: false`; `camp.hub` and `trail.far-bank` prose follow the doubt, and asking Tomas in `camp.guide` clears it. The dream settings keep the list form (`flags: [dream-promise]`, all false) as a contrast |
| Shown-but-unavailable choices | `trail.river`: without rope, **Tie off a rope…** stays visible but disabled with replacement text; `trail.bell`: **Ring the bell** (bare `shown`, same label) and **Silence the bell** (replacement text) swap between available and locked as you ring and silence |
| Shown-but-unavailable storylet under `@hub-limit` | `dream.sleeper`: `@requires …, shown "Approach the Sleeper — *N of 3 marks*"` appears locked once the other keepers are spent, then unlocks when the Hands give the third mark. Available offers fill the cap first |
| Independent scene choice labels | `camp.guide`: hub offer **Ask *Tomas* about the temple** (bold name) under the heading *What the Guild Never Wrote Down*; `trail.swept` ends in a bare `* -> trail.far-bank` labelled *Follow Tomas's voice back upstream*. Other choices keep explicit labels or **Continue** |
| Continued conversation | `camp.watchword-record` and `camp.watchword-readback` are `@continues` replies: one passage on screen, separate History steps |
| Screen clearing | `@clear` on `dream.arrival` (entering the dream) and `ending.return` (the journey home); earlier passages stay in History and saves |
| Multi-paragraph speech | `camp.guide`: Tomas speaks three paragraphs with consecutive `>` lines, a bare `>` separator and an extra blank `>` line |
| Authored spacing | `camp.journal`: the three glyphs are laid out as an indented staircase, followed by extra vertical space before the margin note |
| Skill checks as expressions | `trail.swept`: `{if skill-check guide survival dc 12}` NPC check in prose. `temple.side`: `(requires skill-check athletics dc 12, hidden)` reveals a root climb without a check note; **Squeeze through** always remains. `temple.altar`: `~ var:altar-reading = skill-check history` stores one total that drives three reading thresholds |
| Conditional character-sheet stat | `game.character-sheet.stats` shows Lucidity once it is above 0 (Scholar, solved journal) or you enter the dream. Courage and Strength stay visible so creation still offers them |
| Item-name references | `dream.spiral`: `{spiral:inventory:glyph-mark#a#capitalize}` (NPC inventory); `dream.eye-mark`: `{player:inventory:glyph-mark:name}` |
| Numeric `#one-many` | `dream.hub`: *3 more marks / 1 more mark*, computed from the held count; `dream.passage`: *You keep 2 marks*; `trail.bell`: *sounded 0 times / 1 time / 2 times* |
| Union hub, cap, changing eligibility, `not`, visits | `trail.night`: `night or watch`, `@hub-limit 1`; supper, jaguar, signal and bell; **Press on at dawn** always remains available |
| Counted take, clear flag, explicit choice target | `trail.supper*`: eat one of two rations, clear `packing-open`, then go to the supper result |
| Stat clamping, custom-stat trigger, conditional entry effect | `trail.jaguar`: Courage saturates at 10, `courage-earned` sets `steady`, Courage >=6 grants 5 XP |
| Text comparison, conditional divert with fallback | `trail.signal`: a matching nonempty watchword reaches `trail.found`; a changed, retired or unrecorded phrase reaches `trail.wait` |
| Repeatable edge trigger | `trail.bell`: ring/silence/ring counts two alarms; root `watch-bell` has `once: false` |
| Item/flag/faction gates and converging routes | River rope, temple glyphs, force/side-gap alternatives and guild hand-over |
| Entry skill check | `temple.hall`: Perception leads to the idol niche or directly to the altar |
| Faction tiers and trigger, XP and level-up | Guild standing throughout; keeping the relic fires `guild-wary` |
| Nested block/inline conditions, `and`/`or`, parentheses, `player visited <scene>` | `ending.keep` |
| Conditional early exit before choices | `ending.return`: leave immediately for `ending.empty-handed` without the relic; otherwise offer guild/keeper choices |
| Dream intersection hub and cap | `dream.hub`: `dream and keeper`, `@hub-limit 2` offers at most two eligible keepers **per visit**, not two total visits; a fixed waking choice remains |
| Custom pronouns and watchword responses | The Spiral uses xe/xem with singular agreement; current, changed, retired and unrecorded watchwords produce distinct responses |
| NPC check and alternate player checks | The Watcher uses it/its; its Perception roll changes prose, then player Lucidity, Stealth or Courage >=6 gets past; failed checks allow another attempt or waking |
| Plural agreement and NPC inventory transfers | The Hands use plural name agreement and they; each keeper gives one glyph, then the Hands take one as the exit price after the Sleeper |
| Counted glyphs and trigger gate | `player:inventory:glyph-mark:count` shows and checks held marks; root `dream-ready` fires at 3; all 3 must be held to approach the Sleeper, 2 remain after paying |
| Faction gate, standing changes, tier prose | The Archivist's friendly/neutral/hostile prose; promise requires neutral or better (+5), refusal costs 10 standing |
| Lucidity tiers and visited ending state | The Sleeper uses <2 / 2–4 / >=5 prose; `player visited dream.sleeper`, promise, Lucidity and guild standing change the altar and all three endings |
| State-dependent artwork | `dream.sleeper` selects exactly one low/middle/high Lucidity illustration alongside its prose; descriptive alt text reflects what is visible |
| Titles, speech, formatting, comments, image media | Camp/worktable art, river crossing, night watch, jaguar, temple door/altar and dream illustrations; the dream-hall image appears on arrival, not on every hub visit. Distinct earlier images stay visible; an earlier copy of a repeated image collapses into **Reveal image**; `@clear` hides earlier passages and their images from the screen |
| Save/load, deterministic Back/Forward, restart and title | Shared player; created identity, stats, text variables, counted items and trigger progress survive replay |

### Quick acceptance route

1. Try Scholar, change the name/pronouns, then **Use default name**. It restores
   the name without resetting other selections. Start and check the opening
   agreement; repeat with she, they and he.
2. Continue to camp. **Haggle for supplies** gives two rations.
   **Sign the expedition register** and inspect the pronoun forms.
   Alternatively, sign first, then trade: **Return to camp** remains when the register hub is empty.
3. Study the journal: success gives +2 Lucidity and the glyph route; failure
   leaves forcing and the side gap available. Explorer has no Lucidity on the
   sheet until the journal succeeds; Scholar sees it from the start. Read the
   staircase of glyphs and the spaced margin note.
   Before asking Tomas, the camp prose notes that he hasn't said what he thinks
   of your chances (`guide-doubtful` starts true). **Ask Tomas about the temple**
   opens *What the Guild Never Wrote Down* with three spoken paragraphs; back at
   camp he nods instead.
4. Agree on a night watchword, record it, and keep, change or retire it. The
   ledger retains the recorded phrase. The exchange stays in one passage, but
   History lists each reply. Save, Back, Forward and load preserve both values.
5. Set out. Skip the trader to see the rope crossing shown but disabled. Cross
   by rope, swim, or let the guide try a ford. If you are swept away, Tomas's
   survival check decides whether he spotted where you came ashore, and
   **Follow Tomas's voice back upstream** returns you to the route. On the far bank,
   make camp or climb straight to the temple. At night, eat one ration, face
   the jaguar (Courage clamps at 10), and follow the signal. A kept watchword
   answers correctly; other phrases fall back to waiting. Ring, silence and
   ring the tripwire bell; the counter reaches 2 and the unavailable bell action
   is shown disabled each time. **Press on at dawn** also
   lets you skip any remaining encounters.
6. Try **Close your eyes** at the temple; the dream starts on a cleared screen
   and Lucidity joins the sheet. Speak to Ines (sum 10), answer the Spiral,
   face the Watcher and hear the guild ghost. The hub counts down the marks
   still needed. Then **Approach the Sleeper — 2 of 3 marks** is shown
   disabled beside **Ask the Hands for passage**; their final mark unlocks it.
   Paying the Hands on exit leaves two.
   Try Scholar with/without the journal and Explorer for the three Lucidity
   tiers. Wake early to return to the still-sealed door; glyphs, force and side
   gap remain available. No dream visit is required to finish.
7. At the side gap, Explorer (Athletics) can see a route up the fig roots with
   no check note; Scholar usually cannot. **Squeeze through** always remains.
   Search the hall. At the altar, one History check sets how much of the rim
   you read (Scholar usually reads more). Take or leave the relic; the journey
   home starts on a cleared screen. Try all three endings with and without
   meeting the Sleeper, and with/without the Archivist's promise.
8. Close/reopen and load a save. Publish the folder as a story ZIP, standalone
   web site or desktop game; creation remains part of each build.

### Try it in the author tools

These are [testing tools](../docs/guide/testing-your-story.md), not Talemark directives:

- In **Map**, select `trail.river`, `trail.bell` or `dream.sleeper`. Shown
  choices and offers use dotted lines (“shown when unavailable”). The Inspector
  preview runs no entry effects or skill checks, so the `temple.side` climb and
  the `temple.altar` reading stay unresolved until you play them.
- During a debugger run, **Branches** records each choice and hub offer as
  **Available**, **Shown, unavailable** (with its failing `requires` trace),
  **Hidden** or **Dropped by hub limit**. At the dream hub it shows why the
  Sleeper is locked. **Inspect choice** opens a disabled choice without choosing it.
- In **State**, use **Add item…** to give rope or a glyph mark, or tick
  `guide-doubtful`, then **Apply edits**. The locked offers and prose update.
- **Show source** and **Follow current scene** link the playtest to the `.tale`
  file. Save an edit and the debugger reloads and keeps your state. If the
  recorded route no longer replays, it shows **History changed; continuing from
  here with current state.**

### Features that cannot all be enabled in one story

- `randomness: checks-only` and `undo: true` are active. A fixed `seed` is
  commented in the manifest. In a **copy**, set a seed or `randomness: off`
  for reproducible/take-10 checks; use `undo: false` to hide history controls.
- Omitting `ruleset` (no RPG stats or checks) is an alternative to `base: rpg`. Custom player pronouns
  without a picker are another alternative; the guide already uses explicit fields.
- C# named predicates, custom text modifiers, rulesets and build-time validation
  need a compiled host. See the [C# example](../examples/CSharpStory/README.md);
  the no-code CLI does not discover or execute arbitrary C#.
- CLI validation/packaging and author-debugger state inspection, editing, replay
  and guarded reload operate **on** this sample; they are tools, not directives.
  Try overriding `watchword` and `watch-note`, including empty text.
  See the [CLI guide](../docs/reference/cli.md).
- Save migration and point-buy character creation are not supported. Version
  0.6.0 adds the initially true `guide-doubtful` flag and the stored altar
  reading, so 0.5.0 Lost Relic saves do not load.

**Play it** in the [web player](https://play.trytalewright.com/) or with the
[desktop tools](../docs/guide/getting-started.md#3-get-the-story-checking-tools-cli):
`dotnet run --project apps/desktop -- samples/LostRelic`.

## Use a sample as a starting point

Copy `LostRelic/`, change `id` and `title` in `story.talewright.yaml`, and edit.
The [first story tutorial](../docs/guide/first-story.md) and
[syntax reference](../docs/reference/talemark-syntax.md) explain the rest.

## For contributors

Samples are not part of the engine or NuGet packages. The desktop and web hosts embed
them, and each release publishes them to the [web player](https://play.trytalewright.com/)
(see [releasing](../docs/developer/releasing.md)). Lost Relic has targeted runtime and player
acceptance tests; broader player regressions use their own C# story fixtures.
