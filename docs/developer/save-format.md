# Save format

Player saves, Quick Save, autosave and exported backups all use one binary format, stored with the
`.twsave` extension. A save holds replay inputs and compact replay evidence: story identity, seed,
character choices and the choice history. It never stores the event log or game state. Restoring
replays the choices into a fresh session (see [Architecture](architecture.md)).

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
| 8 | Replay evidence | nil or a 33-byte binary value: evidence version followed by a SHA-256 digest |

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
- A frozen v4 sample from before replay evidence must keep loading; its bytes are pinned separately
  from the current writer's extended array.
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
| Replay evidence | 33 bytes, version 1 | Reading; malformed evidence rejects the save |

Collection counts are checked with `MessagePackReader` before the generated deserializer allocates
their lists. Other decoded structures are bounded by the 8 MiB body limit. A repeated-choice
playthrough of about 200 choices compresses below 4 KiB.

## Compatibility

Released save support remains exact-revision only. Cross-revision loading is a prototype for
[#478](https://github.com/squid-farm/talewright/issues/478), not an approved supported update path.

### Proposed story-update contract

The behavior below describes the candidate implementation. Its fixtures are synthetic, not
preview-tester evidence. Before merging or advertising this path, representative tester cases must
be evaluated and the project lead must explicitly approve the exact path and legacy-save treatment
under [#171](https://github.com/squid-farm/talewright/issues/171). Neither requirement is established
by successful synthetic replay tests.

A save must have the same story id. With matching version and content fingerprint it can replay as
before, and any evidence present is verified. Across changed source or story versions, it additionally
needs version-1 replay evidence and the same engine replay/extension identity. The runtime validates
the character, replays the entire choice history including redo, and accepts only when the evidence
matches. It also checks that reconstructing the visible cursor reproduces the validated prefix and
offered choices. The old bytes are never rewritten by loading. A later save uses the current story
identity and fresh evidence.

The evidence is a SHA-256 chain over the start and each recorded choice. The start binds the story id,
replay/extension identity, seed, character selection and start events. Each choice binds its scene,
the keys and rendered labels of all offered choices, the selected key, and every resulting gameplay
event and field. A final digest also binds the cursor. Canonical, source-generated JSON feeds the hash;
the encoding and test vectors are pinned by `StoryCompatibilityTests`. Change the evidence version
when its canonical encoding or meaning changes. The evidence is a compatibility check, not an
authentication mechanism.

This candidate path is intentionally conservative about selected choices and their offered labels.
Prose edits can work, but changed choices, effects or rolls on the recorded timeline are rejected when their
evidence differs. It compares the played path, not a saved state snapshot: an updated world's unused
defaults can change the resumed state or future play even when the recorded events still match.
General mappings and format migration are not implemented.

The proposed legacy-save treatment keeps format-4 saves without evidence—including any
`1.0.0-preview.1` saves made without it—tied to their exact original story and compatible player.
Reopening them there with an evidence-aware
player and saving again adds evidence; that new save can then be checked against an update. Older
unsupported formats cannot use this path. Manually constructed stories without a replay/extension
identity remain exact-revision only.

The author-facing rules are in [Sharing your story](../guide/sharing-your-story.md) and
[Updating a story with existing saves](../guide/playing-and-saving.md#updating-a-story-with-existing-saves).

Player messages:
- An incompatible update: "Version X of Title has changed, and this save is no longer compatible…"
  when the version changed; otherwise "Title has changed…".
- A different story: "This save belongs to a different story…".
- Replay failure: a generic incompatible-or-damaged message, with no choice number.

Desktop and web saves are interchangeable. Deflate output may differ between platforms or .NET
versions, so tests compare decoded bodies, not compressed bytes. The browser tests load host-written
saves in WASM and decode WASM-written exports on the host.
