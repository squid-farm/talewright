# Save format

Player saves, Quick Save, autosave and exported backups all use one binary format, stored with the
`.twsave` extension. A save holds replay inputs only: story identity, seed, character choices and the
choice history. It never stores the event log or game state. Restoring replays the choices into a
fresh session (see [Architecture](architecture.md)).

Code and the generated MessagePack formatters:
[`SaveFormat.cs`](../../engine/src/Talewright.Runtime/Saves/SaveFormat.cs) and
[`SavePayload.cs`](../../engine/src/Talewright.Runtime/Saves/SavePayload.cs).

## Layout

| Bytes | Content |
|---|---|
| 4 | Magic `TWSV` |
| 1 | Format version, currently `4` |
| 4 | Decoded body length, unsigned little-endian |
| rest | zlib stream (RFC 1950, `CompressionLevel.SmallestSize`) of the body |

The body is an array-keyed MessagePack object. Its positions are the schema:

| Index | Save | Encoding |
|---:|---|---|
| 0 | Story id | string |
| 1 | Story version | string |
| 2 | Content fingerprint | string |
| 3 | Random seed | unsigned 64-bit integer |
| 4 | Choice cursor | signed 32-bit integer |
| 5 | Character selection | nil or a five-element array |
| 6 | Choice history | array of choice-key strings |
| 7 | Display summary | nil or a binary value containing a MessagePack summary array |

The character array is `[first name, last name, pronouns, preset, stats]`. Optional strings are
`nil`; `stats` is an array of `[id, value]` arrays, sorted by id before writing. The summary array
is `[scene title, UTC ticks, UTC offset minutes, character name]`. It is stored as a separate
MessagePack byte sequence so damaged display metadata can be discarded without rejecting replay
data.

The runtime uses MessagePack-CSharp 3.1.12 with its compile-time source-generated formatters and
`MessagePackSecurity.UntrustedData`. No protobuf schema, handwritten wire codec, or runtime-generated
formatter is used. MessagePack-CSharp and its runtime dependencies are MIT-licensed.

The reader requires the decoded length to equal the declared length and the file to end with the
body's Adler-32 checksum. .NET's `ZLibStream` accepts a truncated stream or one missing its checksum,
so these checks are done explicitly.

Saves written by earlier players (JSON, format 2), the previous proposed protobuf format 3, and
unknown format versions are reported as unsupported. There is no migration.

## Array-key rules

Array positions are permanent. Never reorder or reuse a position; add new fields only at the end.
Older readers skip unknown trailing positions, while new readers supply defaults for missing trailing
positions. The MessagePack source generator and the frozen layout tests keep the annotated key
positions and wire layout aligned.

Character statistics use an array rather than a map so their serialization order is deterministic.
The save format does not depend on reflection, typeless deserialization, or dynamic code generation;
the generated formatter works in desktop and Blazor WebAssembly builds.

`SaveFormatTests` enforces these rules:
- A frozen v4 sample must keep loading. Regenerate it only for a deliberate format change with
  `TALEWRIGHT_REGENERATE_SAVE_SAMPLES=1`.
- The top-level and nested array positions, types, and ordering are checked against a representative
  save.
- Missing trailing fields and unknown trailing fields are covered.
- Randomized round trips, truncations, mutations, malformed MessagePack, and cross-platform body
  stability are covered.

## Limits

| Limit | Value | Enforced |
|---|---|---|
| Compressed save | 2 MiB | Writing, storage, import and reading |
| Decoded body | 8 MiB (declared length checked first; decompression stops one byte past it) | Writing and reading |
| Choices | 10,000 | Playing and reading; array size checked before deserialization |
| Choice key | 1–512 characters | Saving and reading |
| Character stats | 1,000 | Reading; array size checked before deserialization |
| Summary UTC offset | ±14 hours | Reading (invalid details are dropped, not fatal) |

Collection counts are checked with `MessagePackReader` before the generated deserializer allocates
their lists. Other decoded structures are bounded by the 8 MiB body limit. A repeated-choice
playthrough of about 200 choices compresses below 4 KiB.

## Compatibility

A save loads only into the same story id, story version and content fingerprint. The fingerprint
covers every story source file and extension identity, so any edit to the story breaks existing saves.
The author-facing rules are in [Sharing your story](../guide/sharing-your-story.md) and
[Updating a story with existing saves](../guide/playing-and-saving.md#updating-a-story-with-existing-saves).

Player messages:
- A version or fingerprint mismatch: "Version X of Title has changed, and this save is no longer
  compatible…" ("Title has changed…" when no version is known).
- A different story: "This save belongs to a different story…".
- Replay failure: a generic incompatible-or-damaged message, with no choice number.

Desktop and web saves are interchangeable. Deflate output may differ between platforms or .NET
versions, so tests compare decoded bodies, not compressed bytes. The browser tests load host-written
saves in WASM and decode WASM-written exports on the host.
