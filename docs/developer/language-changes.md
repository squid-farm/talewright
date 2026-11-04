# Language-change checklist

Use this checklist when proposing any Talemark syntax, including checkpoints, images or formatting.
The [operator registry](../reference/talemark-operators.md) is the source of truth for existing
meanings and the difference between implemented and decided syntax.

## Before implementation

- [ ] Read the [naming conventions](naming-conventions.md); keep author-facing
  spelling consistent and resolve any conflicting issue wording before coding.
- [ ] State the author task in plain English, with one small canonical example.
- [ ] Check the registry. Reuse an existing sigil only for its existing meaning:
  `:` accesses properties, `#` manipulates a value into text and chains left to right,
  `.` groups words inside a name.
- [ ] If proposing a new sigil, explain why the existing ones cannot express the task clearly.
  List its contexts, arguments, boundaries and any conflict with prose or existing syntax.
- [ ] Record rejected alternatives and why they lose. Include at least the best existing-sigil
  alternative; do not silently settle an unresolved project decision in code.
- [ ] Define precedence and associativity, or say that the construct is structural.
- [ ] Use bare names with the shared identifier character set and double-quoted reader text.
  Specify how commas and the backslash escapes work. Include apostrophes, embedded quotes,
  literal backslashes and literal braces in examples.
- [ ] For each modifier, define accepted argument values, omitted/default behavior and error cases.
  Values belong to that modifier, not to a global reserved-word list.
