# Talewright.Cli

The `talewright` command-line tool: `validate`, `debug` and `publish`.

- **Using it:** see the [CLI reference](../../docs/reference/cli.md).
- **Run from source:** `dotnet run --project src/Talewright.Cli -- validate samples/LostRelic`
- **Code:** commands live in `CommandLine.cs`, `PublishCommand.cs` and `DebugCommand.cs`; `PublishAssets/` holds the templates. Templates ship inside the tool package, so
  installed tools need no source checkout.
