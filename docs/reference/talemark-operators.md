# Talemark operator registry

Talemark is the language inside `.tale` files. This is the authoritative list of its markers and
operators, after the property and pronoun changes in
[#191](https://github.com/squid-farm/talewright/issues/191) and
[#190](https://github.com/squid-farm/talewright/issues/190).
See the [syntax reference](tale-syntax.md) for complete stories and the
[language-change checklist](../developer/language-changes.md) before proposing new syntax.

**Status matters.** Unless marked **decided, not implemented**, examples below describe the current
compiler. This documentation does not add syntax. The [implementation status](#implementation-status)
section separates decisions from features you can use today.

## Names and values

Story identifiers start with an ASCII letter (`A`-`Z`, `a`-`z`) or `_`. The remaining characters are
ASCII letters, digits or `_`, with optional single `.` or `-` separators between groups.
Separators cannot be adjacent or end a name. Names are case-sensitive.

- Valid: `weapon.knife`, `camp.arrival`, `_guide`, `north-gate`, `act.2`.
- Invalid: `2doors`, `camp..arrival`, `north.-gate`, `door-`, `café`.
- `weapon.knife` is one name, not a read of `knife` on `weapon`.
- Use `:` for access: `faction:explorers-guild:name`, `player:hp`.
- Use spaces around subtraction: `var:coins - 1`. `var:coins-1` reads the name `coins-1`.
  A minus at the start of a numeric operand means negation: `-3`.

The same story-name character set applies in the compiler, manifest schema and editor grammar.
Expression words such as `true`, `false`, `and`, `or`, `not`, `has` and `visited` have special meanings
in their expression positions; a valid spelling is not a guarantee that a name is available there.
C#-registered predicate and modifier names also depend on their registration contracts.
The five legacy names `pronoun.subject`, `pronoun.object`, `pronoun.possessive`,
`pronoun.possessive-pronoun` and `pronoun.reflexive` are rejected as character reads even if a stat
has that name. The error names the replacement.

### Reader text, quotes and escapes

**Decided, not fully implemented:** reader-facing text inside syntax uses double quotes.
Names remain bare. `"Joel's cat"` is text; `weapon.knife` is a name. Single quotes are not a second
Talemark string delimiter: apostrophes are ordinary text.

Backslash is the escape character everywhere. Inside double quotes, `\"` means a literal double
quote and `\\` means a literal backslash. In prose, `\{` and `\}` mean literal braces.
For example, the decided string `"She said \"Go\"."` contains `She said "Go".`.
These rules also apply to future reader-text arguments such as image alt text.
They do not introduce an image spelling, quoted choice-label spelling or a new formatting feature.

**Today:** expression string literals and the possessive modifier argument use double quotes, but
their scanners do not decode `\"` or `\\`. Unclosed strings can produce TW1001, TW2006, or TW1004
when the enclosing brace cannot be found. Prose already supports `\{` and `\}`. Existing scene
titles, dialogue and choice labels still use their current unquoted forms:
`@title The door`, `> guide: Hello.`, `* [Open the door] -> hall`.
Do not add quotes to those forms expecting them to be removed.

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
| `->` | Divert in a scene, choice or check outcome; takes one scene ID or `END`. Put a scene divert inside a block condition to make it conditional. | `-> camp.arrival` | TW1010 missing target; TW1008 invalid ID; TW2002 unknown target. |
| `*` with `[label]` | Choice at the start of a line (`* ` or `*[`). Takes a prose label, optional `(requires: condition)`, `{ effects }`, target or skill check. This is not multiplication in this context. | `* [Enter] (requires: player:hp > 0) -> hall` | TW1011 malformed choice; TW3003 no usable target. |
| `[` `]` | Bound a choice label; its contents are reader text with substitutions and formatting. The current label scanner skips backslash-escaped closing brackets. | `* [Hello {player:first-name}] -> hall` | TW1011 missing closing bracket. |
| `>` | Dialogue at the start of a line; takes a character ID, `:`, then prose. Not a block quote. | `> guide: Follow me.` | TW1007 malformed speech; TW2001 unknown character. |
| `~` | Entry effect at the start of a line; followed by effects. Runs before rendering. | `~ var:coins += 1` | TW1003 malformed effect; name/type errors. |
| `{` `}` | In prose, delimit a substitution or `if`/`else`/`/if`; in choices and outcomes, delimit effects. Block conditions occupy their own lines. Escape literal prose braces as `\{` and `\}`. | `{if player:hp > 0}Ready{else}Rest{/if}` | TW1004 unclosed brace; TW1005 unmatched condition; TW1011 unclosed effect block. |
| `:::` | Delimit an entry skill-check block. Opening line takes `skill-check [actor] skill dc number-expression`; closing line has only `:::`. Both outcomes are required. | `::: skill-check perception dc 12` (close with `:::`) | TW1012 unknown/unclosed block; TW1009 missing outcomes; TW3006 illegal placement or duplicate check. |
| `:` in labels | Separates fixed control labels from their contents: `requires:`, `success:`, `failure:`. Not a property read in this context. | `success: -> hall` | TW1009 misplaced/missing outcomes; TW1011 malformed choice condition. |
| `//` | Comment when it begins a trimmed line; the rest of the line is ignored. It is not a trailing-comment operator. | `// The guard remembers this.` | None for comment contents. |

`END` is the reserved divert destination that ends the story, not a scene declaration.
`skill-check` and `dc` are words, not new sigils. On a choice, write
`* [Climb] skill-check athletics dc 14` and follow it with `success:` and `failure:` lines.
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
| `#` | Text manipulator in a substitution. Takes the value to its left and returns text; repeated manipulators run left to right. A pronoun form must come first on a pronoun reference. | After the value expression | `{player:pronoun#reflexive#capitalize}` | TW2006 unknown/misplaced modifier, bad argument syntax or value. |
| `(` `)` | Group an expression; also bound `visited`'s scene argument, modifier arguments, choice conditions, and hub tag groups. | 1 for grouping; otherwise structural | `{(var:coins + 1) * 2}` | TW1001 expression mismatch; TW2006 modifier; TW1011 choice; TW1006 hub. |
| `"` | Bound text literals and modifier string arguments. No single-quoted Talemark string form. Escapes are decided as above, not yet decoded. | 1 | `{player:pronoun#possessive("independent")}` | TW1001 malformed expression; TW2006 malformed argument; TW1004 enclosing brace not found. |
| `\` | Escape character, with the current/decided distinction described above. | During text scanning | `Literal \{braces\}` | Unescaped `{` can cause TW1004; not a numeric or boolean operator. |
| `,` | Separate metadata tags and hub arguments today; decided separator for multiple double-quoted modifier arguments. Each modifier defines its own values. | Structural | `@hub any(temple, forest)` | TW1006 invalid hub; multi-argument modifiers are not implemented (TW2006). |
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
is not obvious. In a substitution, the value expression is evaluated before the `#` chain.
`#upper#lower` applies upper case, then lower case.

### Modifier arguments

The general decided shape is `#name("first", "second")`: commas separate double-quoted arguments.
Argument values belong to the modifier; they are not globally reserved names.
Each modifier validates its own supported values and reports a clear diagnostic for invalid ones.
This shape is a contract for future modifiers, not permission to pass extra arguments to today's ones.

Today, only pronoun `#possessive` accepts an argument: `"determiner"` (default, as in *her book*) or
`"independent"` (as in *hers*). For example, `{player:pronoun#possessive("independent")}`.
The compiler accepts one string, not a comma-separated list. Other built-in modifiers take no arguments:
`#capitalize`, `#upper`, `#lower`, `#a`, and `#possessive` on a name.
`#object` and `#reflexive` select a pronoun form and must immediately follow `pronoun`.
Plain `pronoun` is the subject form; `#subject` is an error.
See the [pronoun forms table](tale-syntax.md#3-substitutions).

Completion offers finite values after `#possessive(` and identifies `"determiner"` as the default.
`"hers"` is not an alias for `"independent"`: it produces TW2006 pointing at the argument.

## Prose formatting and legacy images

These are current formatting delimiters, not a promise of general Markdown support.
The formatting design is still being considered in
[#200](https://github.com/squid-farm/talewright/issues/200).

| Marker | Meaning, arguments and context | Binding and escaping | Canonical example | Diagnostics |
|---|---|---|---|---|
| `**...**`, `__...__` | Bold prose, choice labels or dialogue. | Inline formatting after brace parsing; backslash can escape formatting punctuation. | `**Danger**` | Unmatched formatting markers generally remain text, not a Talemark error. |
| `*...*`, `_..._` | Italic prose, choice labels or dialogue. A leading `* ` is a choice instead. | Same formatting stage and escaping. | `*Quietly*` | Same unmatched-marker behavior. |
| Backticks | Inline code text. Brace substitutions are still recognized before code formatting. | Backtick-delimited span after brace parsing. | `` `torch` `` | Not a block-code or brace-escape syntax. |
| `![alt](path)` | Legacy image placeholder in prose; takes a label and path, but is not displayed. | Inline image parsing after brace parsing. | `![A torch](torch)` | No image-file validation; replacement diagnostics are part of #197, not this registry. |
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
| `#` pronoun forms, possessive values and default completion detail | Implemented in [#190](https://github.com/squid-farm/talewright/issues/190). |
| Double quotes around reader text inside syntax | Decided in [#189](https://github.com/squid-farm/talewright/issues/189); not applied to current titles, dialogue or choice labels. Implementation work belongs under [#184](https://github.com/squid-farm/talewright/issues/184); this docs-only PR does not invent replacements for those forms. |
| `\"` and `\\` inside quotes; one consistent escape scanner | Decided, not implemented. Existing expression, modifier and enclosing-brace scanners must change together under [#184](https://github.com/squid-farm/talewright/issues/184); [#197](https://github.com/squid-farm/talewright/issues/197) must apply the rule to image alt text. Prose `\{` and `\}` already work. |
| Comma-separated multi-argument modifiers | Decided contract; today's modifier parser accepts at most one argument. Future modifier work belongs under [#184](https://github.com/squid-farm/talewright/issues/184), with extensibility in [#193](https://github.com/squid-farm/talewright/issues/193). |
| New image syntax | [#197](https://github.com/squid-farm/talewright/issues/197) must choose a spelling using this registry and double-quoted alt text. It does not include image-file existence checks. Resolver integration remains a separate follow-up; see [image diagnostics](diagnostics.md#tw2xxx-names-and-types). |
| Formatting beyond bold, italic and code | Design decision in [#200](https://github.com/squid-farm/talewright/issues/200), not an accepted `#heading` or other new modifier here. |
| Checkpoints/autosaves | Syntax is still to be chosen in [#54](https://github.com/squid-farm/talewright/issues/54) using the checklist. `@once` means storylet eligibility, not a checkpoint policy. No checkpoint marker is reserved by this document. |

Unimplemented contracts need scoped implementation issues agreed with the project manager before coding.
This registry records the gaps; it does not silently expand #197 or change the tracker.
