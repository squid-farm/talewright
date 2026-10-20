# ADR 0002: Blazor UI hosted by Photino (desktop) and WebAssembly (web)

**Status:** Accepted

MAUI Blazor Hybrid has no Linux support. Photino.Blazor runs on Windows, macOS and Linux. Blazor WASM publishes as static files, so it can be hosted on itch.io or GitHub Pages without a server.

**Decision:** one Razor Class Library holds the UI, with a thin Photino host and a thin WASM host around it.
