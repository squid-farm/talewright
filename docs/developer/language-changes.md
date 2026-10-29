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
| Parser and diagnostics | Lexer, value/argument scanner, enclosing delimiters, semantic validation, precise positions, obsolete-form replacement messages. Keep parser behavior and the registry in agreement. |
| Manifest schema | Name rules and any new data shapes; schema/compiler agreement on valid and invalid fixtures. YAML quoting is a separate layer from Talemark quoting. |
| VS Code grammar | Highlighting, strings, escapes, punctuation and identifier boundaries; do not accept a wider name set than the compiler. |
| Language server and shared authoring analysis | Completion, replacement ranges, hover, live diagnostics, partial/incomplete input, finite argument suggestions and default details. Never execute project code merely to offer completion. |
| Docs and examples | Registry, syntax reference, diagnostics, guides, snippets, shipped samples and test-story fixtures. Label any remaining decided-versus-current gap with its implementation issue. |
| Runtime/player, when affected | Rendering, state changes, persistence, accessibility and host behavior; avoid claiming a parser-only change supplies them. |

## Verification and review

- [ ] Test canonical examples and invalid inputs, not just token acceptance. Cover quoted punctuation,
  escapes, defaults, modifier order, old spellings and identifier collisions where applicable.
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
