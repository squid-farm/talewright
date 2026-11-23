# Save format

Player saves, Quick Save, autosave and exported backups all use one binary format, stored with the
`.twsave` extension. A save holds replay inputs, compact replay evidence, a continuation checkpoint
and a readable transcript. It does not store the event log. Restoring prefers replay into a fresh
session and can continue from the checkpoint after an incompatible update (see
[Architecture](architecture.md)).

Code and the generated MessagePack formatters:
[`SaveFormat.cs`](../../engine/src/Talewright.Runtime/Saves/SaveFormat.cs) and
[`SavePayload.cs`](../../engine/src/Talewright.Runtime/Saves/SavePayload.cs), plus
[`SaveContinuation.cs`](../../engine/src/Talewright.Runtime/Saves/SaveContinuation.cs) and
[`ContinuationCheckpoint.cs`](../../engine/src/Talewright.Runtime/Saves/ContinuationCheckpoint.cs).

## Layout

| Bytes | Content |
|---|---|
| 4 | Magic `TWSV` |
| 1 | Format version, written as `6`; the reader accepts `4`, `5` and `6` |
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
| 10 | Continuation | nil or `[current checkpoint, transcript, base checkpoint or nil, base transcript length]` |
| 11 | Talemark arithmetic version | `1` or `2`; required in format 6 |

Replay evidence and replay choice identities are either both present or both `nil`. The reader
accepts missing trailing fields. A base requires both evidence fields to be `nil`. Format 4 always
decodes with a `nil` continuation, including when it has unknown fields at position 10 or later.
Formats 4 and 5 predate the arithmetic-version field and are interpreted as version 1.

The character array is `[first name, last name, pronouns, preset, stats]`. Optional strings are
`nil`; `stats` is an array of `[id, value]` arrays, sorted by id before writing. The summary array
is `[scene title, UTC ticks, UTC offset minutes, character name]`. It is stored as a separate
MessagePack byte sequence so damaged display metadata can be discarded without rejecting replay
data.

The continuation's current checkpoint is taken immediately before the current passage's target is
entered. It is an array of:

1. target scene id, or `nil` for the ending;
2. scene current before entry, or `nil`;
3. characters;
4. faction standings;
5. flags;
6. number variables;
7. text variables;
8. scene visits;
9. fired triggers;
10. trigger edges;
11. known trigger ids;
12. known item ids;
13. pending rendered blocks.

A character is `[id, first name, last name, pronouns or nil, stats, items]`. Pronouns are
`[subject, object, possessive, possessive pronoun, reflexive, agreement]`. Stats, items, standings,
flags (0 or 1), number variables and visits use `[id, int32, exact or nil]` arrays; text variables use
`[id, string]`. For version-2 numeric variables, stats and item quantities, the exact field is a
canonical reduced integer or `numerator/denominator` string and the int32 field is zero.
Other values use the int32 field. Older `[id, int32]` pairs convert exactly. Every id list is sorted ordinally. Seed and character selection remain in the
top-level fields.

The transcript contains frames before the current passage, including earlier continuation segments.
The base transcript length says how many of those frames precede the optional base. A frame is
`[scene id or nil, title or nil, blocks, chosen inlines or nil, chosen automatically, ended,
continues previous, clears previous]`. A block is
`[kind, inlines or nil, speaker or nil, text or nil, paragraphs or nil, note kind]`; an inline is
`[kind, text or nil, style, media id or nil, accessibility]`.

The integer tokens are frozen in `TranscriptCodec`: block kinds are paragraph 0, blank line 1,
speech 2 and note 3; inline kinds are text 0, line break 1, blank line 2 and media 3. Note kinds
are check 0, change 1 and info 2. Text-style bits are emphasis 1, strong 2 and code 4 (0 is none);
media accessibility is descriptive 0 or decorative 1. Pronoun agreement is singular 0 or plural 1.
These are not CLR enum ordinals. The codec coverage test requires every rendered type and enum member
to have an encoding.

