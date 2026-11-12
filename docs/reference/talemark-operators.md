# Talemark operator registry

Talemark is the language inside `.tale` files. This is the authoritative list of its markers and
operators. See the [syntax reference](talemark-syntax.md) for complete stories, the [glossary](glossary.md) for
terms, and the
[syntax principles](../developer/language-changes.md#syntax-principles) and
[language-change checklist](../developer/language-changes.md) before proposing new syntax.

Everything on this page describes what the compiler accepts today. Changes that are planned but not
built yet are listed separately under [planned changes](#planned-changes).

## Names and values

Story identifiers start with an ASCII letter (`A`-`Z`, `a`-`z`) or `_`. The remaining characters are
ASCII letters, digits or `_`, with optional single `.` or `-` separators between groups.
Separators cannot be adjacent or end a name. Names are case-sensitive.
Lowercase kebab-case is a style recommendation, not a parser restriction. The scene/story
naming lint rules TWL001 and TWL002 check that style only when a story enables them.

- Valid: `weapon.knife`, `camp.arrival`, `_guide`, `north-gate`, `act.2`.
- Invalid: `2doors`, `camp..arrival`, `north.-gate`, `door-`, `café`.
- `weapon.knife` is one name, not a read of `knife` on `weapon`.
- Use `:` for access: `faction:explorers-guild:standing`, `player:hp`.
- Use spaces around subtraction: `var:coins - 1`. `var:coins-1` reads the name `coins-1`.
  A minus at the start of a numeric operand means negation: `-3`.

The same story-name character set applies in the compiler, manifest schema and editor grammar.
Expression words such as `true`, `false`, `and`, `or`, `not`, `has` and `visited` have special meanings
in their expression positions; a valid spelling is not a guarantee that a name is available there.
Named predicates use these same case-sensitive identifier rules, including dotted and hyphenated
names such as `@Can.Open-door`. Invalid predicate names are rejected at C# registration.
Manipulator names depend on their registration contract.
Pronoun properties use `character:pronoun:subject`, `:object`, `:reflexive`,
`:possessive-pronoun:determiner` and `:possessive-pronoun:independent`.
`character:pronoun` is subject shorthand. Dots remain part of names, not property access.

### Reader text, quotes and escapes

Reader-facing text inside syntax uses double quotes.
Names remain bare. `"Joel's cat"` is text; `weapon.knife` is a name. Single quotes are not a second
Talemark string delimiter: apostrophes are ordinary text.

Inside double-quoted strings, `\"` means a literal double quote and `\\` means a literal
backslash. Expression strings, manipulator arguments and image paths accept only those two escapes.
In reader text, a backslash before any ASCII punctuation or symbol quotes that character, so
`\{` and `\}` show literal braces; a backslash before letters, digits, whitespace or non-ASCII
characters remains visible. Image alt text uses this reader-text rule.

Write `"C:\\stories"` for the text `C:\stories`: a literal backslash inside a Talemark string
is always doubled. Malformed expression strings report TW1001; manipulator arguments report TW2006. An unclosed
enclosing brace can additionally produce TW1004. Scene titles, dialogue and choice labels are not
quoted: `@title The door`, `> guide: Hello.`, `* [Open the door] -> hall`. Quotes added to those
forms are shown to the reader as part of the text.
Image calls use `{image("path", "alt text")}`. The path is a literal string. Alt text uses the
same reader-text grammar as prose. Both arguments decode `\"` and `\\`; other path escapes produce
TW1014, while unknown letter escapes in alt text remain literal. A nested `{image(...)}` in alt
text also produces TW1014. See the
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
| Expression | An expression slot. | Substitutions, `{if}`, `@requires`, `(requires ...)`, assignment values, quantities and check DCs. | A typed value: number, boolean, text or tier. Never changes state. | `player:hp > 0`, `player visited camp`, `camp:player:visits` |
| Text manipulator | `#name` after a substitution's value. | The end of a substitution only. | Text or formatted text. Applied left to right. Never changes state. | `#capitalize`, `#bold`, `#one-many("is", "are")` |
| Effect | A statement in a `~` line or a choice `{ ... }` block, separated by `;`. | Effect lines and choice effects. | A state change, with no value. | `set flag:x`, `give player rope 2`, `var:coins += 1` |
| Clause | A fixed word, or a word followed by `:`, attached to a directive. | Choices, checks and hub requirements. | Modifies its directive. | `(requires ..., shown "reader text")`, `success:`, `skill-check perception dc 12` |

### Argument slots

Every position that holds author input has exactly one slot kind. The slot kind, never the content,
decides how that input is read. Quotes only delimit an argument; they do not decide whether `*` or
`{` has meaning inside it. **If a slot accepts substitutions, it is reader text.**

| Slot kind | How it is read | Delimited by | Escapes | Current slots |
|---|---|---|---|---|
| Name | One bare identifier using the [shared name rules](#names-and-values). | The construct's punctuation or whitespace. | None. | Scene IDs, `player visited <scene>`, effect targets, item and character IDs. |
| Tag | Nonempty text without commas, trimmed. Not a story name. | Commas, or the end of the line. | None. | `@tags` values and `@hub` tags. |
| Integer literal | A whole number; each slot sets its own range. | End of line. | None. | `@priority` (any integer) and `@hub-limit` (positive). |
| Expression | The [expression grammar](#value-and-expression-operators) and binding order. | The construct: braces, parentheses or end of line. | Only inside its string literals. | `{if ...}`, substitutions, `@requires`, `(requires ...)`, assignment values. |
| Literal text | Exact text after decoding quote escapes. No substitutions or formatting. | Double quotes. | `\"` and `\\`; any other escape is an error. | Image paths, expression string literals, manipulator arguments. |
| Reader text | The [reader-text grammar](#reader-text-grammar): formatting and inline constructs. | A text-unit boundary, or double quotes when inside other syntax. | Reader-text escapes, plus `\"` when quoted. | Prose, speech text, choice labels, `@choice-label`, image alt text, and the optional text in a `shown` clause. |
| Plain line text | The rest of the line, trimmed. No substitutions or formatting. | End of line. | None. | `@title`. |

Reader text that sits inside other syntax, such as a `shown` clause or image alt text, uses double
quotes. Speech and choice labels keep their unquoted forms.

### Callable construct contract

A *callable* is any named construct that takes arguments or works like a method: predicates,
text manipulators, the image constructor, `@hub` groups, effect commands and check clauses.
Each callable is documented in this registry with:

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
New callables follow this guideline. Existing exceptions are listed below.

| Callable | Family and result | Arguments (slot kind) | Contexts | Notes |
|---|---|---|---|---|
| `player visited scene` | Expression predicate; boolean. | One scene name. | Expressions. | True when the player has entered the scene at least once. |
| `scene:player:visits`, `<scene>:player:visits` | Expression reference; number. | Current-scene alias or one scene name. | Expressions. | Counts player entries, including repeated entries; other-character visit counts are unsupported. |
| `character:inventory:item`, `character:inventory:item:name` | Expression reference; text. | Character ID and item ID. | Expressions. | Same shared item display name as `item:<id>`, whether or not the character holds it. |
| `character:inventory:item:count` | Expression reference; number. | Character ID and item ID. | Expressions. | Reads the held quantity, or 0 when not held; read-only, changed with `give` or `take`. |
| `character has item` | Expression predicate; boolean. | Character name, item name. | Expressions. | Infix word, not a call. |
| `@Name` | Expression predicate; boolean. | None. | Expressions. | Registered by a C# story project. |
| `#capitalize`, `#upper`, `#lower`, `#a`, `#the`, `#this`, `#that`, `#possessive` | Text manipulator; text. | None. | End of a substitution. | Chain left to right before any formatting manipulator. |
| `#bold`, `#italic` | Text manipulator; formatted text with a `TextStyle`. | None. | End of a substitution. | Lowered to the same `StyledTemplate` model as prose formatting; can follow text manipulators and each other, but not be followed by text manipulators. |
| `#one-many("singular", "plural")` | Text manipulator; text. | Two literal-text arguments, both required. | Number-valued expressions, pronoun and item-name references. | Only multi-argument manipulator. |
| `image("path", "alt")` | Inline construct; media. | Literal-text path, required; reader-text alt, optional. | The whole content of a `{ }` in reader text. | Not an expression; no `#` chaining. |
| `tag and tag [and tag…]` | Hub intersection; all listed tags. | Two or more distinct tags. | `@hub` only. | Do not mix with `or`. |
| `tag or tag [or tag…]` | Hub union; at least one listed tag. | Two or more distinct tags. | `@hub` only. | Do not mix with `and`. |
| `set flag:id`, `clear flag:id` | Effect. | One flag reference. | Effects. | Word command. |
| `give`/`take character item [n]` | Effect. | Character name, item name, optional number expression (default 1). | Effects. | Space-separated arguments. |
| `skill-check [actor] skill dc n[, hidden]` | Clause. | Optional character name, skill name, number expression, optional hidden modifier. | Choices, `:::` blocks and as the whole condition of a block `{if}`. | Keyword sequence. |

Known inconsistencies (see [planned changes](#planned-changes)):

- Boolean queries have three spellings: a sentence (`player visited camp`), an infix word (`player has rope`)
  and a sigil (`@CanPickLock`).
- Hub selectors use sentence forms without grouping; `and` and `or` are reserved tag names.
- Tags are not story names. `@tags` accepts spaces inside a tag, such as `@tags north gate`
  giving one tag. A tag operand in `@hub` cannot contain whitespace, parentheses or commas; spaces
  separate tags and the `and`/`or` selector words.
- Manipulator names are scanned up to `#` or `(` instead of with the shared name rules.
- Only `#one-many` takes arguments; there is no general multi-argument manipulator support.

## Structural markers

These markers select a construct rather than taking part in arithmetic. Their precedence is
**structural**: line directives are recognized before prose formatting; braces inside prose are
recognized before bold and italic formatting. Each row gives its arguments and boundaries.
Escaping follows the quote/prose rules above: a leading backslash before ASCII punctuation is
handled as reader text, so `\* [Go] -> hall` is prose rather than a choice.

| Marker | Meaning, context and arguments | Canonical example | Diagnostics |
|---|---|---|---|
| `===` | Start a scene on its own line; followed by one scene ID. | `=== camp.arrival` | TW1008 invalid ID; TW3010 duplicate; TW3001 text before a scene. |
| `@` | Scene metadata directly after its header. `title` takes plain line text for the heading; `choice-label` takes reader text for hub choices and bare scene-divert choices. Hub choices fall back to `title`, then the scene ID. `requires` takes a condition with optional `, shown ["reader text"]`; `tags` comma-separated tags; `priority` an integer; `once` no argument; `hub` a tag or a tag list joined with `and` or `or`; `hub-limit` a positive integer with `hub`. The shown text replaces the choice text only while its requirement is false. | `@requires player:hp > 0, shown "You are too tired."` | TW1006 misplaced, duplicate or invalid metadata or shown clause; TW1002 invalid priority; expression errors for conditions. |
| `->` | Immediate divert in a scene, choice or check outcome; takes one scene ID or `END`. Later scene steps do not run. Conditional early exits precede choices; there is no fallback/gather. | `-> camp.arrival` | TW1010 missing target; TW1008 invalid ID; TW2002 unknown target; TW3011 after choices. |
| `*` | Choice at the start of a line (`* ` or `*[`). A prose `[label]` is optional with an explicit `->` target; a bare `* -> scene` uses the destination's `@choice-label` if present, otherwise the label is **Continue**. `* -> END` still shows **Continue**. At most one `(requires condition[, shown ["reader text"]])` and one `{ effects }` work on either form, in either order before the terminal divert or skill check. The opening `(requires ` is exact. The optional shown text is reader text that replaces the label only while the requirement is false. A choice skill check needs a label. This is not multiplication in this context. | `* [Go] (requires flag:ready, shown "You need the key.") -> hall` | TW1011 malformed, duplicate or misplaced choice clause; TW3003 no usable target. |
| `[` `]` | Bound a choice label; its contents are reader text with substitutions and formatting. Balanced inner brackets are literal text; the scanner skips backslash-escaped brackets and inline constructs. | `* [Open [the] door] -> hall` | TW1011 missing closing bracket. |
| `>` | Dialogue at the start of a line; takes a character ID, `:`, then prose. Not a block quote. | `> guide: Follow me.` | TW1007 malformed speech; TW2001 unknown character. |
| `~` | Effect batch at the start of a line; executes in source order. Triggers settle after the whole semicolon-separated batch. | `~ var:coins += 1` | TW1003 malformed effect; TW3011 after choices; name/type errors. |
| `{` `}` | In prose, delimit a substitution or `if`/`else`/`/if`; in choices and outcomes, delimit effects. Block conditions occupy their own lines and accept either a boolean expression or a whole skill-check condition. Escape literal prose braces as `\{` and `\}`. | `{if player:hp > 0}Ready{else}Rest{/if}` or `{if skill-check perception dc 12, hidden}` | TW1004 unclosed brace; TW1005 unmatched condition; TW1011 unclosed choice effect; TW1009 unclosed outcome effect. |
| `{image(...)}` | Construct image media in any narrative-template context, including choices and dialogue. Required quoted literal path and optional quoted alt, comma-separated. Alt uses the prose template language and cannot contain a nested image. Append `#decorative` only when the image has no descriptive alt text. Whitespace before `(` is allowed. This is a dedicated media construct, not a general function-call language or a property read. | `{image("maps/camp", "{guide:first-name} at the camp")}` or `{image("ornament.png")#decorative}` | TW1014 malformed call/escape, unsupported manipulator or nested image; TW2015 contradictory alt; TW2016 inaccessible choice label. Asset validation uses TW2010–TW2014. |
| `:::` | Delimit a skill check executed at this line. Opening takes `skill-check [actor] skill dc number-expression[, hidden]`; closing line has only `:::`. Both outcomes are required; an entry outcome without a target continues at the next line. | `::: skill-check perception dc 12, hidden` (close with `:::`) | TW1012 unknown/unclosed block; TW1009 missing outcomes; TW3011 after choices. |
| `:` in labels | Separates skill-check outcome labels from their contents: `success:`, `failure:`. Not a property read in this context. | `success: -> hall` | TW1009 misplaced/missing outcomes. |
| `//` | Comment when it begins a trimmed line; the rest of the line is ignored. It is not a trailing-comment operator. | `// The guard remembers this.` | None for comment contents. |

`END` is the reserved divert destination that ends the story, not a scene declaration.
`skill-check`, `dc` and `hidden` are words, not new sigils. A hidden check records its roll for
replay and the Author Debugger but does not add a player check note. On a choice, write
`* [Climb] skill-check athletics dc 14` and follow it with `success:` and `failure:` lines.
Choices form the final block, optionally nested in conditions. Every choice and choice-check outcome
requires an explicit target (TW3003). Block conditions select a branch once; inline conditions evaluate
at their prose line. A block `{if}` can use a skill check as its whole condition, but checks cannot be
used in inline conditions, requirements or trigger conditions, and cannot be combined with `and`,
`or` or `not`. Unreachable steps after a guaranteed exit produce warning TW3012.
Unknown names, incorrect types and malformed expressions can also produce the expression diagnostics
below. See [error codes](diagnostics.md) for the complete messages.

## Value and expression operators

Precedence numbers refer to the [binding order](#binding-order) below. **Structural** means the
operator belongs to a surrounding construct; **name** means it is part of a token, not an operation.
No expression operator has an escape that turns it into a name. Put literal punctuation in text,
using the quoted-text rules when it is inside syntax.

| Operator | Meaning, context and arguments | Binding | Canonical example | Diagnostics |
|---|---|---|---|---|
| `:` | Access a named value/property in expressions and substitutions; also select an effect target. Repeats for nested access. Never use `.` instead. | 1 | `{faction:explorers-guild:standing}` | TW2001 unknown name/property or old spelling; TW2004 undeclared flag/variable. |
| `.` | Group words within one identifier, never access a property. | Name | `{item:weapon.knife}` | Invalid identifiers produce TW1001 in expressions, TW1008 for scenes, TW4003 in YAML. |
| `-` in names | Separate groups within a name; not subtraction. | Name | `{player:first-name}` | Same identifier diagnostics as `.`. |
| `#` | Text manipulator in a substitution. Takes the value to its left and returns text or formatted text; repeated manipulators run left to right. Inside quoted text it is literal content. | After the value expression | `{player:pronoun:reflexive#capitalize}` | TW2006 unknown manipulator, bad argument syntax or value, or incompatible chain. |
| `(` `)` | Group an expression; also bound manipulator arguments, choice conditions, and hub tag groups. | 1 for grouping; otherwise structural | `{(var:coins + 1) * 2}` | TW1001 expression mismatch; TW2006 manipulator; TW1011 choice; TW1006 hub. |
| `"` | Bound text literals, manipulator string arguments and image arguments. All decode `\"` and `\\`; no single-quoted Talemark string form. | 1 | `{("a # literal")#capitalize}` | TW1001 malformed expression; TW2006 malformed manipulator; TW1014 malformed image; TW1004 enclosing brace not found. |
| `\` | Escape a quote or backslash inside literal strings, or ASCII punctuation and symbols in reader text. | During text scanning | `Literal \{braces\}` | Unsupported string escapes are errors; unescaped `{` can cause TW1004. Not a numeric or boolean operator. |
| `,` | Separate metadata tags, hub arguments, image arguments and manipulator arguments. Each manipulator defines its own values. | Structural | `{image("camp", "The camp")}` | TW1014 invalid image; TW1006 invalid hub; TW2006 for manipulator arguments. `#one-many` is the only manipulator that takes multiple arguments; general multi-argument support is not implemented. |
| Unary `-` | Negate one number. | 2 | `{var:coins + -3}` | TW2003 non-number; TW1002 oversized literal. |
| `*` `/` | Integer multiplication and division; two numbers. A slash also appears in `{/if}` and `//` comments, with structural meanings. | 3 | `{var:coins / 2}` | TW2003 non-numbers; TW1001 missing operand. |
| `+` `-` | Integer addition and subtraction; two numbers. Space subtraction away from names. | 4 | `{var:coins - 1}` | TW2003 non-numbers; TW1001 missing operand; TW2004 if an unintended hyphenated variable is undeclared. |
| `==` `!=` | Equality/inequality of matching types (number, boolean, text or tier). | 5 | `{if item:rope == "Rope"}Found it.{/if}` | TW2003 mismatched types; TW1001 malformed expression. |
| `<` `<=` `>` `>=` | Ordered comparison of numbers or reputation tiers. | 5 | `{if faction:guild:tier >= friendly}Welcome.{/if}` | TW2003 mismatched/unordered types; faction values suggest `:standing` or `:tier`; TW1001 malformed expression. |
| `not` | Negate a boolean condition. | 6 | `{if not player visited camp}New here.{/if}` | TW2003 non-boolean; TW2002 unknown scene. |
| `and` | Both boolean conditions must hold. | 7 | `{if player:hp > 0 and player has rope}Climb.{/if}` | TW2003 non-booleans. |
| `or` | At least one boolean condition must hold. | 8 | `{if flag:ready or player:hp > 0}Go.{/if}` | TW2003 non-booleans; TW2004 undeclared flag. |
| `has` | Inventory predicate: character ID on the left, item ID on the right. Item counts live only in inventory, not variables. | 1 | `{if player has rope}Equipped.{/if}` | TW2001 unknown character/item. |
| `player visited <scene>` | Boolean predicate with one bare scene ID. | 1 | `{if player visited camp}Back again.{/if}` | TW2002 unknown scene; other-character subjects are unsupported. |
| `scene:player:visits`, `<scene>:player:visits` | Numeric visit count for the current or named scene. | 1 | `{if camp:player:visits > 1}Back again.{/if}` | TW2002 unknown scene; TW2003 non-number in numeric operations. |
| `character:inventory:item`, `character:inventory:item:name` | Shared item display name for any declared character. | 1 | `{if player:inventory:rope == item:rope}Found it.{/if}` | Reads the definition even when not held; unknown character or item is TW2001. |
| `character:inventory:item:count` | Numeric quantity of an item held by any declared character. | 1 | `{if player:inventory:rope:count >= 2}Enough rope.{/if}` | Reads 0 when not held; unknown character, item or property is TW2001; read-only, use `give` or `take`. |
| `@` in expressions | Named boolean predicate registered by a C# story project; no arguments. Distinct from line metadata. | 1 | `{if @CanPickLock}Try the lock.{/if}` | TW2005 unregistered predicate. |
| `=` `+=` `-=` | Assign, add or subtract a number in an effect targeting a stat, variable or faction standing. Text variables support `=` only and require a text expression. Not comparison. Inventory references are read-only. | Structural | `~ player:hp -= 3`; `~ faction:guild:standing += 2`; `~ var:weather = "stormy"` | TW1003 malformed effect; TW2001 inventory count assignment (use `give` or `take`) or item-name assignment; TW2003 incompatible types or numeric operations on text; a faction target needs `:standing`; name errors. |
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

1. Parenthesized expressions, literals, references, `has`, `player visited <scene>`, named predicates.
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

### Manipulator arguments

Manipulator arguments use the shape `#name("first", "second")`: commas separate double-quoted
arguments. Argument values belong to the manipulator; they are not globally reserved names.
Each manipulator validates its own supported values and reports a clear diagnostic for invalid ones.
Today only `#one-many` takes arguments; passing arguments to any other manipulator is an error.

Pronoun forms are properties, not manipulators; for example,
`{player:pronoun:possessive-pronoun:independent}` gives *hers*. The selector
`#one-many("singular", "plural")` requires exactly two quoted strings, singular
first and plural second. A number-valued expression selects the first string only
when its evaluated value is exactly `1`; every other value selects the second.
This includes inventory counts, stats, number variables and arithmetic. Current
Talemark numbers are integers. Pronoun references select from the effective
pronoun set's `agreement` at render time; singular they uses plural agreement.
On an item-name reference, `{item:pants#one-many("is", "are")}` selects from
item `name-agreement` instead. Character names, text variables and other
non-number values are not selector targets. Empty strings are allowed. Following
text manipulators operate on the selected text, for example
`#one-many("has", "have")#capitalize`.
This is dedicated two-string selector support, not generalized multi-argument parsing.
Other built-in manipulators take no arguments:
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
Completion currently offers `one-many` on pronoun and item-name references; the
same selector is valid after number-valued expressions. Its strings are author
text, not finite argument suggestions.
Wrong targets, missing strings and extra arguments produce TW2006.

### Image syntax

Images use `{image("path", "alt text")}`, with optional alt text, in every
narrative-template context. The path stays literal. Alt text is rendered with the prose template
language where the image appears; under the [reader-text grammar](#reader-text-grammar)
it is always reader text. It constructs media; it is not an expression function returning text.
The value is the media node, with no arithmetic precedence or `#` chaining. Its strings decode
`\"` and `\\` once. Alt text also treats `\{` and `\}` as literal braces. Quoted paths can contain
filename punctuation without becoming story identifiers.
`{image:name}` still means the `name` property of a character called `image`.
There is no `@image` directive, and the Markdown `![alt](path)` form is rejected (TW1015).
Images can appear anywhere reader text can, except inside alt text; plain-text fields such as
`@title` do not accept them.

## Prose formatting and Markdown images

These formatting delimiters use Talemark's
[reader-text grammar](#reader-text-grammar) below, not a general Markdown parser.

| Marker | Meaning, arguments and context | Binding and escaping | Canonical example | Diagnostics |
|---|---|---|---|---|
| `**...**`, `__...__` | Bold prose, choice labels or dialogue. | Inline formatting after brace parsing; backslash can escape formatting punctuation. | `**Danger**` | Unmatched formatting markers generally remain text, not a Talemark error. |
| `*...*`, `_..._` | Italic prose, choice labels or dialogue. A leading `* ` is a choice instead. | Same formatting stage and escaping. | `*Quietly*` | Same unmatched-marker behavior. |
| Backticks | Ordinary reader text; they have no formatting or escaping role. | Literal punctuation. | `` `torch` `` | None. |
| `![alt](path)` | Rejected Markdown image form. | Detected after brace parsing, with the diagnostic mapped back to the author's source. | Use `{image("torch", "A torch")}`. | TW1015 reports unexpected reader-text syntax. |
| `[text](target)` | Literal reader text; it is not a hyperlink feature. | No link parsing or target validation. | `[Go](https://example.org)` | None. |

## Reader-text grammar

CommonMark is not the specification. Anything not listed here is literal text.
The [parser rationale](../developer/architecture.md#reader-text-parser-rationale) explains why
Talemark owns this grammar.

### Text units

Reader text is parsed one *text unit* at a time:

- **Paragraph**: consecutive prose lines, ended by a blank line or a line directive. A `//`
  comment line is skipped without ending the paragraph. Each line is trimmed, and each authored
  line break renders as a line break.
- **Speech text**: the rest of a `>` line after the speaker's `:`.
- **Choice label**: the contents of the label brackets.
- **Image alt text**: the second quoted argument of `image(...)`.

Formatting and inline conditions never cross a text unit's boundary. An inline construct may span
lines only inside a paragraph. Block directives, including block `{if}` lines, are single lines.

### Order of recognition

1. Line directives decide the text units.
2. Within a unit, a single left-to-right scan recognizes escapes, inline constructs, then emphasis
   and strong delimiters. Inline constructs are atoms: a `*` or `_` inside `{ }` is never formatting.
3. Inline `{if}`, `{else}` and `{/if}` pair only with markers in the same formatting span.
   Otherwise they report TW1005. Inline conditions nest at most 64 deep (TW1016).

### Escapes

A backslash before any ASCII punctuation or symbol character quotes exactly that character. The
backslash is removed, and the character is literal rather than opening syntax. `\\` therefore
shows one backslash. Backslashes before letters, digits, whitespace or non-ASCII characters stay
visible: `C:\stories`, `\a` and `\—` display as written. This applies in prose, dialogue, choice
labels and image alt text. A quoted image path remains strict; a quoted alt also uses `\"` for a
quote. Unknown letter escapes in alt text remain literal.

A line-ending unpaired `\` reports TW1017; remove it because line breaks render as written. A
line-ending `\\` shows one literal backslash and is not an error. A line whose content renders
empty, such as a false inline conditional on its own line, does not leave an empty line. A blank
line still ends the paragraph.

### Emphasis and strong

- `*` and `_` are the only delimiter characters. A *run* is a maximal sequence of the same
  character that is not escaped and not inside an inline construct.
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
- Matched spans nest at most 64 deep. A deeper opening run reports TW1016 and is literal.
  Unmatched opening runs remain literal and do not count toward this limit.

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
whitespace, so multiline conditions and arguments work. Talemark has no line-continuation
character.

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
the whole candidate literal. It does not decode Markdown escapes or entities. TW1015 points at
the opening `!` and reports unexpected reader-text syntax.

### Image alt text

Alt text always uses this grammar, whether or not it contains `{`. The reader sees plain text:
formatting is dropped, and backticks remain ordinary text. Its escapes are `\"` plus the
reader-text punctuation rule; unknown letter escapes remain literal. A nested `{image(...)}` still
reports TW1014. Missing-alt warnings are decided from the authored alt, not the rendered text.

### Example corpus

These examples show how the grammar reads tricky input; the compiler's tests cover each one.
A ⏎ marks a line break in the source. `[I: ...]` is emphasis and `[B: ...]` is strong.

| # | Input | Result |
|---|---|---|
| 1 | `Lost line` ⏎ `---` | `Lost line` ⏎ `---` |
| 2 | `Lost line` ⏎ `-` | `Lost line` ⏎ `-` |
| 3 | `[Inventory]: empty` | Literal. |
| 4 | `[the inn][1] is here` | Literal. |
| 5 | `[Go](https://example.org) now` | Literal. |
| 6 | `&amp; &copy; &#65;` | Literal. |
| 7 | `<b>tag</b>`, `https://example.org` | Literal. |
| 8 | `# Not heading`, `- not list`, `1. not list` | Literal. |
| 9 | `2*3*4 and snake_case_name` | Literal. |
| 10 | `word*inner*word` | Literal. |
| 11 | `***both***` | Literal; write `**_both_**`. |
| 12 | `**bold *italic* bold**` | `[B: bold [I: italic] bold]` |
| 13 | `*a **b* c**` | `*a [B: b* c]` |
| 14 | `**unclosed and *also` | Literal. |
| 15 | `_under_ and __strong__` | `[I: under] and [B: strong]` |
| 16 | ``` `code {player:first-name} **x**` ``` | `` `code Ada [B: x]` `` |
| 17 | ``` `a \{b\} c` ``` | `` `a {b} c` `` |
| 18 | ```` `` a`b `` ```` | ```` `` a`b `` ```` |
| 19 | `\* \_ \[ \] \{ \} \\` | `* _ [ ] { } \` |
| 20 | `\. \# \> \& \a` | `. # > & \a` |
| 21 | `line one\` ⏎ `line two` | TW1017; remove `\` and the authored newline remains. |
| 22 | `She said "hi" (quietly) [aside]` | Literal. |
| 23 | `* [Open [the] door] -> END` | Label `Open [the] door`. |
| 24 | `* [Open [door] -> END` | TW1011. |
| 25 | `* [Say "hi" (quietly)] -> END` | Label `Say "hi" (quietly)`. |
| 26 | `* [Open \] door] -> END` | Label `Open ] door`. |
| 27 | `Hello {player:first-name` | TW1004; the rest is literal. |
| 28 | `Close } stray` | Literal. |
| 29 | `{if true}**a{else}b**{/if}` | TW1005. |
| 30 | `{if flag:a and` ⏎ `flag:b}Both.{/if}` | Works. |
| 31 | `{image("portrait", "**The guide**")}` | Alt `The guide`. |
| 32 | `{image("portrait", "**{guide:first-name}**")}` | Alt `Bea`. |
| 33 | ``` {image("portrait", "`code` and \\ slash")} ``` | Alt ``` `code` and \ slash ```. |
| 34 | `{image("portrait", "a \* b")}` | Alt `a * b`. |
| 35 | `![A torch](torch)` | TW1015. |
| 36 | `![a [b]](p)` | Literal. |
| 37 | `![a\]](p)` | Literal. |
| 38 | `![a](p(q))` | Literal. |

## Dotted-prefix completion

In a recognized reference position, completion filters on the whole typed name, including dots.
With items `weapon.knife`, `weapon.sword` and `rope`, `{item:weapon.` offers only the two
`weapon.*` items. The dot is part of the replacement range, not a traversal into another object.
The same prefix filtering applies to scene targets, character references, stats, factions, flags
and variables in their recognized positions.
This filtering applies only in recognized reference positions, not in plain prose, new declarations
or arbitrary quoted strings.

## Not Talemark syntax

Checkpoints and autosaves have no Talemark syntax. Autosave is configured in the manifest, and a
trigger with `save: true` keeps a checkpoint; see [Writing stories](../guide/writing-stories.md).
`@once` controls hub eligibility, not saving.

## Planned changes

These are tracked but not built. Until they ship, the rules above are the whole language.

- Syntax consistency ([#184](https://github.com/squid-farm/talewright/issues/184)): whether speech and
  choice labels move to quoted reader text, one spelling for boolean queries, aligned tag
  rules, and further manipulators.
- Typed, extensible manipulators, including registration and shared name rules for manipulator names
  ([#193](https://github.com/squid-farm/talewright/issues/193)).
- Formatting beyond bold and italic ([#200](https://github.com/squid-farm/talewright/issues/200)).

New syntax follows the [language-change checklist](../developer/language-changes.md) and needs a scoped
issue before implementation.
