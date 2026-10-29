# Naming conventions

Read this before adding or renaming author-facing settings, syntax or examples.
It applies to human contributors and coding agents.

## Story settings and Talemark

- Use lowercase kebab-case for multiword YAML setting keys: `first-name`,
  `name-agreement`, `possessive-pronoun`, `governing-attribute`.
  Do not copy a C# member's casing into the author-facing format.
- Recommend lowercase kebab-case for author-defined IDs, such as `lost-relic`
  and `tracking`. Dots can group Talemark names, such as `ending.empty-handed`.
  This is style, not a narrower parser rule; see the
  [identifier rules](../reference/talemark-operators.md#names-and-values).
- Display names and story text are reader-facing prose, not identifiers:
  `id: grit` can have `name: Grit`.
- Reuse existing terms and syntax rather than introducing a second name for the
  same concept. Consult the [operator registry](../reference/talemark-operators.md)
  before adding Talemark syntax.

## Implementation names

Follow the surrounding language and API conventions. For example, the YAML key
`governing-attribute` maps to the C# property `GoverningAttribute`; C# locals and
parameters use camelCase. YAML conventions do not rename C# APIs, JSON protocols
or existing external contracts.

## Changing a name

Update the reader, diagnostics, schema, editor support, documentation, examples
and tests together. Test the new spelling and ensure the rejected spelling is
diagnosed rather than accepted as an alias. Pre-alpha changes do not need
compatibility aliases or migrations.

If an issue or proposed implementation conflicts with these conventions, flag
the mismatch and agree on the spelling before coding. Record approved
clarifications on the issue and PR, then use the
[language-change checklist](language-changes.md) to cover every affected surface.