The runtime uses MessagePack-CSharp 3.1.12 with its compile-time source-generated formatters and
`MessagePackSecurity.UntrustedData`. No protobuf schema, handwritten wire codec, or runtime-generated
formatter is used. MessagePack-CSharp and its runtime dependencies are MIT-licensed.

The reader requires the decoded length to equal the declared length and the file to end with the
body's Adler-32 checksum. .NET's `ZLibStream` accepts a truncated stream or one missing its checksum,
so these checks are done explicitly.

Any other format version is reported as unsupported. Reading version 4 is compatibility support, not
save migration; a later save writes version 6.

## Array-key rules

Array positions are permanent. Never reorder or reuse a position; add new fields only at the end.
Readers skip unknown trailing positions, while new readers supply defaults for missing top-level
trailing positions. The version bump prevents a version-4 reader from silently replaying
base-relative choices from the story start. The MessagePack source generator and frozen layout tests
keep the annotated key positions and wire layout aligned.

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
| Compressed save | 512 MiB | Writing, storage, import and reading |
| Decoded body | 1 GiB (declared length checked first; decompression stops one byte past it) | Writing and reading |
| Choice key | 1–512 characters | Saving and reading |
| Summary UTC offset | ±14 hours | Reading (invalid details are dropped, not fatal) |
| Replay evidence | 33 bytes, version 4 | Reading; malformed or unsupported evidence rejects the save |
| Replay choice identity | 8 bytes, or 32 bytes for a colliding scene | Reading; count and encoding are checked before replay |

There is no limit on choices, transcript frames or character stats. A playthrough can run as long
as the author's story allows; long playthroughs cost time and memory in proportion to their length
instead of failing at a count.

Collection shapes are checked with `MessagePackReader` before the generated deserializer allocates
their lists. Arrays cannot declare more elements than the remaining bytes; maps are not allowed.
The base transcript length must be between zero and the frame count, and a base cannot carry replay
evidence. A checkpoint's total visit count cannot exceed its preceding transcript frames (the base
transcript length for a base checkpoint).

