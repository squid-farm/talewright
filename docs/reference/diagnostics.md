# Error codes

Every problem Talewright reports has a code and a location:

```text
chapters/02-temple.tale:14:23: error TW2002: Unknown scene 'temple.hal'.
└── file ─────────────┘ │  │         └ code ┘ └ message ──────────────┘
                     line column
```

**Errors** stop the story from loading. **Warnings** (marked ⚠) don't, but usually point at a mistake.
When you see several errors, fix the first one and check again: one mistake can cause others.

## Editor project configuration

| Code | Meaning | Usual fix |
|---|---|---|
| TW5001 | Required C# authoring registrations are missing. | Explicitly export and configure data-only project metadata; editors never build or execute project code automatically. |
| TW5002 | The language server cannot analyze the story with its configured metadata. | Fix the reported metadata file/data error and refresh it. See [language-server configuration](../developer/language-server.md). |
| TWLSP004 | The language server cannot read or parse `talewright.lint.json`. | Fix the reported configuration error; live analysis continues with default rule settings until the file is valid. |

## TW1xxx: writing mistakes in `.tale` files

| Code | Meaning | Usual fix |
|---|---|---|
| TW1001 | Something unexpected in a condition or effect. | Check spelling and operators, e.g. `player:dex >= 12`. |
| TW1002 | A number is too large, or `@priority` isn't a whole number. | Use a whole number. |
| TW1003 | Malformed effect. | Stats and variables use `=`, `+=` or `-=`. Flags use `set flag:x` / `clear flag:x`. |
| TW1004 | A `{` is never closed. | Close it, or write `\{` for a literal brace. |
| TW1005 | Unbalanced `{if}` / `{else}` / `{/if}`. | Inline conditions must open and close in the same paragraph. Block conditions need their own lines. |
| TW1006 | Bad scene metadata (`@title`, `@hub`, `@hub-limit`, ...). | Put metadata directly after the `===` line, once each. `@hub-limit` needs `@hub`. |
| TW1007 | Malformed dialogue line. | `> who: text`, where `who` is a character id. |
| TW1008 | Invalid scene id. | Use the shared ASCII identifier form: start with a letter or `_`, then use letters, digits, `_`, and single `.` or `-` separators, e.g. `temple.entrance`. |
| TW1009 | Skill check outcomes are missing or misplaced. | Every check needs both `success:` and `failure:` lines directly below it. |
| TW1010 | `->` without a target. | `-> scene.id` or `-> END`. |
| TW1011 | Malformed choice. | `* [label] (requires: ...) { effects } -> target`. Check for missing `]`, `)` or `}`. |
| TW1012 | Unknown or unclosed `:::` block. | Only `::: skill-check` is supported, and it ends with `:::`. |
| TW1013 | Malformed skill check. | `skill-check [character] skill dc 12`. |
| TW1014 | Malformed image call or image string escape. | Write `{image("path", "alt text")}` or `{image("path")}`. Quote both arguments, separate them with a comma, and use `\"` or `\\` for embedded quotes or backslashes. |
| TW1015 | Old Markdown image syntax. | Replace `![alt](path)` with `{image("path", "alt")}`; the message gives the escaped replacement. |

## TW2xxx: names and types

| Code | Meaning | Usual fix |
|---|---|---|
| TW2001 | Unknown character, stat, item or faction. | Fix the spelling, or declare it in `story.talewright.yaml`. |
| TW2002 | Unknown scene. | Fix the spelling, or add the scene. |
| TW2003 | Wrong kind of value, e.g. assigning text to a number, comparing text with a number, or arithmetic on text. | Keep each variable's declared type. Text uses `=` and text-to-text `==` / `!=`; numbers retain arithmetic. Declare text with a quoted initial value under `vars:`. |
| TW2004 | A flag or variable is checked but never set. | Fix the spelling, set it somewhere, or declare it under `flags:` / `vars:`. |
| TW2005 | Unknown `@predicate`. | Named predicates come from C# extensions; check the name or register it. |
| TW2006 | Unknown `#modifier`. | Use `#possessive`, `#capitalize`, `#upper`, `#lower` or `#a`. |
| TW2007 | A variable's integer or text type cannot be inferred, e.g. a circular assignment chain without a known type. | Declare an initial value under `vars:` in the manifest, or fix the unresolved expression. Types are fixed; discovery never executes assignments. |
| TW2010 | Unsafe image path. | Use a relative path inside `images/`, with `/` separators and no parent traversal, absolute path, web address, query string or fragment. |
| TW2011 | Unsupported image format. | Use PNG (`.png`), JPEG (`.jpg` or `.jpeg`), or WebP (`.webp`). |
| TW2012 | Image file not found. | Put the file in the story's `images/` folder and match its path's case exactly. |
| ⚠ TW2013 | An extensionless image path matches several files. | Include the extension to choose explicitly. Otherwise the first match wins in `.png`, `.jpg`, `.jpeg`, `.webp` order. |
| ⚠ TW2014 | Image alt text is missing, empty or whitespace-only. | Supply descriptive alt text. The fallback is the humanized filename in sentence case, or `Image` if no words remain. |

