# Reader text: dialogue, references and presentation

Plain lines are prose. `> character-id:` starts speech by a declared character; consecutive `>`
lines continue that speaker. Use a bare `>` for a speech paragraph break and a non-`>` line
(normally a blank line) before a new speaker. A colon on a continuation line is ordinary text.

Substitutions use `{expression#manipulator}`. `:` accesses properties; dots remain part of a name.
Manipulators change display, not state, and chain left to right. Text manipulators must precede
formatting manipulators such as `#bold` and `#italic`.

## Complete example

`story.talewright.yaml`:

```yaml
game: { id: lantern, title: The Lantern, start: camp }
characters:
  player: { first-name: Ada, pronouns: they, inventory: { lantern: 1 } }
  guide: { first-name: Sam, pronouns: she }
items:
  lantern: { name: brass lantern }
```

`story.tale`:

```tale
=== camp
@title At Camp

> guide: Bring {item:lantern#a}.
>
> The path is dark.

> player: I'll carry it myself.

{player:pronoun#capitalize} {player:pronoun#one-many("has", "have")} a light.
{item:lantern#the#capitalize#bold} glows.
You carry {player:inventory:lantern:count} {player:inventory:lantern:count#one-many("lantern", "lanterns")}.
**Stay close.** Walk *quietly*. Literal \{braces\} and \\.
{image("lantern.png", "A brass lantern beside the tent")}
-> END
```

For this example, supply an actual `images/lantern.png`. For a text-only story, omit the image
line rather than inventing an asset or pretending a missing file exists.

## Reference and manipulator toolbox

- Names: `{player:first-name}`, `{player:last-name}`, `{player:name}`; any character ID can
  replace `player`. `{item:lantern}` is the item name even if not held.
- Pronouns: `:pronoun` (subject), `:pronoun:object`, `:pronoun:reflexive`,
  `:pronoun:possessive-pronoun:determiner` (their) and
  `:pronoun:possessive-pronoun:independent` (theirs).
- Values: `{var:weather}`, `{flag:ready}`, `{player:hp}` (declared stat),
  `{faction:guild:standing}` (number), `{faction:guild:tier}` (tier).
  Declare names before using these forms.
- Text: `#capitalize`, `#upper`, `#lower`, `#possessive`, `#a`, `#the`, `#this`, `#that`.
  Format with `#bold`/`#italic` last.
- `#one-many("one", "many")` selects wording, not the input value. Numbers use the first
  wording only at exactly one; pronouns use grammatical agreement, including plural agreement
  for singular they; item names use declared `name-agreement`, not held quantity.
  Text variables are not supported inputs to `#one-many`.
- Item declarations may use `name-agreement: plural` and `uncountable: true`; grammar helpers
  do not infer exceptional nouns or count inventory for you.

## Formatting, quotes and images

Talemark is not Markdown: `**bold**`/`__bold__`, `*italic*`/`_italic_` work, but headings,
lists, links and backticks do not acquire Markdown meaning. A leading `* ` is a choice instead.
Line breaks render as written; use soft wrapping in the editor.

In reader text, escape literal punctuation with backslash: `\{`, `\}`, `\*`, `\\`.
Backticks do not escape substitutions. Inside double-quoted expression strings and manipulator
arguments, only `\"` and `\\` are escapes; apostrophes are ordinary text.
Do not quote whole dialogue lines, titles or choice labels unless readers should see those quotes.

Images use `{image("path.png", "description")}`, relative to `images/`. Alt text can contain
substitutions but no nested images. Purely decorative media uses
`{image("ornament.png")#decorative}` without descriptive alt. A choice cannot be named only by
decorative images. Markdown `![alt](path)` is not supported.
