# State, effects and automatic triggers

Only effects change story state. A `~` line runs when reached, again on each visit; a choice's
`{ effects }` runs when selected. Keep entry effects before choices and guard one-time rewards.
Semicolons separate effects in one batch; triggers and levelling settle after the entire batch,
before the next step. Separate `~` lines are separate batches.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: guild-job, title: A Guild Job, start: job, talemark: 1 }
characters:
  player: { first-name: Ada }
items:
  coin: { name: silver coin }
factions:
  guild: { name: The Guild, standing: 0 }
flags: [rewarded, guild-noticed]
vars: { jobs: 0, report: "" }
triggers:
  - id: notice-work
    when: var:jobs >= 1
    do: set flag:guild-noticed
    once: true
```

`story.tale`:

```tale
=== job
{if not flag:rewarded}
~ give player coin 3; var:jobs += 1; faction:guild:standing += 5; set flag:rewarded
~ var:report = "The work is done."
{/if}
{var:report}
{if flag:guild-noticed}The guild has noticed you.{/if}
You hold {player:inventory:coin:count} coins.
* [Read the report again] -> job
* [Finish] -> END
```

`rewarded` prevents repeated payment. The trigger sees the completed batch, then sets `guild-noticed`
before the following prose. Re-entering does not pay again.

## Effect forms

| Purpose | Effect, inside a `~` line or choice/outcome braces |
|---|---|
| Set / clear a flag | `set flag:rewarded` / `clear flag:rewarded` |
| Give / take inventory | `give player coin 3` / `take player coin` |
| Set / change an integer | `var:jobs = 2` / `var:jobs += 1` / `var:jobs -= 1` |
| Assign text | `var:report = "He said \"Done\"."` |
| Change a declared stat | `player:hp -= 2` |
| Change faction standing | `faction:guild:standing += 5` |

An omitted item quantity means one. Any declared character can hold items; counts are read-only,
zero when not held. Do not assign to `player:inventory:coin:count`; use `give`/`take`.
Stat assignments are clamped to the ruleset's minimum/maximum; bare faction references are names,
so mutate `:standing`, not `faction:guild`.

Flags are boolean; variables are integers or text. The initial declaration fixes a variable's type.
Text allows `=` and case-sensitive `==`/`!=`, not arithmetic, concatenation or implicit conversions.
Copying a variable copies its current value, not a live reference.
Effects can introduce flags and variables without YAML declarations, but declare initial values
explicitly when they matter. Implicit flags start false, numbers zero and text empty; discovering an
assignment does not execute it before its scene is visited.

Triggers belong in one YAML `triggers:` list, in authored order. Each has an `id`, boolean `when`
and effect list `do`; `once: true` prevents repeated firing and optional `save: true` makes a
checkpoint. `when` cannot contain a skill check. Do not create triggers that keep making each
other eligible forever. YAML quoting is a separate layer: quote a whole scalar when YAML would
interpret its punctuation; Talemark strings themselves use double quotes with `\"` and `\\`.