- [ ] Place the construct in a [construct family](../reference/talemark-operators.md#construct-families-and-argument-slots)
  and give every argument a slot kind from that section, such as name, expression, literal text or
  reader text. If a slot accepts substitutions, it is reader text; never infer the kind from the
  argument's content.
  For a callable, complete the [callable construct contract](../reference/talemark-operators.md#callable-construct-contract)
  and follow its shape guideline, or justify the exception.
- [ ] Define errors and source ranges for malformed, incomplete and obsolete spellings.
  Pre-1.0 changes need no compatibility mode; old forms must name their replacement.
- [ ] Check identifier collisions, hyphen/subtraction ambiguity and interactions with nested
  conditions, formatting and quoted delimiters.
- [ ] Identify any decided-but-unimplemented prerequisite. Agree on a scoped issue with the
  project manager rather than expanding a syntax slice into resolver, runtime or presentation work.
- [ ] Keep the change reviewable. Split oversized issues with the project manager before coding.

## Ship together

For each surface, implement the change or explicitly explain why it is unaffected:

| Surface | Required review |
|---|---|
| Parser and diagnostics | Lexer, expression syntax grammar, enclosing delimiters, semantic binding, precise positions, obsolete-form replacement messages. `ExpressionSyntaxParser` owns expression grammar; `ExpressionBinder` owns symbol resolution and type validation. Identify which is affected and explicitly call out unaffected surfaces. Keep parser behavior and the registry in agreement. |
| Manifest schema | Name rules and any new data shapes; schema/compiler agreement on valid and invalid fixtures. YAML quoting is a separate layer from Talemark quoting. |
| VS Code grammar | Highlighting, strings, escapes, punctuation and identifier boundaries; do not accept a wider name set than the compiler. Keep every rule readable at a glance: no lookbehinds and no context-dependent rules such as bold/italic flanking. Leave constructs that need the parser uncolored rather than approximating them with regex. |
| Language server and shared authoring analysis | Completion, replacement ranges, hover, live diagnostics, partial/incomplete input, finite argument suggestions and default details. Never execute project code merely to offer completion. |
| Docs and examples | Registry, syntax reference, diagnostics, guides, snippets, shipped samples and test-story fixtures. Label any remaining decided-versus-current gap with its implementation issue. |
| Runtime/player, when affected | Rendering, state changes, persistence, accessibility and host behavior; avoid claiming a parser-only change supplies them. |

### Grammar spellings

Each Talemark spelling lives once, in [`Talewright.Talemark.Vocabulary`](../../engine/src/Talewright.Talemark/Vocabulary/).
That includes expression keywords, reference namespaces, properties, operators, scene and choice markers, inline conditions,
skill-check terms, scene metadata keys, text escape and quote markers, and the `END` target.
The parser turns text into a typed value at the boundary, such as `BinaryOp`, `AssignOp`, `ReferenceNamespace`,
`PronounForm` or `SceneMetadataKey`.
Keep vocabulary members internal by default; expose only the spellings that another assembly's tooling must consume.
Identical characters in unrelated syntaxes, such as Talemark escapes and legacy Markdown, retain separate semantic owners.
After that:

- Binding and analysis switch on the typed value.
- The formatter, completion and messages read spellings back from the vocabulary. Authoring tokens carry an
  `AuthoringTokenKind`, and [`AuthoringSyntaxVocabulary`](../../engine/src/Talewright.Talemark/Vocabulary/AuthoringSyntaxVocabulary.cs)
  supplies shared spellings and descriptions to hover and completion, including examples composed from owned spellings.
  It also provides completion triggers, quoted-content escaping, and formatting for modifier and hub-selector text
  used by authoring tools.
- No code compares against a hand-typed copy.

To add a term:

1. Add its constant to the appropriate vocabulary type.
2. If it selects behavior, map it to an exhaustive typed value (for example `SceneMetadataKey`).
3. Use the constant from parsing through formatting, completion and authoring where applicable.
   Add or update the typed authoring-help entry when the term is exposed in hover or completion.
4. Update the [editor vocabulary cases](../../tooling/vscode-talewright/tests/fixtures/vocabulary.json)
   with a representative source example, expected TextMate scopes and an exact syntax-reference example.
   Compound spellings can specify separate scopes; intentionally unhighlighted characters use the root scope.

Identifier characters belong to `IdentifierRules`, and text-modifier names belong to their modifier or registry
(for example `OneManyTextModifier.ModifierName` and `TextModifierRegistry`).

Complementary tests enforce these boundaries:

- [TalemarkVocabularyGuardTests](../../tooling/language-server/tests/Talewright.LanguageServer.Tests/TalemarkVocabularyGuardTests.cs) fails on any string or character literal whose decoded value, as Roslyn reads it, equals a vocabulary spelling.
  - It covers the Talemark, authoring and language-server sources, including authoring-help projections but excluding the vocabulary definitions themselves; built-in modifier names come from the runtime registry.
  - Single-character spellings are checked only in the listed expression, authoring and reader-text parser files,
    not in unrelated path, YAML or Markdown handling.
  - Remaining exemptions name the issue that removes them.
- [ExpressionVocabularyTests](../../engine/tests/Talewright.Talemark.Tests/ExpressionVocabularyTests.cs) checks that every typed expression value has a spelling that maps back, and every lexer symbol produces its declared token. Scene metadata uses an exhaustive `SceneMetadataKey` mapping.
- [EditorVocabularyContractTests](../../engine/tests/Talewright.Talemark.Tests/EditorContracts/EditorVocabularyContractTests.cs)
  checks fixture coverage against the vocabulary and built-in modifier registry, and requires each documented example
  to occur in the syntax reference. The editor's `compiler-contract` target runs it alongside manifest contracts.
- The [TextMate vocabulary contract](../../tooling/vscode-talewright/tests/grammar.test.mjs) tokenizes the same
  examples with the real grammar and checks every non-whitespace character against its expected scope.

## Verification and review

- [ ] Test canonical examples and invalid inputs, not just token acceptance. Cover quoted punctuation,
  escapes, defaults, modifier order, old spellings and identifier collisions where applicable.
- [ ] For each new compiler diagnostic, use a literal code in the compiler source, document its meaning
  in the [diagnostic reference](../reference/diagnostics.md), and add an emitting fixture to
  [CompilerDiagnosticCoverageTests](../../engine/tests/Talewright.Talemark.Tests/CompilerDiagnosticCoverageTests.cs).
  The contract compiles each fixture and checks the actual code and severity; add a valid counterpart and
  precise location checks for the changed syntax. Authoring and lint codes have their own scope and tests.
- [ ] Verify dotted-prefix completion in each relevant symbol category. For example, `weapon.`
  must offer only matching `weapon.*` names, with the correct whole-token replacement range.
  Record gaps and route follow-up tracker changes through the project manager.
- [ ] Run the smallest existing compiler/schema/editor tests that cover the change. Run docs link checks.
  Add broader runtime or E2E checks only when behavior requires them; coordinate shared performance runs.
- [ ] Update the registry status for implemented rules. A documented decision is not evidence that
  its parser support exists.
- [ ] Include the issue, decisions, rejected alternatives, migration examples and exact validation
  evidence in one small PR, then stop for review.

For [checkpoint syntax (#54)](https://github.com/squid-farm/talewright/issues/54), first decide
whether the operation is scene metadata or an executable effect. Reuse the corresponding marker
only if that meaning fits. Define timing and first-encounter behavior separately from `@once`,
which already controls storylet eligibility. Do not reserve a new checkpoint sigil before that review.
