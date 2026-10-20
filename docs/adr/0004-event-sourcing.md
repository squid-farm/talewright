# ADR 0004: Event-sourced game state

**Status:** Accepted

**Decision:** every change is a `GameEvent`, and state is a fold over the event log. This gives cheap saves (seed plus log), rewind, debugging, and history-based conditions such as `visited(...)`. Dice results are stored in events, so replays are deterministic.
