# ADR 0006: Talemark owns its reader-text grammar

**Status:** Accepted. Supersedes only the Markdig portion of [ADR 0003](0003-tale-markup.md).
Implementation is tracked in [#392](https://github.com/squid-farm/talewright/issues/392).

## Context

ADR 0003 parsed the prose inside Talemark with Markdig, a CommonMark library. Talemark already
owns its line directives, braces, choice-label brackets and quoted arguments, so the parser masks
those constructs, runs Markdig on what remains, walks Markdig's tree and maps positions back.
That layering has three costs:

- **Silent text loss.** A prose line followed by `-`, `--` or `---` disappears as a setext
  heading. `[Inventory]: empty` disappears as a link reference definition. `[the inn][1]` loses its
  opening bracket. Authors receive no diagnostic.
- **Accidental language.** Entity decoding (`&amp;` becomes `&`), intraword emphasis
  (`2*3*4` italicises `3`), triple runs and CommonMark's delimiter rules are Talemark behavior
  only because Markdig implements them. No Talemark document promised them.
- **Bounded-cost work fights the library.** Cancelling a long paragraph needed a second Markdig
  pipeline, a cancellation context and two Markdig parser subclasses. Depth failures arrive as a
  library exception matched by message text.

The project lead decided to "drop most of the markdown specs unless we've called it out
specifically" and not to add Markdown behavior that conflicts with Talemark syntax.

## Decision

Talemark defines a small reader-text grammar of its own, and CommonMark is not its
specification. The normative rules and the example corpus are in the
[reader-text grammar](../../reference/talemark-operators.md#reader-text-grammar) section of the
operator registry. In summary, decided by the lead in the #399 discussion:

1. **Minimal formatting whitelist.** Only `*`/`_` (emphasis), `**`/`__` (strong) and backtick
   code spans. Emphasis and strong delimiters work only at word boundaries, so `2*3*4`,
   `snake_case` and `word*inner*word` stay literal. Runs of three or more stay literal, including
   `***both***`. Properly nested spans work; crossing or unmatched delimiters stay literal.
   "It's easier to expand than it is to take away."
2. **Everything else that looks like Markdown is literal text.** That covers headings (including
   setext underlines), reference definitions, links, entities, HTML and lists. TW1015 is retained
   for the rejected `![alt](path)` image form because it has a direct replacement.
3. **Legacy links become literal.** `[Go](https://example.org)` displays exactly as written instead
   of keeping `Go` and discarding the target.
4. **Backslash escapes only Talemark's own delimiters.** A trailing backslash still makes a hard
   line break inside a paragraph. Other backslashes are literal in unquoted prose.
5. **Substitution-capable means reader text.** "If it accepts a substitution, it must go through
   the reader." Image alt text is always reader text, not only when it contains `{`. Contexts that
   do not accept substitutions remain separate plain or literal text.
6. **Multiline braces stay open.** Current multiline `{...}` support inside paragraphs is
   preserved. Newlines inside a braced expression are whitespace. No continuation character and no
   single-line restriction is added. The broader policy, including multiline block directives, is
   deliberately deferred so the language can evolve. "Don't paint us into a corner."
7. **Choice clauses keep both accepted orders.** [#384](https://github.com/squid-farm/talewright/issues/384)
   continues to own choice-clause ordering. This ADR does not fix an order.
8. **Line-directive escapes are out of scope.** [#404](https://github.com/squid-farm/talewright/issues/404)
   remains a separate, decision-gated proposal.

The same discussion asked for a consistent structure across every Talemark construct, not just
prose. The registry therefore also records [construct families and argument slots](../../reference/talemark-operators.md#construct-families-and-argument-slots).
Each argument slot declares whether it holds a name, an expression, literal text or reader text.
Quotes delimit an argument; they never decide how its contents are read.

## Rejected alternatives

| Alternative | Why it loses |
|---|---|
| Keep Markdig and patch each loss | Every patch fights library behavior that Talemark never wanted. Masking, mapping and cancellation plumbing remain. Future Markdig upgrades can change author-visible output. |
| Write a full CommonMark-compatible parser | Imports dozens of rules authors never asked for, conflicts with Talemark brackets and line directives, and makes later simplification a breaking change. |
| Compatibility mode that mirrors Markdig output | Pre-1.0 Talemark needs no compatibility machinery merely because it once delegated to a library. A temporary Markdig oracle may find differences during #401, but never overrides an approved rule. |
| CommonMark intraword `*` (emphasis inside `word*inner*word`) | Treats arithmetic and identifiers in prose as formatting. One boundary rule for `*` and `_` is easier to explain. |
| Triple runs as bold italic | Adds a special case with no author request. Nest instead: `**_both_**`. |
| Keep legacy link text and drop the target | Silently discards author text. Links can return as an explicit Talemark feature if one is designed. |
| Treat alt text as a template only when it contains `{` | Makes the meaning of `*` in alt depend on whether an unrelated substitution is present. The slot kind should decide. |
| End-of-line recovery or a single-line rule for braces | Would break current multiline conditions and close off future multiline forms before the lead decides that policy. |
| Make any backslash escape any punctuation | Copies CommonMark. Talemark only needs escapes for characters that have Talemark meaning, and a smaller set leaves room for future escapes such as #404. |

## Language-change checklist assignment

This ADR is the decision and contract step of the
[language-change checklist](../language-changes.md). It changes no parser, runtime or grammar code.

| Surface | Owner |
|---|---|
| Bounded, mode-aware delimiter scanning | [#400](https://github.com/squid-farm/talewright/issues/400) |
| Parser and diagnostics, corpus tests, silent-loss regressions, rendering and accessible alt text | [#401](https://github.com/squid-farm/talewright/issues/401) |
| VS Code grammar and highlighting (the `markup.*` patterns follow the boundary rules) | #401 |
| Completion and hover, which already use shared authoring analysis | #401 verifies tokens and ranges; no new completion surface |
| Registry status, syntax reference, diagnostics docs and samples | #401 when behavior ships; this ADR for the contract |
| Remaining duplicate delimiter scanners | [#403](https://github.com/squid-farm/talewright/issues/403) |
| Package and notice removal | [#402](https://github.com/squid-farm/talewright/issues/402) |
| Choice-clause order | [#384](https://github.com/squid-farm/talewright/issues/384), unchanged |

## Consequences

- [#400](https://github.com/squid-farm/talewright/issues/400) adds a bounded Talemark scanner and
  [#401](https://github.com/squid-farm/talewright/issues/401) replaces Markdig with it, including
  regression coverage for the three silent-loss cases and the corpus.
  [#402](https://github.com/squid-farm/talewright/issues/402) removes the package.
- Cancellation of a long paragraph becomes a periodic check in one linear scanner instead of
  library subclasses.
- Some current output changes when #401 ships. The registry corpus lists each before/after pair.
  Until then, the current behavior described in the reference remains in effect.
- [#200](https://github.com/squid-farm/talewright/issues/200) still owns broader typography and
  presentation. This ADR settles only the parser and library question for existing formatting.
