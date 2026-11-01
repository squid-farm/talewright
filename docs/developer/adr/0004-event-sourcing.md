# ADR 0004: Event-sourced game state

**Status:** Accepted

**Decision:** every change is a `GameEvent`, and state is a fold over the event log. This supports state reconstruction from an existing log, rewind, debugging, and history-based conditions such as `visited(...)`. Dice results are stored in events, so folding an existing log does not reroll. Player saves instead store a seed, choice history, cursor, and character selection; restore reruns the story choices with that seed to create a new event log.

## Delivered save compatibility boundary

Player saves identify the format and story identity/version and include the source/extension compatibility
fingerprint. They reject corrupt, unsupported, and incompatible data explicitly. Restore builds a replacement
session before adopting it, so a failed load does not partially mutate the active session or overwrite the
original save. Round-trip and deterministic replay are covered on desktop and web.

Event sourcing does not by itself make saves compatible with changed stories or event schemas. Migration
machinery has its own M8 milestone with explicit old/new contracts and fixtures; it is not part of M3
authoring. M8 depends on M2 saves, not on intervening gameplay milestones, and can be pulled forward
when compatibility changes require it.
See [the roadmap](../plan.md#core-production-readiness-gates-m2), [FR-026 / #34](https://github.com/squid-farm/talewright/issues/34),
and [FR-031 / #39](https://github.com/squid-farm/talewright/issues/39).
