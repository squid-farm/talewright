# Save format

Player saves, Quick Save, autosave and exported backups all use one binary format, stored with the
`.twsave` extension. A save holds replay inputs only: story identity, seed, character choices and the
choice history. It never stores the event log or game state. Restoring replays the choices into a
fresh session (see [Architecture](architecture.md)).

Code: [`SaveFormat.cs`](../../engine/src/Talewright.Runtime/Saves/SaveFormat.cs) and
[`ProtoWire.cs`](../../engine/src/Talewright.Runtime/Saves/ProtoWire.cs). Schema:
[`save-format.proto`](../../engine/src/Talewright.Runtime/Saves/save-format.proto).

## Layout

| Bytes | Content |
|---|---|
| 4 | Magic `TWSV` |
| 1 | Format version, currently `3` |
| varint | Decoded body length |
| rest | zlib stream (RFC 1950, `CompressionLevel.SmallestSize`) of the body |

The body is a protobuf-wire message (the `Save` message in the schema). It uses Deflate from
`System.IO.Compression`, which works in Blazor WASM and adds no download size. Brotli is unavailable
in WASM, and LZMA saved only 4–10% more for a large dependency.

The reader requires the decoded length to equal the declared length and the file to end with the
body's Adler-32 checksum. .NET's `ZLibStream` accepts a truncated stream or one missing its checksum,
so these checks are done explicitly.

Saves written by earlier players (JSON, format 2) and unknown format versions are reported as an
unsupported format. There is no migration.

## Schema and field-number rules

The `.proto` file is documentation only; nothing is generated from it. The encoder and reader are
hand-written from the [protobuf encoding specification](https://protobuf.dev/programming-guides/encoding/).
No serialization library ships with the player.

- **Field numbers are permanent.** Never reuse a field number or change a field's type. Mark removed
  fields `reserved` in the schema forever.
- **New fields are safe.** Readers skip fields they don't know, so new metadata can be added without
  breaking older players. Missing fields read as their defaults.
- **Choice history uses a string table.** `choice_keys` lists each distinct key once, and `choices`
  is a packed list of indexes into it. Readers also accept unpacked `choices`, as the spec requires.
- **Always-positive numbers** are unsigned varints. Signed values (stat values, UTC offset) use zigzag.
- Changing the body in an incompatible way requires a new format version byte and a new frozen sample.

`SaveFormatTests` enforces these rules:
- The schema's field numbers and types must match the reader and writer.
- Frozen samples in
  [`SaveSamples`](../../engine/tests/Talewright.Runtime.Tests/SaveSamples) must keep loading.
  Regenerate them only when deliberately adding a format version (`TALEWRIGHT_REGENERATE_SAVE_SAMPLES=1`).
- Google.Protobuf must read what we write, and we must read what Google writes, including unknown
  fields of every wire type. Google.Protobuf is a test-only dependency.

## Limits

| Limit | Value | Enforced |
|---|---|---|
| Compressed save | 2 MiB | Writing, storage, import and reading |
| Decoded body | 8 MiB (declared length checked first; decompression stops one byte past it) | Reading |
| Choices | 10,000 | Playing and reading |
| Choice key | 1–512 characters | Saving and reading |
| Character stats | 1,000 | Reading |
| UTC offset in summary | ±14 hours | Reading (an invalid summary is dropped, not fatal) |

No allocation is sized from an untrusted length. A RealisticStory playthrough of about 200 choices
is around 400 bytes; the size test fails above 4 KiB.

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
versions, so tests compare decoded saves, not compressed bytes. The browser tests load host-written
saves in WASM and decode WASM-written exports on the host.

## Conformance audit against Google.Protobuf

Compared with Google.Protobuf C# 3.36.2 (`ParsingPrimitives.cs`, `ParsingPrimitivesMessages.cs`,
`WritingPrimitives.cs` and `CodedInputStream.cs` at tag `v36.2`) and the encoding spec. Our reader is
deliberately stricter wherever Google accepts data a valid writer never produces.

