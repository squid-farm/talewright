# Error codes

Every problem Talewright reports has a code and a location:

Diagnostic codes are defined in assembly-owned catalogs: `CompilerDiagnosticCodes`,
`AuthoringDiagnosticCodes`, `LanguageServerDiagnosticCodes` and `CliDiagnosticCodes`.
These catalogs are the source of truth; the reference tables are checked against them.

```text
chapters/03-temple.tale:14:23: error TW2002: Unknown scene 'temple.hal'.
└── file ─────────────┘ │  │         └ code ┘ └ message ──────────────┘
                     line column
```

**Errors** stop the story from loading. **Warnings** (marked ⚠) don't, but usually point at a mistake.
When you see several errors, fix the first one and check again: one mistake can cause others.

## TW1xxx: writing mistakes in `.tale` files

| Code | Meaning | Usual fix |
|---|---|---|
| TW1001 | Something unexpected in an expression, including malformed strings and incomplete or extra property segments. | Check spelling and operators, e.g. `player:dex >= 12`. Close text strings with `"`, and use only `\"` or `\\` as string escapes. A possessive pronoun property needs its final `:determiner` or `:independent`. |
| TW1002 | A number is too large, or `@priority` isn't a whole number. | Use a whole number. |
| TW1003 | Malformed effect. | Stats and variables use `=`, `+=` or `-=`. Flags use `set flag:x` / `clear flag:x`. |
| TW1004 | A `{` is never closed. | Close it, or write `\{` for a literal brace. |
| TW1005 | Unbalanced `{if}` / `{else}` / `{/if}`. | Inline conditions must open and close in the same paragraph. Block conditions need their own lines. |
| TW1006 | Bad scene metadata (`@title`, `@hub`, `@hub-limit`, ...). | Put metadata directly after the `===` line, once each. `@hub-limit` needs `@hub`. |
| TW1007 | Malformed dialogue line. | `> who: text`, where `who` is a character id. |
| TW1008 | Invalid scene id. | Use the shared ASCII identifier form: start with a letter or `_`, then use letters, digits, `_`, and single `.` or `-` separators, e.g. `temple.entrance`. |
| TW1009 | Skill check outcomes are missing or misplaced, or an outcome effect block is unclosed. | Put both `success:` and `failure:` lines directly below the check and close each outcome's `{ effects }`. |
| TW1010 | `->` without a target. | `-> scene.id` or `-> END`. |
| TW1011 | Malformed choice or misplaced choice clause. | Use `* [label] (requires ...) { effects } -> target` or `* -> target` for **Continue**. Use at most one requirement and one effect block, in either order before the divert or check. Spell `(requires ` exactly and close `]`, `)` and `}`. |
| TW1012 | Unknown or unclosed `:::` block. | Only `::: skill-check` is supported, and it ends with `:::`. |
| TW1013 | Malformed skill check. | `skill-check [character] skill dc 12`. |
| TW1014 | Malformed image call, image path escape, unsupported image manipulator, or an image nested inside alt text. | Write `{image("path", "alt text")}` or `{image("path")#decorative}`. Quote each supplied argument; separate path and alt text with a comma. Paths use only `\"` or `\\`; alt text uses `\"` and the reader-text punctuation escape rule. Describe a picture in words instead of nesting `{image(...)}`. |
| TW1015 | Unexpected syntax in reader text, including the simple `![alt](path)` image form. | Use `{image("path", "alt")}` for an image. |
| TW1016 | Expression, condition or formatting nesting exceeds the safe parsing limit. | Reduce nesting. Expressions allow 256 levels; block and inline conditions and formatting spans allow 64. |
| TW1017 | A reader-text line ends with an unpaired backslash. | Remove the trailing `\`; line breaks render as written. |

## TW2xxx: names and types

| Code | Meaning | Usual fix |
|---|---|---|
| TW2001 | Unknown character, stat, item, faction or property. | Fix the spelling, or declare the identifier in `story.talewright.yaml`. Use the documented [pronoun properties](talemark-syntax.md#3-substitutions), including the explicit possessive leaves `:determiner` and `:independent`. |
| TW2002 | Unknown scene. | Fix the spelling, or add the scene. |
| TW2003 | Wrong kind of value, e.g. assigning text to a number, comparing text with a number, or arithmetic on text. | Keep each variable's declared type. Text uses `=` and text-to-text `==` / `!=`; numbers retain arithmetic. Declare text with a quoted initial value under `vars:`. |
| TW2004 | A flag or variable is checked but never set. | Fix the spelling, set it somewhere, or declare it under `flags:` / `vars:`. |
| TW2005 | Unknown `@predicate`. | Named predicates come from C# extensions; check the name or register it. |
| TW2006 | Unknown or invalid `#modifier`, unsupported target, malformed arguments or incompatible modifier chain. | Follow the modifier's documented arguments and output type. `#one-many("singular", "plural")` requires exactly two quoted strings on a pronoun or item-name reference, with singular first. Use `\"` for a quote and `\\` for a backslash inside either string. It does not work on character names, text variables or counts. `#a`, `#the`, `#this`, `#that`, `#bold` and `#italic` take no arguments. Apply text modifiers before `#bold` or `#italic`; formatting modifiers are not allowed in plain-text fields such as image alt text. |
| TW2007 | A variable's integer or text type cannot be inferred, e.g. a circular assignment chain without a known type. | Declare an initial value under `vars:` in the manifest, or fix the unresolved expression. Types are fixed; discovery never executes assignments. |
| TW2010 | Unsafe image path. | Use a relative path inside `images/`, with `/` separators and no parent traversal, absolute path, web address, query string or fragment. |
| TW2011 | Unsupported image format. | Use PNG (`.png`), JPEG (`.jpg` or `.jpeg`), or WebP (`.webp`). |
| TW2012 | Image file not found. | Put the file in the story's `images/` folder and match its path's case exactly. |
| ⚠ TW2013 | An extensionless image path matches several files. | Include the extension to choose explicitly. Otherwise the first match wins in `.png`, `.jpg`, `.jpeg`, `.webp` order. |
| ⚠ TW2014 | Descriptive image alt text is missing, empty or whitespace-only. | Supply descriptive alt text. The fallback is the humanized filename in sentence case, or `Image` if no words remain. Mark an intentionally decorative image with `#decorative` instead. |
| TW2015 | A decorative image also supplies descriptive alt text. | Remove the alt text or remove `#decorative`; an image cannot be both decorative and described. |
| TW2016 | A choice label contains only decorative images, so it has no accessible name. | Add label text, a substitution, or a descriptive image. Text, substitutions and conditions are trusted to provide the name; the compiler does not check what they render. |

