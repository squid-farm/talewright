# ADR 0004: Event-sourced game state

**Status:** Accepted

**Decision:** every change is a `GameEvent`, and state is a fold over the event log. This gives cheap saves (seed plus log), rewind, debugging, and history-based conditions such as `visited(...)`. Dice results are stored in events, so replays are deterministic.

## Planned save compatibility boundary

M2 saves must identify the save-format version and the story identity/version, define the compatibility
policy, and reject corrupt, unsupported, or incompatible saves explicitly. A failed load must not partially
mutate the active session or overwrite the original save. Round-trip and deterministic replay tests are
release criteria on desktop and web.

Event sourcing does not by itself make saves compatible with changed stories or event schemas. Migration
machinery is deferred until a concrete compatibility change establishes the old/new formats and desired
behavior; it is not part of the M3 authoring milestone.
See [the roadmap](../PLAN.md#core-production-readiness-gates-m2) and [FR-026](../ISSUES.md#fr-026).