| Area | Google.Protobuf C# | Talewright | Match | Test |
|---|---|---|---|---|
| Varint length | Up to 10 bytes; an 11th continuation byte is `MalformedVarint` | Same | Yes | `Malformed_bodies_are_rejected`, `Google_also_rejects…` |
| Overflowing 10th byte | Ignores bits beyond 64 | Rejects a 10th byte above `1` | Stricter | `Malformed_bodies_are_rejected` |
| Overlong (padded) varints within 10 bytes | Accepted | Accepted | Yes | `Our_reader_matches_google_for_every_valid_scalar_encoding` |
| Negative `int32` | Sign-extended to 10 bytes | No `int32` fields; unsigned fields are `uint32`/`uint64`, signed fields are zigzag | N/A | Schema test |
| `uint32` value above 2³²−1 | Truncates to the low 32 bits | Rejects | Stricter | `Malformed_bodies_are_rejected` |
| Zigzag (`sint32`/`sint64`) | `(n << 1) ^ (n >> 31/63)` | Same encoding, written from the spec | Yes, byte for byte | `Our_written_scalars_match_google_byte_for_byte` |
| Field 0 | `InvalidTag` | Rejects | Yes | `Malformed_bodies_are_rejected`, `Google_also_rejects…` |
| Field above 2²⁹−1 | Tag read as a 32-bit varint, so higher bits are discarded | Rejects field numbers above 2²⁹−1 | Stricter | `Malformed_bodies_are_rejected` |
| Known field with wrong wire type | Treated as unknown and skipped | Skipped | Yes | `Our_reader_decodes_a_google_written_body…` |
| Unknown varint, fixed32, fixed64, length-delimited | Skipped | Skipped | Yes | `Our_reader_decodes_a_google_written_body…` |
| Groups (wire types 3 and 4) | Start-group skipped (recursion limit 100); bare end-group throws | Both rejected | Stricter | `Malformed_bodies_are_rejected` |
| Wire types 6 and 7 | `SkipLastField` has no case for them: nothing is consumed and parsing continues | Rejected | Stricter (deliberate) | `Malformed_bodies_are_rejected` |
| Length beyond remaining bytes | `TruncatedMessage` | Rejects | Yes | `Malformed_bodies_are_rejected`, `Google_also_rejects…` |
| Negative or overflowing length | Read as 32-bit `int`; negative is `NegativeSize` | Read as 64-bit and compared with remaining bytes | Yes (both reject) | `Malformed_bodies_are_rejected` |
| Invalid UTF-8 on read | `UTF8Encoding(throwOnInvalidBytes: true)`: `InvalidUtf8` | Same strict decoder; rejects | Yes | `Malformed_bodies_are_rejected` |
| Lone surrogates on write | Replaced with U+FFFD | Same | Yes, byte for byte | `Our_written_scalars_match_google_byte_for_byte` |
| Packed and unpacked repeated | Both accepted | Both accepted for `choices` | Yes | `Our_reader_decodes_a_google_written_body…` |
| Repeated scalar field | Last value wins | Last value wins | Yes | `Our_reader_decodes_a_google_written_body…` |
| Repeated embedded message | Merged | Merged (`character`, `summary`) | Yes | `Our_reader_decodes_a_google_written_body…` |
| Field order | Any order | Any order; indexes are resolved after the whole body is read | Yes | Randomized and Google-written bodies |
| Missing fields | Defaults (empty, zero) | Defaults; `story_id` is required | Stricter for `story_id` | `Missing_fields_read_as_defaults` |
| Size limit | `int.MaxValue` | 2 MiB compressed, 8 MiB decoded | Stricter | `Container_damage…`, `Decompression_bombs…` |
| Recursion limit | 100 | Fixed depth of 2 (Save → Character → Stat); no recursion | Stricter | Schema |

Robustness tests also cover every truncation and every single-bit flip, 20,000 random and mutated
bodies, and 500 randomized round trips with edge values and unusual Unicode. In every case the
reader either rejects the input with `InvalidDataException` or returns a valid save.

**Provenance.** `ProtoWire.cs` and `SaveFormat.cs` were written from the published encoding
specification. No Google code was copied; Google's source was read only to compare edge-case
behavior for this audit. Google.Protobuf (BSD 3-clause) is referenced only by
`Talewright.Runtime.Tests` and is not shipped in any package or player.
