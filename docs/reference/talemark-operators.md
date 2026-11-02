# Talemark operator registry

Talemark is the language inside `.tale` files. This is the authoritative list of its markers and
operators, after the property and pronoun changes in
[#191](https://github.com/squid-farm/talewright/issues/191) and
[#190](https://github.com/squid-farm/talewright/issues/190).
See the [syntax reference](talemark-syntax.md) for complete stories and the
[language-change checklist](../developer/language-changes.md) before proposing new syntax.

**Status matters.** Unless marked **decided, not implemented**, examples below describe the current
compiler. This documentation does not add syntax. The [implementation status](#implementation-status)
section separates decisions from features you can use today.

## Names and values

Story identifiers start with an ASCII letter (`A`-`Z`, `a`-`z`) or `_`. The remaining characters are
ASCII letters, digits or `_`, with optional single `.` or `-` separators between groups.
Separators cannot be adjacent or end a name. Names are case-sensitive.
Lowercase kebab-case is a style recommendation, not a parser restriction. The scene/story
naming lint diagnostics TWL001 and TWL002 are warnings about that style.

- Valid: `weapon.knife`, `camp.arrival`, `_guide`, `north-gate`, `act.2`.
- Invalid: `2doors`, `camp..arrival`, `north.-gate`, `door-`, `café`.
- `weapon.knife` is one name, not a read of `knife` on `weapon`.
- Use `:` for access: `faction:explorers-guild:name`, `player:hp`.
- Use spaces around subtraction: `var:coins - 1`. `var:coins-1` reads the name `coins-1`.
  A minus at the start of a numeric operand means negation: `-3`.

The same story-name character set applies in the compiler, manifest schema and editor grammar.
Expression words such as `true`, `false`, `and`, `or`, `not`, `has` and `visited` have special meanings
in their expression positions; a valid spelling is not a guarantee that a name is available there.
Named predicates use these same case-sensitive identifier rules, including dotted and hyphenated
names such as `@Can.Open-door`. Invalid predicate names are rejected at C# registration.
Modifier names depend on their registration contract.
Pronoun properties use `character:pronoun:subject`, `:object`, `:reflexive`,
`:possessive-pronoun:determiner` and `:possessive-pronoun:independent`.
`character:pronoun` is subject shorthand. Dots remain part of names, not property access.

### Reader text, quotes and escapes

**Decided, not fully implemented:** reader-facing text inside syntax uses double quotes.
Names remain bare. `"Joel's cat"` is text; `weapon.knife` is a name. Single quotes are not a second
Talemark string delimiter: apostrophes are ordinary text.

Backslash is the escape character everywhere. Inside double quotes, `\"` means a literal double
quote and `\\` means a literal backslash. In prose, `\{` and `\}` mean literal braces.
For example, `"She said \"Go\"."` contains `She said "Go".`.
Expression strings, modifier arguments, and image path/alt arguments all decode these escapes.
Other escapes, including `\n` and `\u0041`, are errors. These rules do not introduce a
quoted choice-label spelling or a new formatting feature.

Write `"C:\\stories"` for the text `C:\stories`. A single backslash inside a Talemark string
must now be doubled, including in stories written before escape decoding was implemented.
Malformed expression strings report TW1001; modifier arguments report TW2006. An unclosed
enclosing brace can additionally produce TW1004. Existing scene
titles, dialogue and choice labels still use their current unquoted forms:
`@title The door`, `> guide: Hello.`, `* [Open the door] -> hall`.
Do not add quotes to those forms expecting them to be removed.
Image calls use `{image("path", "alt text")}`. The path is a literal string. Alt text uses the
same template language as prose, including `\{` and `\}` for literal braces. Both arguments
decode `\"` and `\\`. Unknown image escapes produce TW1014. A nested `{image(...)}` in alt text
also produces TW1014.

The manifest is YAML, not Talemark: YAML quoting and escaping still apply there. A Talemark
expression stored in YAML must first survive YAML decoding.

## Structural markers

These markers select a construct rather than taking part in arithmetic. Their precedence is
**structural**: line directives are recognized before prose formatting; braces inside prose are
recognized before bold, italic and code formatting. A code span does not hide a substitution.
Each row gives its arguments and boundaries. Escaping follows the quote/prose rules above;
there is no general escape for a line directive today. Do not assume that `\@` or `\===` escapes one.

| Marker | Meaning, context and arguments | Canonical example | Diagnostics |
|---|---|---|---|
| `===` | Start a scene on its own line; followed by one scene ID. | `=== camp.arrival` | TW1008 invalid ID; TW3010 duplicate; TW3001 text before a scene. |
| `@` | Scene metadata directly after its header. `title` takes text; `requires` a condition; `tags` comma-separated tags; `priority` an integer; `once` no argument; `hub` a tag or `all(...)`/`any(...)`; `hub-limit` a positive integer with `hub`. | `@requires player:hp > 0` | TW1006 misplaced, duplicate or invalid metadata; TW1002 invalid priority; expression errors for conditions. |
| `->` | Immediate divert in a scene, choice or check outcome; takes one scene ID or `END`. Later scene steps do not run. Conditional early exits precede choices; there is no fallback/gather. | `-> camp.arrival` | TW1010 missing target; TW1008 invalid ID; TW2002 unknown target; TW3011 after choices. |
| `*` | Choice at the start of a line (`* ` or `*[`). A prose `[label]` is optional with an explicit `->` target; omitted labels display **Continue**. Optional `(requires: condition)` and `{ effects }` work on either form. A choice skill check needs a label. This is not multiplication in this context. | `* -> hall` | TW1011 malformed choice or textless choice without a divert; TW3003 no usable target. |
| `[` `]` | Bound a choice label; its contents are reader text with substitutions and formatting. The current label scanner skips backslash-escaped closing brackets. | `* [Hello {player:first-name}] -> hall` | TW1011 missing closing bracket. |
| `>` | Dialogue at the start of a line; takes a character ID, `:`, then prose. Not a block quote. | `> guide: Follow me.` | TW1007 malformed speech; TW2001 unknown character. |
| `~` | Effect batch at the start of a line; executes in source order. Triggers settle after the whole semicolon-separated batch. | `~ var:coins += 1` | TW1003 malformed effect; TW3011 after choices; name/type errors. |
| `{` `}` | In prose, delimit a substitution or `if`/`else`/`/if`; in choices and outcomes, delimit effects. Block conditions occupy their own lines. Escape literal prose braces as `\{` and `\}`. | `{if player:hp > 0}Ready{else}Rest{/if}` | TW1004 unclosed brace; TW1005 unmatched condition; TW1011 unclosed effect block. |
| `{image(...)}` | Construct image media in any narrative-template context, including choices and dialogue. Required quoted literal path and optional quoted alt, comma-separated. Alt uses the prose template language and cannot contain a nested image. Whitespace before `(` is allowed. This is a dedicated media construct, not a general function-call language or a property read. | `{image("maps/camp", "{guide:first-name} at the camp")}` | TW1014 malformed call/escape or nested image. Asset validation uses TW2010–TW2014. |
| `:::` | Delimit a skill check executed at this line. Opening takes `skill-check [actor] skill dc number-expression`; closing line has only `:::`. Both outcomes are required; an entry outcome without a target continues at the next line. | `::: skill-check perception dc 12` (close with `:::`) | TW1012 unknown/unclosed block; TW1009 missing outcomes; TW3011 after choices. |
| `:` in labels | Separates fixed control labels from their contents: `requires:`, `success:`, `failure:`. Not a property read in this context. | `success: -> hall` | TW1009 misplaced/missing outcomes; TW1011 malformed choice condition. |
| `//` | Comment when it begins a trimmed line; the rest of the line is ignored. It is not a trailing-comment operator. | `// The guard remembers this.` | None for comment contents. |

`END` is the reserved divert destination that ends the story, not a scene declaration.
`skill-check` and `dc` are words, not new sigils. On a choice, write
`* [Climb] skill-check athletics dc 14` and follow it with `success:` and `failure:` lines.
Choices form the final block, optionally nested in conditions. Every choice and choice-check outcome
requires an explicit target (TW3003). Block conditions select a branch once; inline conditions evaluate
at their prose line. Unreachable steps after a guaranteed exit produce warning TW3012.
Unknown names, incorrect types and malformed expressions can also produce the expression diagnostics
below. See [error codes](diagnostics.md) for the complete messages.

## Value and expression operators

Precedence numbers refer to the [binding order](#binding-order) below. **Structural** means the
operator belongs to a surrounding construct; **name** means it is part of a token, not an operation.
No expression operator has an escape that turns it into a name. Put literal punctuation in text,
using the quoted-text rules when it is inside syntax.

| Operator | Meaning, context and arguments | Binding | Canonical example | Diagnostics |
|---|---|---|---|---|
| `:` | Access a named value/property in expressions and substitutions; also select an effect target. Repeats for nested access. Never use `.` instead. | 1 | `{faction:explorers-guild:name}` | TW2001 unknown name/property or old spelling; TW2004 undeclared flag/variable. |
| `.` | Group words within one identifier, never access a property. | Name | `{item:weapon.knife}` | Invalid identifiers produce TW1001 in expressions, TW1008 for scenes, TW4003 in YAML. |
| `-` in names | Separate groups within a name; not subtraction. | Name | `{player:first-name}` | Same identifier diagnostics as `.`. |
| `#` | Text manipulator in a substitution. Takes the value to its left and returns text; repeated manipulators run left to right. Inside quoted text it is literal content. | After the value expression | `{player:pronoun:reflexive#capitalize}` | TW2006 unknown modifier, bad argument syntax or value. |
| `(` `)` | Group an expression; also bound `visited`'s scene argument, modifier arguments, choice conditions, and hub tag groups. | 1 for grouping; otherwise structural | `{(var:coins + 1) * 2}` | TW1001 expression mismatch; TW2006 modifier; TW1011 choice; TW1006 hub. |
| `"` | Bound text literals, modifier string arguments and image arguments. All decode `\"` and `\\`; no single-quoted Talemark string form. | 1 | `{("a # literal")#capitalize}` | TW1001 malformed expression; TW2006 malformed modifier; TW1014 malformed image; TW1004 enclosing brace not found. |
| `\` | Escape a quote or backslash inside strings, or a brace in prose. | During text scanning | `Literal \{braces\}` | Unsupported string escapes are errors; unescaped `{` can cause TW1004. Not a numeric or boolean operator. |
| `,` | Separate metadata tags, hub arguments and image arguments today; decided separator for multiple double-quoted modifier arguments. Each modifier defines its own values. | Structural | `{image("camp", "The camp")}` | TW1014 invalid image; TW1006 invalid hub; multi-argument modifiers are not implemented (TW2006). |
| Unary `-` | Negate one number. | 2 | `{var:coins + -3}` | TW2003 non-number; TW1002 oversized literal. |
| `*` `/` | Integer multiplication and division; two numbers. A slash also appears in `{/if}` and `//` comments, with structural meanings. | 3 | `{var:coins / 2}` | TW2003 non-numbers; TW1001 missing operand. |
| `+` `-` | Integer addition and subtraction; two numbers. Space subtraction away from names. | 4 | `{var:coins - 1}` | TW2003 non-numbers; TW1001 missing operand; TW2004 if an unintended hyphenated variable is undeclared. |
| `==` `!=` | Equality/inequality of matching types (number, boolean, text or tier). | 5 | `{if item:rope == "Rope"}Found it.{/if}` | TW2003 mismatched types; TW1001 malformed expression. |
| `<` `<=` `>` `>=` | Ordered comparison of numbers or reputation tiers. | 5 | `{if faction:guild >= friendly}Welcome.{/if}` | TW2003 mismatched/unordered types; TW1001 malformed expression. |
| `not` | Negate a boolean condition. | 6 | `{if not visited(camp)}New here.{/if}` | TW2003 non-boolean; TW2002 unknown scene. |
| `and` | Both boolean conditions must hold. | 7 | `{if player:hp > 0 and player has rope}Climb.{/if}` | TW2003 non-booleans. |
| `or` | At least one boolean condition must hold. | 8 | `{if flag:ready or player:hp > 0}Go.{/if}` | TW2003 non-booleans; TW2004 undeclared flag. |
| `has` | Inventory predicate: character ID on the left, item ID on the right. Item counts live only in inventory, not variables. | 1 | `{if player has rope}Equipped.{/if}` | TW2001 unknown character/item. |
| `visited(...)` | Boolean predicate with one bare scene ID. | 1 | `{if visited(camp)}Back again.{/if}` | TW1001 malformed call; TW2002 unknown scene. |
| `@` in expressions | Named boolean predicate registered by a C# story project; no arguments. Distinct from line metadata. | 1 | `{if @CanPickLock}Try the lock.{/if}` | TW2005 unregistered predicate. |
| `=` `+=` `-=` | Assign, add or subtract a number in an effect targeting a stat, variable or faction standing. Text variables support `=` only and require a text expression. Not comparison. | Structural | `~ player:hp -= 3`; `~ var:weather = "stormy"` | TW1003 malformed effect; TW2003 incompatible types or numeric operations on text; name errors. |
| `;` | Separate effects inside an effect list, not prose sentences or arguments. | Structural | `* [Pay] { var:coins -= 1; set flag:paid } -> hall` | TW1003 malformed effect; TW1001 unexpected trailing text. |

Effects also use `set flag:id`, `clear flag:id`, `give character item [number-expression]` and
`take character item [number-expression]`. An omitted inventory quantity is one. These are word
commands, not additional sigils.

Variable assignment can introduce integer or text state without YAML declarations.
Discovery is story-wide; implicit text starts empty until an assignment executes.
The first declaration fixes the type and assignments copy values, not live links.
See [declaration ordering and inference](talemark-syntax.md#declaring-variables-in-talemark).

### Binding order

From tightest to loosest:

1. Parenthesized expressions, literals, references, `has`, `visited(...)`, named predicates.
2. Unary numeric `-`.
3. `*`, `/`.
4. `+`, binary `-`.
5. One comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`); comparisons do not chain.
6. `not`.
7. `and`.
8. `or`.

Arithmetic at the same level groups left to right: `8 - 3 - 1` is `(8 - 3) - 1`.
`not player:hp > 0` means `not (player:hp > 0)`. Use parentheses when the intended grouping
is not obvious. Expressions allow at most 256 nested groups and prefix operators combined
(TW1016 beyond the limit). In a substitution, the value expression is evaluated before the `#` chain.
`#upper#lower` applies upper case, then lower case.

### Modifier arguments

The general decided shape is `#name("first", "second")`: commas separate double-quoted arguments.
Argument values belong to the modifier; they are not globally reserved names.
Each modifier validates its own supported values and reports a clear diagnostic for invalid ones.
This shape is a contract for future modifiers, not permission to pass extra arguments to today's ones.

Pronoun forms are properties, not modifiers; for example,
`{player:pronoun:possessive-pronoun:independent}` gives *hers*. The agreement selector
`{player:pronoun#one-many("has", "have")}` requires exactly two quoted strings,
singular first and plural second. It returns the authored text selected by the
effective pronoun set's `agreement` at render time; singular they uses plural
agreement. On an item-name reference, `{item:pants#one-many("is", "are")}` selects
from item `name-agreement` instead. Neither case uses groups, population, counts
or `uncountable`. Character names and text variables are not selector targets.
Empty strings are allowed. Following text modifiers operate on the selected
text, for example `#one-many("has", "have")#capitalize`.
This is dedicated two-string selector support, not generalized multi-argument parsing.
Other built-in modifiers take no arguments:
`#capitalize`, `#upper`, `#lower`, `#a`, `#the`, `#this`, `#that`, and the generic text `#possessive`.
`#a` uses `some` for plural/uncountable names; `#this`/`#that` use `these`/`those`
for plural agreement. `#the` always prefixes `the`. Original item grammar stays
attached throughout a substitution chain; assigning the name to a variable copies
text only. See [item-name grammar](talemark-syntax.md#item-name-grammar) for defaults
and the complete table. There is no article inference, noun inflection or count phrasing.
Plain `pronoun` is the subject form, also accessible as `pronoun:subject`.
See the [pronoun forms table](talemark-syntax.md#3-substitutions).

Completion offers `subject`, `object`, `possessive-pronoun` and `reflexive` after
`character:pronoun:`, then `determiner` and `independent` after
`character:pronoun:possessive-pronoun:`. Both possessive properties are explicit; neither is a default.
Completion and hover offer `one-many` on pronoun and item-name references only, with its fixed
argument order. Its strings are author text, not finite argument suggestions.
Wrong targets, missing strings and extra arguments produce TW2006.

### Image syntax decision

The approved form is `{image("path", "alt text")}`, with optional alt text, in every
narrative-template context. The path stays literal. Alt text is rendered with the prose template
language where the image appears. It constructs media; it is not an expression function returning text.
The value is the media node, with no arithmetic precedence or `#` chaining. Its strings decode
`\"` and `\\` once. Alt text also treats `\{` and `\}` as literal braces. Quoted paths can contain
filename punctuation without becoming story identifiers.
Existing `{image:name}` character access is unchanged.

The alternatives considered for [#197](https://github.com/squid-farm/talewright/issues/197) were
`@image(path | "alt")`, `@image("path", "alt")`, a line-only `@image` directive, a colon form and
`#image`. Braces give narrative insertion an explicit boundary without a new distinction between
scene metadata and image content. Commas follow the argument convention instead of adding `|`.
The line-only form unnecessarily limits authoring; `:` already means access, and `#` returns text,
not media. Markdown images remain rejected, rather than supported as a second spelling.
Image placement is not restricted for presentation preferences; authors control their content.
Plain-string fields are a separate model boundary, not an image-specific prohibition.

## Prose formatting and legacy images

These are current formatting delimiters, not a promise of general Markdown support.
The formatting design is still being considered in
[#200](https://github.com/squid-farm/talewright/issues/200).

| Marker | Meaning, arguments and context | Binding and escaping | Canonical example | Diagnostics |
|---|---|---|---|---|
| `**...**`, `__...__` | Bold prose, choice labels or dialogue. | Inline formatting after brace parsing; backslash can escape formatting punctuation. | `**Danger**` | Unmatched formatting markers generally remain text, not a Talemark error. |
| `*...*`, `_..._` | Italic prose, choice labels or dialogue. A leading `* ` is a choice instead. | Same formatting stage and escaping. | `*Quietly*` | Same unmatched-marker behavior. |
| Backticks | Inline code text. Brace substitutions are still recognized before code formatting. | Backtick-delimited span after brace parsing. | `` `torch` `` | Not a block-code or brace-escape syntax. |
| `![alt](path)` | Rejected legacy Markdown image form. | Detected after brace parsing, with the diagnostic mapped back to the author's source. | Replace `![A torch](torch)` with `{image("torch", "A torch")}`. | TW1015 names the quoted replacement. |
| `[text](target)` | Legacy link parsing keeps the label text but discards the destination; not a working hyperlink feature. | Inline parsing after brace parsing. | `[Go](https://example.org)` | No target validation or hyperlink guarantee. |

## Dotted-prefix completion

In a recognized reference position, completion filters on the whole typed name, including dots.
With items `weapon.knife`, `weapon.sword` and `rope`, `{item:weapon.` offers only the two
`weapon.*` items. The dot is part of the replacement range, not a traversal into another object.
The same prefix filtering applies to scene targets, character references, stats, factions, flags
and variables in their recognized positions.
It does not imply completion in prose, a new declaration, or inside an arbitrary quoted string.
The language server uses the shared authoring completion provider.

## Implementation status

| Rule or feature | Current implementation and next owner |
|---|---|
| `:` access, `.` within names, shared story-name characters | Implemented in [#191](https://github.com/squid-farm/talewright/issues/191). |
| Pronoun forms as text-valued properties | Implemented in [#332](https://github.com/squid-farm/talewright/issues/332), replacing the earlier #190 syntax without compatibility machinery. |
| `#one-many("singular", "plural")` | Pronoun selection implemented in [#192](https://github.com/squid-farm/talewright/issues/192); item-name selection in [#308](https://github.com/squid-farm/talewright/issues/308). Uses effective pronoun `agreement` or item `name-agreement`, never quantity. |
| Item `name-agreement` / `uncountable`, `#a` / `#the` / `#this` / `#that` | Implemented in [#308](https://github.com/squid-farm/talewright/issues/308); explicit helpers retain original-reference grammar through chains, with no object-copy semantics. |
| Double quotes around reader text inside syntax | Decided in [#189](https://github.com/squid-farm/talewright/issues/189); not applied to current titles, dialogue or choice labels. Implementation work belongs under [#184](https://github.com/squid-farm/talewright/issues/184); this docs-only PR does not invent replacements for those forms. |
| `\"` and `\\` inside quotes | Implemented for image paths and alt in [#197](https://github.com/squid-farm/talewright/issues/197), and expression/modifier strings in [#352](https://github.com/squid-farm/talewright/issues/352). Enclosing delimiters respect escaped quotes. Prose `\{` and `\}` also work. |
| Comma-separated multi-argument modifiers | Only the pronoun/item-name `#one-many` selector accepts exactly two strings. General multi-argument support is not implemented. Future modifier work belongs under [#184](https://github.com/squid-farm/talewright/issues/184), with extensibility in [#193](https://github.com/squid-farm/talewright/issues/193). |
| New image syntax | `{image("path", "alt")}` works across narrative-template contexts. Alt is optional. It uses the same template language as prose ([#330](https://github.com/squid-farm/talewright/issues/330)), rendered with the state where the image appears, and cannot contain a nested image. Quoted paths stay literal asset strings, not story identifiers. Shared lookup checks paths in compilation, validation and editor analysis; editor completion refreshes with file changes. The shared renderer displays images in folder stories, ZIP uploads and published web/desktop games. See [images](talemark-syntax.md#images) and [image diagnostics](diagnostics.md#tw2xxx-names-and-types) for paths, formats, failure behavior and limits. |
| Formatting beyond bold, italic and code | Design decision in [#200](https://github.com/squid-farm/talewright/issues/200), not an accepted `#heading` or other new modifier here. |
| Checkpoints/autosaves | Syntax is still to be chosen in [#54](https://github.com/squid-farm/talewright/issues/54) using the checklist. `@once` means storylet eligibility, not a checkpoint policy. No checkpoint marker is reserved by this document. |

Unimplemented contracts need scoped implementation issues agreed with the project manager before coding.
This registry records the gaps; it does not silently expand #197 or change the tracker.
