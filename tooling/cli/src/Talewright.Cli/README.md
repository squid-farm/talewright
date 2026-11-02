# Talewright.Cli

The `talewright` command-line tool: `validate`, `lint`, `run`, `debug` and `publish`.

- **Using it:** see the [CLI reference](../../../../docs/reference/cli.md).
- **Run from source:** `dotnet run --project tooling/cli/src/Talewright.Cli -- validate samples/LostRelic`
- **Code:** commands live in `CommandLine.cs`, `LintCommand.cs`, `RunCommand.cs`, `DebugCommand.cs`
  and `PublishCommand.cs`; `PublishAssets/` holds the templates. Templates ship inside the tool package, so
  installed tools need no source checkout.

All commands accept `--non-interactive` after the command name. It guarantees no CLI prompts,
not overwrite permission or a different validation mode. Only `publish` asks a CLI question today:
existing outputs require terminal confirmation (default No) or explicit `--force`.

Publishing stages a sibling temporary ZIP before replacement. Approval is scoped to the checked
destination's content and metadata; a second check rejects observable changes before replacement.
These are separate filesystem operations, not compare-and-swap or crash-durability guarantees.
`--force` deliberately bypasses the approval/state check.

Tests supply `CliInput` terminal flags/readers and `PublishCommand.Operations` build/archive delegates.
The public injected-writer entry point treats custom stdout or stderr writers as noninteractive, avoiding
unexpected reads from process stdin. Prompts and cancellation use the injected error writer.
The native player's and debugger's shared settings do not change their GUI confirmations or scenario
behavior. They use the same child-process launcher and native prerequisites; the player starts only
after validation succeeds.

The Linux native suite includes `NativeCliRunTests` for real player startup, save/reload, window close,
and Ctrl+C/task termination. Run it with
`python3 player/tests/run_e2e.py linux --filter 'FullyQualifiedName~NativeCliRunTests'`.
