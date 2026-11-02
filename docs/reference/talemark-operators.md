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
same reader-text grammar as prose, including `\{`, `\}`, `\*`, `\_`, `\[` and `\]` for literal
delimiters. Both arguments decode `\"` and `\\`; alt text also accepts backtick escapes. Unknown
image escapes produce TW1014. A nested `{image(...)}` in alt text also produces TW1014. See the
[reader-text grammar](#reader-text-grammar).

The manifest is YAML, not Talemark: YAML quoting and escaping still apply there. A Talemark
expression stored in YAML must first survive YAML decoding.

## Construct families and argument slots

Every Talemark construct belongs to one family. The family decides where the construct can appear,
what it produces, and how it reads its arguments. New syntax must fit an existing family, or the
[language-change checklist](../developer/language-changes.md) proposal must explain why a new family
is needed.

| Family | Recognized by | Where | Produces | Examples |
|---|---|---|---|---|
| Line directive | The first characters of a trimmed line. One directive per line. | Scene bodies; metadata directly after `===`. | Story structure: a scene, metadata, choice, speech, effect batch, check block, divert or comment. | `=== camp`, `@title`, `* [Go] -> hall`, `> guide: Hi.`, `~ set flag:x`, `::: skill-check`, `-> hall`, `// note` |
| Reader text | The default content of a text unit. | Paragraphs, speech text, choice labels, image alt text. | Formatted text shown to the reader. | `**Danger** ahead` |
| Inline construct | `{` and its matching `}` inside reader text. | Any reader text. | Substituted text, a conditional branch, or media. | `{player:first-name}`, `{if flag:x}`, `{image("map")}` |
| Expression | An expression slot. | Substitutions, `{if}`, `@requires`, `(requires: ...)`, assignment values, quantities and check DCs. | A typed value: number, boolean, text or tier. Never changes state. | `player:hp > 0`, `visited(camp)` |
| Text manipulator | `#name` after a substitution's value. | The end of a substitution only. | Text. Applied left to right. Never changes state. | `#capitalize`, `#one-many("is", "are")` |
| Effect | A statement in a `~` line or a choice `{ ... }` block, separated by `;`. | Effect lines and choice effects. | A state change, with no value. | `set flag:x`, `give player rope 2`, `var:coins += 1` |
| Clause | A fixed word, or a word followed by `:`, attached to a directive. | Choices and checks. | Modifies its directive. | `(requires: ...)`, `success:`, `skill-check perception dc 12` |

### Argument slots

Every position that holds author input has exactly one slot kind. The slot kind, never the content,
decides how that input is read. Quotes only delimit an argument; they do not decide whether `*` or
`{` has meaning inside it. **If a slot accepts substitutions, it is reader text.**

| Slot kind | How it is read | Delimited by | Escapes | Current slots |
|---|---|---|---|---|
| Name | One bare identifier using the [shared name rules](#names-and-values). | The construct's punctuation or whitespace. | None. | Scene IDs, `visited(...)`, effect targets, item and character IDs. |
| Tag | Nonempty text without commas, trimmed. Not a story name. | Commas, or the end of the line. | None. | `@tags` values and `@hub` tags. |
| Integer literal | A whole number; each slot sets its own range. | End of line. | None. | `@priority` (any integer) and `@hub-limit` (positive). |
| Expression | The [expression grammar](#value-and-expression-operators) and binding order. | The construct: braces, parentheses or end of line. | Only inside its string literals. | `{if ...}`, substitutions, `@requires`, `(requires: ...)`, assignment values. |
| Literal text | Exact text after decoding quote escapes. No substitutions or formatting. | Double quotes. | `\"` and `\\`; any other escape is an error. | Image paths, expression string literals, modifier arguments. |
| Reader text | The [reader-text grammar](#reader-text-grammar): formatting and inline constructs. | A text-unit boundary, or double quotes when inside other syntax. | Reader-text escapes, plus `\"` when quoted. | Prose, speech text, choice labels, image alt text. |
| Plain line text | The rest of the line, trimmed. No substitutions or formatting. | End of line. | None. | `@title`. |

Decided in [#189](https://github.com/squid-farm/talewright/issues/189): reader text inside syntax
uses double quotes. Speech and choice labels have not moved to quotes yet; that work belongs to
[#184](https://github.com/squid-farm/talewright/issues/184).

### Callable construct contract

A *callable* is any named construct that takes arguments or works like a method: predicates,
text manipulators, the image constructor, `@hub` groups, effect commands and check clauses.
Each callable documents the following in this registry before it ships:

1. **Family and result**: which family it belongs to, and the value, text, media or state change
   it produces.
2. **Contexts**: where it is valid. Anything else is a diagnostic, never silent text.
3. **Name**: spelling and case sensitivity. Names follow the shared identifier rules unless a
   registration contract says otherwise.
4. **Arguments**: an ordered list of slots. For each, give its slot kind, whether it is required,
   its default, which values it accepts and who validates them. Accepted values belong to the
   callable, not to a global reserved-word list.
5. **Timing and mutation**: when it is evaluated. Expressions, predicates and manipulators never
   change state; only effects do.
6. **Composition**: chaining, nesting and precedence, or a note that it is structural.
7. **Diagnostics**: codes and source ranges for malformed, incomplete and obsolete forms,
   including partial input while typing.
8. **Editor surface**: completion, hover and highlighting for its name and finite arguments.

**Shape guideline for new callables.** Expression-family callables (predicates, manipulators and
constructors) use `name(arg, arg)`. Arguments are separated by commas. Text arguments are
double-quoted, and names are bare. Whitespace is allowed around `(`, `,` and `)`. A callable with
no arguments omits the parentheses, as in `#capitalize` or `@CanPickLock`. Effects are statements,
not calls: they keep the word-command shape `verb target [value]`. Clauses keep their label words.
New callables follow this guideline. Existing exceptions are listed below rather than changed silently.

| Callable | Family and result | Arguments (slot kind) | Contexts | Notes |
|---|---|---|---|---|
| `visited(scene)` | Expression predicate; boolean. | One scene name. | Expressions. | Whitespace before `(` is accepted. |
| `character has item` | Expression predicate; boolean. | Character name, item name. | Expressions. | Infix word, not a call. |
| `@Name` | Expression predicate; boolean. | None. | Expressions. | Registered by a C# story project. |
| `#capitalize`, `#upper`, `#lower`, `#a`, `#the`, `#this`, `#that`, `#possessive` | Text manipulator; text. | None. | End of a substitution. | Chain left to right. |
| `#one-many("singular", "plural")` | Text manipulator; text. | Two literal-text arguments, both required. | Pronoun and item-name references. | Only multi-argument modifier. |
| `image("path", "alt")` | Inline construct; media. | Literal-text path, required; reader-text alt, optional. | The whole content of a `{ }` in reader text. | Not an expression; no `#` chaining. |
| `all(tag, ...)`, `any(tag, ...)` | `@hub` argument; tag group. | One or more distinct tags. | `@hub` only. | `(` must follow immediately today. |
| `set flag:id`, `clear flag:id` | Effect. | One flag reference. | Effects. | Word command. |
| `give`/`take character item [n]` | Effect. | Character name, item name, optional number expression (default 1). | Effects. | Space-separated arguments. |
| `skill-check [actor] skill dc n` | Clause. | Optional character name, skill name, number expression. | Choices and `:::` blocks. | Keyword sequence. |

Known inconsistencies, recorded for follow-up rather than changed here:

- Boolean queries have three spellings: a call (`visited(camp)`), an infix word (`player has rope`)
  and a sigil (`@CanPickLock`). Any unification belongs to [#184](https://github.com/squid-farm/talewright/issues/184).
- `visited`, modifiers and `image` accept whitespace before `(`; `@hub all(`/`any(` do not.
  Aligning them belongs to #184.
- Tags are not story names. `@tags` accepts spaces inside a tag, such as `@tags north gate`
  giving one tag. `@hub` rejects tags containing whitespace, parentheses or commas. Aligning tag
  rules belongs to #184.
- Modifier names are scanned up to `#` or `(` instead of with the shared name rules. Registration
  and extensibility belong to [#193](https://github.com/squid-farm/talewright/issues/193).
- General multi-argument modifiers are not implemented; only `#one-many` takes arguments
  (#184, #193).
- Image alt text is read as reader text only when it contains `{`. The approved rule makes it always
  reader text in [#401](https://github.com/squid-farm/talewright/issues/401).

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
| `*` | Choice at the start of a line (`* ` or `*[`). A prose `[label]` is optional with an explicit `->` target; omitted labels display **Continue**. At most one `(requires: condition)` and one `{ effects }` work on either form, in either order before the terminal divert or skill check. The opening `(requires:` is exact. A choice skill check needs a label. This is not multiplication in this context. | `* -> hall` | TW1011 malformed, duplicate or misplaced choice clause; TW3003 no usable target. |
| `[` `]` | Bound a choice label; its contents are reader text with substitutions and formatting. Balanced inner brackets are literal text; the scanner skips backslash-escaped brackets and inline constructs. | `* [Open [the] door] -> hall` | TW1011 missing closing bracket. |
| `>` | Dialogue at the start of a line; takes a character ID, `:`, then prose. Not a block quote. | `> guide: Follow me.` | TW1007 malformed speech; TW2001 unknown character. |
| `~` | Effect batch at the start of a line; executes in source order. Triggers settle after the whole semicolon-separated batch. | `~ var:coins += 1` | TW1003 malformed effect; TW3011 after choices; name/type errors. |
| `{` `}` | In prose, delimit a substitution or `if`/`else`/`/if`; in choices and outcomes, delimit effects. Block conditions occupy their own lines. Escape literal prose braces as `\{` and `\}`. | `{if player:hp > 0}Ready{else}Rest{/if}` | TW1004 unclosed brace; TW1005 unmatched condition; TW1011 unclosed choice effect; TW1009 unclosed outcome effect. |
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
language where the image appears; under the approved [reader-text grammar](#reader-text-grammar)
it is always reader text. It constructs media; it is not an expression function returning text.
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

These formatting delimiters use Talemark's
[reader-text grammar](#reader-text-grammar) below, not a general Markdown parser.
Broader typography and presentation remain in
[#200](https://github.com/squid-farm/talewright/issues/200).

| Marker | Meaning, arguments and context | Binding and escaping | Canonical example | Diagnostics |
|---|---|---|---|---|
| `**...**`, `__...__` | Bold prose, choice labels or dialogue. | Inline formatting after brace parsing; backslash can escape formatting punctuation. | `**Danger**` | Unmatched formatting markers generally remain text, not a Talemark error. |
| `*...*`, `_..._` | Italic prose, choice labels or dialogue. A leading `* ` is a choice instead. | Same formatting stage and escaping. | `*Quietly*` | Same unmatched-marker behavior. |
| Backticks | Inline code text. Brace substitutions are still recognized before code formatting. | Backtick-delimited span after brace parsing. | `` `torch` `` | Not a block-code or brace-escape syntax. |
| `![alt](path)` | Rejected legacy Markdown image form. | Detected after brace parsing, with the diagnostic mapped back to the author's source. | Replace `![A torch](torch)` with `{image("torch", "A torch")}`. | TW1015 names the quoted replacement. |
| `[text](target)` | Literal reader text; it is not a hyperlink feature. | No link parsing or target validation. | `[Go](https://example.org)` | None. |

## Reader-text grammar

**Implemented.** [ADR 0006](../developer/adr/0006-talemark-reader-text.md) records
the decision. [#400](https://github.com/squid-farm/talewright/issues/400) adds the bounded scanner and
[#401](https://github.com/squid-farm/talewright/issues/401) replaces Markdig with these rules.
Until #401 ships, the current behavior above and in the corpus's *Current* column applies.
CommonMark is not the specification. Anything not listed here is literal text.

### Text units

Reader text is parsed one *text unit* at a time:

- **Paragraph**: consecutive prose lines, ended by a blank line or a line directive. A `//`
  comment line is skipped without ending the paragraph. Each line is trimmed, and lines are joined
  by a single space.
- **Speech text**: the rest of a `>` line after the speaker's `:`.
- **Choice label**: the contents of the label brackets.
- **Image alt text**: the second quoted argument of `image(...)`.

Formatting, code spans and inline conditions never cross a text unit's boundary. An inline
construct may span lines only inside a paragraph. Block directives, including block `{if}` lines,
are single lines.

### Order of recognition

1. Line directives decide the text units.
2. Within a unit, a single left-to-right scan recognizes escapes, then inline constructs, then
   code spans, then emphasis and strong delimiters. Inline constructs are atoms: a `*`, `_` or
   backtick inside `{ }` is never formatting. Code spans hide formatting but not inline constructs
   or escapes.
3. Inline `{if}`, `{else}` and `{/if}` pair only with markers in the same formatting span or code
   span. Otherwise they report TW1005. Inline conditions nest at most 64 deep (TW1016).

### Escapes

A backslash followed by one of `\`, `{`, `}`, `[`, `]`, `*`, `_` or a backtick produces that
character literally. Escapes work the same way inside code spans. In a quoted reader-text slot
such as alt text, `\"` is also an escape. Escapes are decoded once: finding the closing quote does
not decode anything.

In unquoted reader text, any other backslash is literal, so `C:\stories` and `\.` display as
written. A quoted slot is strict: an unknown escape is that construct's error, such as TW1014 for
alt text. A trailing `\` on a paragraph line followed by another line of the same paragraph is a
hard line break. On a paragraph's last line, or in speech text and labels, it is a literal
backslash. Trailing spaces never create a break. Line directives have no escape; that proposal
is [#404](https://github.com/squid-farm/talewright/issues/404).

### Emphasis and strong

- `*` and `_` are the only delimiter characters. A *run* is a maximal sequence of the same
  character that is not escaped and not inside an inline construct or code span.
- A run of one marks emphasis and a run of two marks strong. A run of three or more is literal, so
  write `**_both_**` instead of `***both***`.
- A word character is a Unicode letter or digit. Treat the start and end of a text unit as
  boundary characters that are both non-word and non-whitespace. A run **can open** when its next
  character is not a boundary and not whitespace, and its previous character is not a word
  character. A run **can close** when its previous character is not a boundary and not whitespace,
  and its next character is not a word character.
- Scan left to right with a stack of open runs:
  1. If a run can close and matches the innermost open run in both character and length, it closes
     that run.
  2. Otherwise, if it can close and matches any farther-out open run, it is literal; it neither
     closes that outer run nor opens a new span.
  3. Otherwise, if it can open, push it. This includes a run that can close but matches no open
     run; if it remains unmatched at the end of the unit, it is literal.
  4. Otherwise, it is literal. Any open runs left at the end of the unit are also literal.
- So `2*3*4`, `snake_case` and `word*inner*word` are literal. Nested spans work. Crossing spans
  are literal at the crossing run and never close an outer span or reinterpret that run as an
  opener.
- Spans nest at most 64 deep. A deeper opening run reports TW1016 and is literal.

### Code spans

A run of *n* backticks opens a code span, which closes at the next run of exactly *n* backticks.
An unmatched run is literal. Inside, emphasis and strong do not apply. Inline constructs and
escapes still do. If the content both begins and ends with a space and is not all spaces, one
space is removed from each end. A line join inside a paragraph is a space.

### Quotes, parentheses and brackets

In reader text, `"`, `'`, `(`, `)`, `[` and `]` are typography, not strings or groups. Only a
choice label's own brackets have structure. The label scanner counts nested unescaped `[` and `]`,
skips escaped characters, and skips inline constructs, including their quoted strings. The label
ends at the bracket that balances the opening one, so `[Open [the] door]` displays
`Open [the] door`. An unbalanced inner bracket needs `\[` or `\]`; otherwise TW1011 reports the
missing `]` at the label's opening bracket. A suggested insertion point is given only for one
clear later clause boundary; quotes in the label remain typography.

### Inline constructs and recovery

`{` opens an inline construct, which ends at its matching `}`. Nested braces and quoted strings
inside it are skipped. Within a paragraph the construct may span lines, and newlines count as
whitespace. That preserves current multiline conditions and arguments. Talemark adds no
continuation character and no single-line rule; the broader multiline policy is deliberately
left open.

- An unclosed `{` reports one TW1004 at the brace. The rest of the text unit is literal text, and
  editor analysis still analyzes the partial expression.
- A stray `}` is literal text without a diagnostic.
- An error inside a closed construct reports at the author's source position, and scanning
  continues after the `}`.
- Every diagnostic maps to the original source line and column, including multiline paragraphs
  and quoted alt text.

### Unsupported Markdown

Headings, setext underlines, thematic breaks, lists, block quotes inside prose, reference
definitions, links, autolinks, HTML, entities and numeric character references are literal text.
None of them consumes or hides author text. The one exception is the simple legacy image form.
TW1015 matches only this exact, single-line shape, with no whitespace between its delimiters:

1. `![`
2. Alt text, possibly empty, containing no backslash, `[`, `]`, `{`, `}`, CR or LF. Other
   characters, including quotes and whitespace, are allowed.
3. `](`
4. A nonempty path containing no whitespace, `(`, `)`, backslash, double quote, `{`, `}` or
   line break.
5. `)`

The scanner looks for `![` from left to right. If the following text does not match the shape,
that `!` is literal and scanning resumes at the next character; a later independent `![` can
still match.
The matcher does not balance or unescape brackets or parentheses. A nested bracket in alt text,
an escape such as `![a\]](p)`, a nested parenthesis such as `![a](p(q))`, or any line break makes
the whole candidate literal. It does not decode Markdown escapes or entities. The captures contain
no backslashes by definition. In the replacement, the alt is otherwise preserved, with each `"`
escaped as `\"`; the path is inserted verbatim. Thus `![A "torch"](torch)` suggests
`{image("torch", "A \"torch\"")}`. TW1015 points at the opening `!`.

### Image alt text

Alt text always uses this grammar, whether or not it contains `{`. The reader sees plain text:
formatting is dropped, code-span text is kept, and a hard break becomes a space. Its escapes are
`\"` plus the reader-text escapes; other escapes report TW1014, and a nested `{image(...)}` reports
TW1014. Missing-alt warnings are decided from the authored alt, as today.

### Example corpus

This 38-example corpus is the implementation contract for #401. *Current* is the Markdig behavior
measured before #401; a ⏎ marks a line break in the source. `[I: ...]` is emphasis, `[B: ...]` is
strong and `[C: ...]` is code.

| # | Input | Current | Approved |
|---|---|---|---|
| 1 | `Lost line` ⏎ `---` | The paragraph disappears (setext heading). | `Lost line ---` |
| 2 | `Lost line` ⏎ `-` | The paragraph disappears. | `Lost line -` |
| 3 | `[Inventory]: empty` | Disappears (reference definition). | Literal. |
| 4 | `[the inn][1] is here` | `the inn][1] is here` | Literal. |
| 5 | `[Go](https://example.org) now` | `Go now` | Literal. |
| 6 | `&amp; &copy; &#65;` | `& © A` | Literal. |
| 7 | `<b>tag</b>`, `https://example.org` | Literal. | Literal. |
| 8 | `# Not heading`, `- not list`, `1. not list` | Literal. | Literal. |
| 9 | `2*3*4 and snake_case_name` | `2[I: 3]4 and snake_case_name` | Literal. |
| 10 | `word*inner*word` | `word[I: inner]word` | Literal. |
| 11 | `***both***` | Bold italic `both`. | Literal; write `**_both_**`. |
| 12 | `**bold *italic* bold**` | `[B: bold [I: italic] bold]` | Same. |
| 13 | `*a **b* c**` | `[I: a b c]` | `*a [B: b* c]` |
| 14 | `**unclosed and *also` | Literal. | Literal. |
| 15 | `_under_ and __strong__` | `[I: under] and [B: strong]` | Same. |
| 16 | ``` `code {player:first-name} **x**` ``` | `[C: code Ada **x**]` | Same. |
| 17 | ``` `a \{b\} c` ``` | `[C: a \{b\} c]` | `[C: a {b} c]` |
| 18 | ```` `` a`b `` ```` | ``` [C: a`b] ``` | Same. |
| 19 | `\* \_ \[ \] \{ \} \\` | `* _ [ ] { } \` | Same. |
| 20 | `\. \# \> \& \a` | `. # > & \a` | Literal, backslashes kept. |
| 21 | `line one\` ⏎ `line two` | Hard break. | Same. |
| 22 | `She said "hi" (quietly) [aside]` | Literal. | Same. |
| 23 | `* [Open [the] door] -> END` | Label `Open [the] door`. | Same. |
| 24 | `* [Open [door] -> END` | TW1011. | Same. |
| 25 | `* [Say "hi" (quietly)] -> END` | Label `Say "hi" (quietly)`. | Same. |
| 26 | `* [Open \] door] -> END` | Label `Open ] door`. | Same. |
| 27 | `Hello {player:first-name` | TW1004. | Same; the rest is literal. |
| 28 | `Close } stray` | Literal. | Same. |
| 29 | `{if true}**a{else}b**{/if}` | TW1005. | Same. |
| 30 | `{if flag:a and` ⏎ `flag:b}Both.{/if}` | Works. | Same. |
| 31 | `{image("portrait", "**The guide**")}` | Alt `**The guide**`. | Alt `The guide`. |
| 32 | `{image("portrait", "**{guide:first-name}**")}` | Alt `Bea`. | Same. |
| 33 | ``` {image("portrait", "`code` and \\ slash")} ``` | Alt ``` `code` and \ slash ```. | Alt `code and \ slash`. |
| 34 | `{image("portrait", "a \* b")}` | TW1014. | Alt `a * b`. |
| 35 | `![A torch](torch)` | TW1015. | Same. |
| 36 | `![a [b]](p)` | TW1015. | Literal. |
| 37 | `![a\]](p)` | TW1015. | Literal. |
| 38 | `![a](p(q))` | TW1015. | Literal. |

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
| [Reader-text grammar](#reader-text-grammar) | Approved in [#399](https://github.com/squid-farm/talewright/issues/399) and [ADR 0006](../developer/adr/0006-talemark-reader-text.md). Bounded scanner in [#400](https://github.com/squid-farm/talewright/issues/400); Markdig replaced, with corpus and silent-loss regression tests, in [#401](https://github.com/squid-farm/talewright/issues/401). Package removal remains in [#402](https://github.com/squid-farm/talewright/issues/402); remaining delimiter callers are tracked by [#403](https://github.com/squid-farm/talewright/issues/403). |
| Alt text always reader text | Approved in [#399](https://github.com/squid-farm/talewright/issues/399); implemented in [#401](https://github.com/squid-farm/talewright/issues/401). |
| Choice clauses | Both orders remain accepted; [#384](https://github.com/squid-farm/talewright/issues/384) diagnoses duplicate, incomplete and misplaced clauses. |
| Line-directive escape | Not approved. Optional and decision-gated in [#404](https://github.com/squid-farm/talewright/issues/404). |
| [Callable construct contract](#callable-construct-contract) | Documentation contract for new callables. Existing inconsistencies are recorded for [#184](https://github.com/squid-farm/talewright/issues/184) and [#193](https://github.com/squid-farm/talewright/issues/193); no syntax changed. |
| Checkpoints/autosaves | Syntax is still to be chosen in [#54](https://github.com/squid-farm/talewright/issues/54) using the checklist. `@once` means storylet eligibility, not a checkpoint policy. No checkpoint marker is reserved by this document. |

Unimplemented contracts need scoped implementation issues agreed with the project manager before coding.
This registry records the gaps; it does not silently expand #197 or change the tracker.
