# Hubs and storylets

A hub offers scenes matching its tags. It generates choices; it does not automatically divert.
`@requires` controls a scene's generated hub offer, not whether an explicit divert can enter it.
Use `(requires ...)` on a direct choice when it also needs a guard.

Put metadata directly after `=== id`. Tags are comma-separated in `@tags`; a hub selects a tag
or joins tags with `and` (all) or `or` (any). Do not mix `and` and `or`, use parentheses, or
write invented function syntax in a hub selector.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: investigation, title: An Investigation, start: office }
characters:
  player: { first-name: Ada }
flags: [murder-happened]
```

`story.tale`:

```tale
=== office
@title The Office
@hub investigation
@hub-limit 2

Choose your next lead.
* [Wait for the midnight report] (requires not flag:murder-happened) { set flag:murder-happened } -> office
* [Close the case] -> END

=== witness
@title Question the Witness
@choice-label Speak to the witness
@tags investigation
@priority 10
@once

The witness remembers a voice.
* [Return] -> office

=== study
@title Search the Study
@tags investigation
@requires flag:murder-happened, shown "The study is not yet a crime scene."

You discover the body.
* [Return] -> office
```

Initially the witness is available and the study is shown but disabled. After the report, the study
is available. Once visited, the witness is not offered again because of `@once`.
The explicit exit prevents the hub from trapping the player after leads run out.

## Selection rules

- Generated offers use `@choice-label`, then `@title`, then scene ID as their label.
  `@choice-label` accepts reader text; `@title` is plain text.
- `@priority` is an integer; higher offers come first. `@once` has no value and excludes a scene
  once the player has visited it. The hub never offers itself.
- `@hub-limit` is positive and caps generated choices only, never written choices.
  Available offers take precedence over shown-but-unavailable offers when filling the limit.
  Requirements below the selection cutoff are not evaluated.
- Written choices have priority zero. At equal priority, written choices stay in authored order
  before generated offers; generated ties keep scene declaration order.
- A false `@requires` hides the offer unless `, shown` is present. Its optional double-quoted
  reader text replaces the label while unavailable.
- A scene-level `->` leaves immediately rather than displaying hub offers. Use an authored
  exit choice, not a fallback divert after choices.
- Tags do not create local namespaces. Name scenes distinctly and test state combinations:
  compilation cannot guarantee an offer is available in every playthrough.
