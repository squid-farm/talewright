# Talewright CLI

Requires the .NET 10 SDK. Run from source with
`dotnet run --project src/Talewright.Cli -- validate samples/LostRelic`.
The project packs as a .NET tool with command name `talewright`.

```bash
talewright validate path/to/story
talewright validate path/to/story --format json --warnings-as-errors
```

Validation uses the same compiler as the player. It checks a directory containing
`story.yaml` and `.tale` files. Static validation does **not** prove every possible
playthrough succeeds. Warnings succeed unless `--warnings-as-errors` is supplied.

## Output contract

- Exit **0**: accepted validation (or help).
- Exit **1**: compiler errors, or warnings rejected by the selected policy.
- Exit **2**: invalid invocation or input/I/O failure.

Human output uses the compiler's `file:line:column: severity CODE: message`
diagnostics and a final status. Operational errors go to stderr.

`--format json` writes one JSON object to stdout, with no progress messages:

```json
{"schemaVersion":1,"success":true,"diagnostics":[]}
```

Every diagnostic has `code`, `severity` (`error` or `warning`), `message`, `file`
(story-relative path), `line`, and `column`. Source positions are one-based;
zero denotes an unspecified position. Compiler severities are not rewritten by
warnings-as-errors.

Operational failures return exit 2 and include `success:false`, empty
`diagnostics`, and `error:{"code":"TWCLI001","message":"..."}` for invocation
errors or `TWCLI002` for input errors. A missing manifest in an otherwise readable
directory is a compiler diagnostic, not an operational error.
