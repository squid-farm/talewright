# Repository agent guidance

Follow [contributor guidance](CONTRIBUTING.md) and the [issue workflow](docs/developer/issues.md),
including its [project decision-making policy](docs/developer/issues.md#project-decision-making).
This file points to project sources of truth; it does not replace them.

## Talemark changes

When proposing or changing Talemark syntax, or changing how existing syntax is parsed, diagnosed,
analysed, highlighted, documented or executed, read the
[language-change checklist](docs/developer/language-changes.md) before implementation. Consult the
[operator registry](docs/reference/talemark-operators.md), [naming conventions](docs/developer/naming-conventions.md)
and [architecture tour](docs/developer/architecture.md) as well.

- The registry describes implemented syntax separately from its **Planned changes**, which are
  tracked but not built. Verify behavior against the code and tests; a documented decision is not
  evidence of implementation.
- Treat the [design decisions](docs/developer/plan.md) as historical rationale, not a description
  of current behavior. The [roadmap](docs/roadmap.md) is direction, not a schedule.
- GitHub Issues are the source of issue status. Per the [issue workflow](docs/developer/issues.md),
  `status:idea` and `status:deferred` are not approved work. Respect issue scope and recorded
  dependencies; coordinate unresolved scope rather than implementing a proposal by assumption.
- Review the applicable surfaces in the checklist: parser and diagnostics, manifest schema,
  compiler-backed semantic tokens, the separately maintained TextMate grammar, language-server and
  shared authoring analysis, docs/examples, and runtime/player. Explicitly identify unaffected
  surfaces.
- Follow the checklist and the [compatibility freeze](CONTRIBUTING.md#compatibility-freeze) for
  compatibility decisions. Keep the documented pre-1.0 flexibility distinct from post-1.0 guarantees;
  do not create a separate policy or infer one from a historical plan.

Unrelated tasks do not need to read the full language-change documentation.
