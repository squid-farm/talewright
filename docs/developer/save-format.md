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
| 9 | Replay choice identities | nil or an array of binary choice-outcome identities, one per choice |

Replay evidence and replay choice identities are either both present or both `nil`. The reader
accepts missing trailing fields, but saves without evidence can only be loaded by the exact same story revision.

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

Any other format version is reported as unsupported. There is no migration between format versions.

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
| Replay evidence | 33 bytes, version 4 | Reading; malformed or unsupported evidence rejects the save |
| Replay choice identity | 8 bytes, or 32 bytes for a colliding scene | Reading; count and encoding are checked before replay |

Collection counts are checked with `MessagePackReader` before the generated deserializer allocates
their lists. Other decoded structures are bounded by the 8 MiB body limit. A repeated-choice
playthrough of about 200 choices compresses below 4 KiB.

## Compatibility

A save must have the same story id. When the story version or content fingerprint changes, loading
also requires replay evidence, per-choice outcome identities, and the same replay/extension identity.
The runtime replays the complete timeline, including redo, and accepts the update only when the
starting state and full gameplay evidence match. It then rebuilds the visible cursor from the validated
timeline. Loading never rewrites the original bytes; a later save uses the current story identity.

Each choice identity is a SHA-256 prefix describing its scene and gameplay outcome: destination,
ordered effects, and skill-check definition and outcomes. The usual encoding is the first 8 bytes.
If different outcome descriptions in one scene collide on those 8 bytes, every choice in that scene
uses the full 32-byte hash. Choice labels, prose, source positions, availability requirements, and
other offered choices are excluded. When replaying, the runtime chooses the first current authored
choice with the same outcome identity, even if its availability requirement is now false. Live choice
keys and availability behavior are unchanged. Recorded hub selections match existing destinations
regardless of their current tag eligibility; removed destinations still reject the save.

`ChoiceReplayIdentity` owns the outcome description, a JSON array tagged `choice-replay-v1`. Its
operator, name-part and pronoun-form tokens are frozen in that class, not taken from enum member
names; effect and expression kinds use the encoder's own tokens or the current Talemark vocabulary
spellings. `ChoiceReplayIdentityEncodingTests` pins descriptions and identities for every built-in effect,
expression and operator, and fails when a new effect or expression type has no encoding. If one of
those vectors fails, the change altered saved identities: either keep the old token or deliberately
change the version tag. Changing an identity is a compatibility break under the
[compatibility freeze](../../CONTRIBUTING.md#compatibility-freeze).

Replay evidence is a versioned SHA-256 chain. Its start binds the story and replay/extension identity,
seed, character selection, initial ruleset and world state, and start events. Each decision binds its
scene, outcome identity, and every resulting gameplay event. The final evidence binds the complete
timeline digest and cursor. Full evidence is recomputed during restore; short choice identities are
never trusted alone. Version 4 records keyed skill-check identity and execution visit. This intentionally
changes seeded check results once; saves with earlier evidence versions do not load and are not migrated.
Evidence is a compatibility check, not an authentication mechanism.
`ReplayEvidence` owns the hash inputs. Event kinds, randomness modes and built-in rule types are
written as tokens frozen in that class, not taken from CLR names; event payloads use the
author-scenario serializer. Extension rule and modifier types have no frozen token, so renaming one
changes evidence. `ReplayEvidenceEncodingTests` pins the input bytes of every gameplay event kind and
of a representative initial state and rule configuration, plus complete-timeline evidence with
checks, triggers and redo; `StoryCompatibilityTests` pins two more evidence values. A failing vector
means saved evidence would change: keep the old encoding, or deliberately bump the evidence version
and record the compatibility decision. From 1.0.0-preview.1, saves made by a preview must keep loading:
changing evidence is a compatibility break under the
[compatibility freeze](../../CONTRIBUTING.md#compatibility-freeze), which needs a project-lead decision,
a version bump and migration notes. The
[golden save tests](../../engine/tests/Talewright.Runtime.Tests/SaveSamples/golden/README.md) load real saves to prove gameplay is unchanged.

Prose, label, unrelated-choice, ordering, and availability-requirement changes can be compatible.
Changed starting values or recorded gameplay outcomes are not. This checks a replay path, not a saved
state snapshot, and does not provide general state mappings or save-format migration.

Saves without evidence only load against the exact same story revision. Reopening and re-saving
such a save is not an approved migration path. Stories without a replay/extension identity remain
exact-revision only. The author-facing guidance is in
[Sharing your story](../guide/sharing-your-story.md) and
[Updating a story with existing saves](../guide/playing-and-saving.md#updating-a-story-with-existing-saves).

Player messages:
- An incompatible update: "Version X of Title has changed, and this save is no longer compatible…"
  when the version changed; otherwise "Title has changed…".
- A different story: "This save belongs to a different story…".
- Replay failure: a generic incompatible-or-damaged message, with no choice number.

Desktop and web saves are interchangeable. Deflate output may differ between platforms or .NET
versions, so tests compare decoded bodies, not compressed bytes. The browser tests load host-written
saves in WASM and decode WASM-written exports on the host.
