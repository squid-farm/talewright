# Talewright.Cli

The `talewright` command-line tool for Talewright story authors. It creates, checks, plays, debugs and
publishes stories written in Talemark.

```bash
dotnet tool install --global Talewright.Cli --version VERSION
talewright new my-story
talewright validate my-story
talewright run my-story
```

Replace `VERSION` with a published version, such as `0.5.0-preview.1`; preview versions must be
requested explicitly. Installing the .NET tool needs the .NET 10 SDK. Standalone downloads are also on
[GitHub Releases](https://github.com/squid-farm/talewright/releases); see the guides below for what
each command needs.

| Command | Does |
|---|---|
| `talewright new` | Creates a runnable starter story. |
| `talewright validate` | Checks a story for errors and warnings. |
| `talewright lint` | Runs compiler checks plus configurable style rules. |
| `talewright run` | Validates and plays a story in a native window. |
| `talewright debug` | Opens the Author Debugger. |
| `talewright publish` | Builds a story ZIP, web game or desktop game. |

Every command and option is described in the [CLI reference](../../../../docs/reference/cli.md).
[Get the story-checking tools](../../../../docs/guide/cli-installation.md) walks through installation
step by step.

## For contributors

- Run from source: `dotnet run --project tooling/cli/src/Talewright.Cli -- validate samples/LostRelic`
- Commands live in `CommandLine.cs`, `NewStoryCommand.cs`, `LintCommand.cs`, `RunCommand.cs`,
  `DebugCommand.cs` and `PublishCommand.cs`. `PublishAssets/` holds the host templates, which ship inside
  the tool package, so installed tools need no source checkout.
- Tests supply terminal flags and readers through `CliInput`, and build/archive delegates through
  `PublishCommand.Operations`. Custom stdout or stderr writers are treated as non-interactive, so tests
  never read from the real stdin.
- `talewright run` and `talewright debug` share one native host launcher; the player starts only after
  validation succeeds. The Linux native suite covers real startup, save/reload, window close and Ctrl+C:
  `python3 player/tests/run_e2e.py linux --filter 'FullyQualifiedName~NativeCliRunTests'`.
