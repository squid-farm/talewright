# Talemark 2 is coming

Starting with Talewright v0.8.0, you can try Talemark 2 by adding `talemark: 2` under `game:` in
your `story.talewright.yaml`:

```yaml
game:
  talemark: 2
```

Talemark 2 lets numbers keep their fractions. For example, `1 / 4` stays `0.25` instead of becoming
`0`. Number variables and resource stats can keep fractional values too, as can items you mark
`divisible: true`.

For now, stories without a `talemark` setting still use Talemark 1. Starting with the first
Talewright 1.0 preview, the planned default changes to Talemark 2. If you've already written an
unmarked story, the new player won't rewrite your files—but its calculations may come out
differently.

To keep the integer math your story uses today, add `talemark: 1` under `game:`. Talemark 1 will
remain available throughout Talewright 1.x, and already-published games keep the player they were
published with. There's no need to rush.

Before switching an existing story, check calculations that depend on division or numbers reaching
their limits. Talemark 2 compares exact values, even when the player rounds them for display. Back
up your saves and test with the player you plan to publish: some saves can continue, while others
may need the older story and player that created them.

The [language-version guide](language-versions.md) walks through the differences and what to check
before you switch.
