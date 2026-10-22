# Author Debugger

Native Photino/Blazor workbench for local Tale folders. Prefer
`talewright debug path/to/story`; from source:

```sh
dotnet run --project src/Talewright.Debugger -- samples/LostRelic
```

Requires .NET 10 and a graphical desktop with WebKitGTK 4.1 (Linux),
WebView2 (Windows), or WKWebView (macOS). Linux x64 is verified; other targets
require platform testing.

The workbench uses `AuthorSession` for fresh-state scene jumps and recorded
inspection. It reuses Player rendering/creation components but has its own
`debug-assets` root and does not access player save storage. The UI-independent
model is tested with CLI tests; actual native interactions run through the
existing Linux AT-SPI E2E harness. Source changes require explicit Reload story.
See [CLI usage](../Talewright.Cli/README.md#author-debugging) for behavior and limits.