Story compilation, `talewright validate` and editor analysis report these image codes
through the same resolver. Diagnostics point to the image path's opening quote in
the authored narrative, including dialogue and choices. Malformed image calls receive
syntax diagnostics instead of resolver errors. Editors refresh the catalogue and
diagnostics when files change. Lookup reads paths, not image bytes; it does not
validate file contents or render images. Successful compilation stores the selected
story-root-relative path and effective alt text in each media node.

## TW3xxx: story structure

| Code | Meaning | Usual fix |
|---|---|---|
| TW3001 | Text before the first scene, or no `.tale` files at all. | Start each file with `=== scene.id`. |
| TW3002 | Dead end: a scene with no way out. | Add choices, `-> target` or `-> END`. |
| TW3003 | A choice or check outcome has no target, and the scene has no `->` to fall through to. | Give it a target or add a `->` line to the scene. Also appears as a side effect of TW2002. |
| TW3005 | The ruleset has no skill-check rule. | Use the `generic` ruleset, or remove the checks. |
| TW3006 | More than one `::: skill-check` block in a scene, or one inside `{if}`. | Move the check into its own scene. |
| TW3007 | The `start` scene doesn't exist. | Fix `start:` in `story.talewright.yaml`. |
| ⚠ TW3008 | A hub has no scenes with its tags. | Tag some scenes, or fix the hub's tag. |
| ⚠ TW3009 | A scene can never be reached. | Link to it, or remove it. Only shown once there are no errors. |
| TW3010 | Two scenes have the same id. | Rename one. The message says where the first one is. |

## TW4xxx: `story.talewright.yaml`

| Code | Meaning | Usual fix |
|---|---|---|
| TW4001 | Invalid YAML. | Check indentation (spaces, not tabs) and colons. |
| TW4002 | Something required is missing: `story.talewright.yaml` itself, `id`, `title`, `start`, the `player` character, a character's `first-name`, a stat's `id`, or a trigger's `id`/`when`/`do`. | Add it. |
| TW4003 | A value is invalid, e.g. not a number, out of range, or not allowed here. Includes invalid identifiers. | Read the message for the allowed values. |
| TW4004 | Unknown ruleset. | Use `generic` or `none`. |
| TW4005 | Unknown stat or item in `story.talewright.yaml`. | Fix the spelling, or declare it under `ruleset.stats` / `items`. |
| ⚠ TW4006 | Unknown key (ignored). | Usually a typo, e.g. `charaters:`. |
| TW4007 | Two triggers have the same id. | Rename one. |

## TW5xxx: authoring integration

| Code | Meaning | Usual fix |
|---|---|---|
| TW5001 | Project authoring metadata is required but missing. Built-in-only analysis cannot fully represent the project. | Explicitly supply the project's compiler options or exported [authoring metadata](../developer/authoring-analysis.md#c-project-registrations). Editors never automatically execute project code. |

## Lint rule codes

Lint rules use stable `TWL` IDs, separate from compiler diagnostic codes. They run on shared source-aware
analysis and do not replace or duplicate compiler diagnostics. Their severities and per-file suppressions
are configured in the optional story-root `talewright.lint.json`; see the [`lint` CLI reference](cli.md#lint).

| Code | Meaning | Usual fix |
|---|---|---|
| TWL001 | A scene ID is not lowercase kebab-case, optionally grouped with dots. | Use lowercase words separated by hyphens, and dots to group scenes (for example `ending.empty-handed`). |
| TWL002 | The manifest story ID is not lowercase kebab-case. | Use a short lowercase ID with words separated by hyphens (for example `night-ferry`). |

## Command-line codes

`talewright validate` and `talewright lint` report these for problems with the command itself rather than
the story. See the [CLI reference](cli.md#output-contract).

| Code | Meaning |
|---|---|
| TWCLI001 | Invalid command or options. |
| TWCLI002 | An input/output operation failed, an existing publish destination needs `--force` in noninteractive use, the destination changed during publication, or a required file, tool or desktop component is missing. |
| TWCLI003 | `talewright lint` configuration is invalid. |