The byte limits reject damaged or hostile files, not long stories. They sit far above real saves:
the largest measured 10,000-choice save in the
[performance scorecard](performance-scorecard.md#save-size-and-continuation) is 65,365 compressed
bytes and 7,033,682 decoded bytes, so the decoded limit allows roughly 150 times that playthrough.
The decoded limit stays at 1 GiB because .NET arrays top out near 2 GiB; the compressed limit keeps
the trigger-save encoding within a 32-bit length. Browser storage quota can refuse a save well
below these limits; the player then reports a storage error and play continues.

## Compatibility

A save must have the same story id. Format 4 and format 5 saves with a `nil` continuation keep
the replay-only policy: an update requires replay evidence, per-choice outcome identities and the
same replay/extension identity. The runtime validates the full timeline, including redo, before
rebuilding the visible cursor. A save without evidence loads only against the exact story revision.
Loading never rewrites the original bytes or replaces an active session on failure.

Every normal format-5 or format-6 player save carries a continuation. For an unchanged story and
arithmetic version, a save without a
base replays from the start with evidence; a save with a base rebuilds it, enters its target once,
then replays the choices since that boundary without evidence. Failure rejects as damaged rather
than falling back. For a changed story without a base, compatible evidence replay is tried first.
It preserves undo and redo and re-renders History using the updated prose. If replay fails, or the
save already has a base, the runtime continues from the current checkpoint.

Arithmetic-version changes count as changes even when the source fingerprint is identical. A
checkpoint can continue into the new arithmetic using the normal boundary contract below. A
replay-only save cannot safely infer its old state from a fingerprint. The runtime overload accepting
the original compiled story can reconstruct a version-1 replay-only save only if its source
fingerprint, story version, choice identities and replay evidence validate; otherwise it rejects it.
The normal player does not obtain an old story automatically. This is a bounded compatibility path,
not a general migration policy; see issues #536 and #172.

### Continuing from a checkpoint

The checkpoint restores state from current declarations, then enters its target once using the
updated story. The current passage's entry effects, checks, prose and choices come from the update;
effects of the choice that led there were already applied and are not repeated. Pending output from
that choice is retained. Automatic diverts continue normally.

Earlier passages are saved rendered frames: History shows their original text and choice labels
without executing or re-rendering them. A continuation starts a new undo boundary at cursor zero;
Back cannot cross it and redo is discarded. `StoryHistory.Continuation` reports whether redo was
discarded and lists author-facing notes. It remains `null` for replay loads. A subsequent save uses
the normalized resumed checkpoint as its base and retains the earlier transcript; further incompatible
updates add boundaries in the same way. Restart starts a new playthrough without a base.

For declaration changes:

| Change | Continuation behavior |
|---|---|
| Character, stat, item, faction, flag, variable, scene or trigger added | Use the current initial value. An item known to the save but absent from its counts has count zero. |
| Declaration removed | Keep its value for later saves, report it in notes, but do not load it. If the same id and kind return, restore the retained value. |
| Number variable changed to text, or text to number | Reject, including when the old value was retained through an intermediate version. |
| Stat range narrowed | Clamp the saved value and add a note. |
| Fractional quantity or value changed to a whole-only declaration | Reject rather than round or truncate it. |
| Initial value, stat default, preset or creation defaults changed | Keep the saved values. |
| Saved character selection no longer validates | Reject with the character detail. |
| Target scene removed | Reject. Renames are removal plus addition; there is no mapping. |

Structural rejections tell the player to open the save with the story version that created it.
Visits to existing scenes are restored, removed-scene visits are retained, and entering the target
increments its visit count. Seeded rolls remain keyed by seed, check identity and visit count.
`RollCount` is not saved and counts only rolls after continuation; rebuilding does not invent rolls.

Fired once-triggers and edge memory are restored for known triggers. A newly declared trigger is
primed against the restored state, so the update alone does not fire it. A known trigger whose
condition changed keeps its old edge: if that edge was false and the condition is now true, it can
fire at the normal evaluation after entering the target. Rebuilding itself does not run triggers
or level changes.

`PlaythroughLength` is the base transcript length plus cursor. Progress identity hashes the save without its summary and reduces
the continuation to `[nil, nil, base, base transcript length]`. Thus historical rendered text and
the current checkpoint do not change the key, while different bases do. After continuation,
`RestoredProgressKey` identifies the original slot until play changes.

`ForkAuthor` rejects a history with a base because an Author Debugger timeline must replay from
the story start. The player-facing notice and host/storage changes are separate work in
[#759](https://github.com/squid-farm/talewright/issues/759); this runtime change does not add that UI.

Each choice identity is a SHA-256 prefix describing its scene and gameplay outcome: destination,
ordered effects, and skill-check definition and outcomes. The usual encoding is the first 8 bytes.
If different outcome descriptions in one scene collide on those 8 bytes, every choice in that scene
uses the full 32-byte hash. Choice labels, prose, source positions, availability requirements, and
other offered choices are excluded. When replaying, the runtime chooses the first current authored
choice with the same outcome identity, even if its availability requirement is now false. Live choice
keys and availability behavior are unchanged. Recorded hub selections match existing destinations
regardless of their current tag eligibility; a removed destination makes replay fail.

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

Prose, label, unrelated-choice, ordering, and availability-requirement changes can be replay-compatible.
Changed starting values or recorded gameplay outcomes are not replay-compatible; saves with a
continuation can instead resume under the rules above. This does not provide general state mappings
or save-format migration.

Replay-only saves without evidence load only against the exact same story revision. Reopening and
re-saving such a save is not an approved migration path. The current author-facing guidance is in
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
