# ADR 0004: Event-sourced game state

**Status:** Accepted

**Decision:** every change is a `GameEvent`, and state is a fold over the event log. This gives cheap saves (seed plus log), rewind, debugging, and history-based conditions such as `visited(...)`. Dice results are stored in events, so replays are deterministic.

## Planned save compatibility boundary

M2 saves must identify the save-format version and the story identity/version, define the compatibility
policy, and reject corrupt, unsupported, or incompatible saves explicitly. A failed load must not partially
mutate the active session or overwrite the original save. Round-trip and deterministic replay tests are
release criteria on desktop and web.

Event sourcing does not by itself make saves compatible with changed stories or event schemas. Migration
machinery has its own M8 milestone with explicit old/new contracts and fixtures; it is not part of M3
authoring. M8 depends on M2 saves, not on intervening gameplay milestones, and can be pulled forward
when compatibility changes require it.
See [the roadmap](../PLAN.md#core-production-readiness-gates-m2), [FR-026 / #34](https://github.com/squid-farm/talewright/issues/34),
and [FR-031 / #39](https://github.com/squid-farm/talewright/issues/39).