Story compilation, `talewright validate` and editor analysis report these image
diagnostics consistently. TW2010–TW2014 point to the image path's opening quote;
TW2015 points to the decorative modifier, and TW2016 to the choice label.
Malformed image calls receive syntax diagnostics instead of resolver errors. Editors
refresh the catalogue and diagnostics when files change. Lookup reads paths, not image
bytes; it does not validate file contents or render images. Successful compilation
stores the selected story-root-relative path in each media node, with literal alt text
or an alt template. Missing or blank alt on a descriptive image still stores the
filename fallback; decorative media stores empty alt text.

For an unknown skill, check the id's spelling and its declaration under `game.ruleset.stats`.
Giving a character a value does not declare the skill. See the complete
[custom skill recipe](../guide/authoring-recipes.md#add-a-skill-for-your-setting).

## TW3xxx: story structure

| Code | Meaning | Usual fix |
|---|---|---|
| TW3001 | Text before the first scene, or no `.tale` files at all. | Start each file with `=== scene.id`. |
| TW3002 | Dead end: a scene with no way out. | Add choices, `-> target` or `-> END`. |
| TW3003 | A choice or choice-check outcome has no explicit target. | Add `-> target` or `-> END` to that choice/outcome. Scene diverts are not fallbacks. Also appears as a side effect of TW2002. |
| TW3005 | The ruleset has no skill-check rule. | Use the `generic` ruleset, or remove the checks. |
| TW3007 | The `start` scene doesn't exist. | Fix `game.start:` in `story.talewright.yaml`. |
| ⚠ TW3008 | A hub has no scenes with its tags. | Tag some scenes, or fix the hub's tag. |
| ⚠ TW3009 | A scene can never be reached. | Link to it, or remove it. Only shown once there are no errors. |
| TW3010 | Two scenes have the same id. | Rename one. The message says where the first one is. |
| TW3011 | A non-choice step follows the scene's first choice, including inside another condition branch. | Move prose, images, effects, diverts and checks before the final choice block. |
| ⚠ TW3012 | A line cannot run because an earlier step always leaves its block. | Move the line before the exit, make the exit conditional, or remove the unreachable line. |
| TW3014 | A scene ID matches a character ID. | Rename either the scene or the character; these IDs must be distinct. |

Validation does not predict which choice requirements or hub offers will be available during play.
If play reaches a scene where none is, the reader sees a "The story cannot continue" note and the
story ends there.

## TW4xxx: `story.talewright.yaml`

| Code | Meaning | Usual fix |
|---|---|---|
| TW4001 | Invalid YAML. | Check indentation (spaces, not tabs) and colons. |
| TW4002 | Something required is missing: `story.talewright.yaml` itself, `game.id`, `game.title`, `game.start`, the `player` character, a character's `first-name`, custom pronoun `agreement`, a stat's `id`, or a trigger's `id`/`when`/`do`. | Add it. Custom pronoun sets require `agreement: singular` or `agreement: plural`. |
| TW4003 | A value is invalid, e.g. not a number, out of range, or not allowed here. Includes invalid identifiers, invalid grammatical number, invalid item `uncountable`, and wrong-category governing-attribute links. | Read the message for the allowed values. Item `name-agreement` must be `singular` or `plural`, and `uncountable` must be `true` or `false`. `governing-attribute` belongs only to `category: skill` and must target `category: attribute`. |
| TW4004 | Unknown ruleset. | Use `generic` or `none`. |
| TW4005 | Unknown stat or item in `story.talewright.yaml`, or an unknown/disabled `governing-attribute` target. | Fix the spelling, or declare it under `game.ruleset.stats` / `items`. Governing attributes must resolve to enabled attributes after `disable` and all stat declarations. |
| TW4006 | Unknown or non-scalar configuration key. | Correct or remove the key; the message lists the expected keys. |
| TW4007 | A declaration identity is repeated, two triggers have the same id, or more than one configuration file declares `triggers:`. | Remove or rename the duplicate; keep one authored trigger list. Cross-file conflicts name both sources. |

## Lint rule codes

Lint rules use stable `TWL` IDs, separate from compiler diagnostic codes. They run on shared source-aware
analysis and do not replace or duplicate compiler diagnostics. Their severities and per-file suppressions
are configured in the optional story-root `talewright.lint.json`; see the [`lint` CLI reference](cli.md#lint).
The naming rules are off by default because naming style is your choice; enable them in that file or
with `--rule`.

| Code | Meaning | Usual fix |
|---|---|---|
| TWL001 | A scene ID is not lowercase kebab-case, optionally grouped with dots. | Use lowercase words separated by hyphens, and dots to group scenes (for example `ending.empty-handed`). |
| TWL002 | The manifest story ID is not lowercase kebab-case. | Use a short lowercase ID with words separated by hyphens (for example `night-ferry`). |

## Command-line codes

`talewright validate` and `talewright lint` report these for problems with the command itself rather than
the story. See the [CLI reference](cli.md#output-contract).

| Code | Meaning |
|---|---|
| TWCLI001 | Invalid command or options. |
| TWCLI002 | An input/output operation failed, an existing publish destination needs `--force` in noninteractive use, the destination changed during publication, or a required file, tool or desktop component is missing. |
| TWCLI003 | `talewright lint` configuration is invalid. |

## Editor and C# project messages

These messages concern editor integration and C# extensions. `TWLSP005` can also appear while
editing an ordinary `.tale` story.

The language server also writes the `TWLSP001`, `TWLSP002` and `TWLSP003` prefixes to stderr
for startup, background-request and analysis failures. These are log prefixes, not diagnostics
shown to the user, and are defined separately in `LanguageServerLogCodes`.

| Code | Meaning | Usual fix |
|---|---|---|
| TW5001 | A C# project uses custom story rules or features that the editor has not been told about. | Configure compiler options or export [authoring metadata](../developer/authoring-analysis.md#c-project-registrations). Editors do not automatically build or run C# project code. |
| TW5002 | The language server cannot analyze the story. | Check the reported error. Fix invalid metadata or inaccessible story files; if the problem persists, inspect the Talewright Language Server output. See [language-server configuration](../developer/language-server.md). |
| TWLSP004 | The language server cannot read or parse `talewright.lint.json`. | Fix the reported configuration error; live analysis continues with default rule settings until the file is valid. |
| TWLSP005 | The editor is showing the first 200 problems in a file and has hidden additional diagnostics. | Fix the displayed problems to reveal the rest, or use CLI validation or lint for the uncapped results. |
