# ADR 0001: Custom storylet engine instead of ink

**Status:** Accepted

Ink and Yarn Spinner keep state as flat global variables and lists. Stat-heavy RPG logic then either leaks into the prose or becomes a second state store in C#.

**Decision:** build a custom quality-based narrative ("storylet") engine. Content units become eligible through typed predicates over rich game state. This handles on-rails play (explicit diverts) and open-world play (hubs) with the same core.
