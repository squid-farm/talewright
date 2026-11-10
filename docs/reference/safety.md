# Distribution and runtime safety

All stories are untrusted input. Talewright is an authoring framework and player,
not a content-review or certification service. A successful compile, publication
or play session does not establish that a game is harmless, authentic or suitable
for a particular reader.

This policy describes the framework **as provided**. It is not a sandbox, and
not every part of Talewright meets every responsibility below yet; the
[current safeguards and known limits](#current-safeguards-and-known-limits) section
says where. Defects in Talewright remain Talewright's responsibility.

## What are you opening?

| Action | Boundary |
|---|---|
| Open story data in an existing player | Talewright parses text, evaluates supported story operations and decodes supported images. Loading a story ZIP is not an instruction to install or execute code from that ZIP. |
| Visit an author's standalone web game | You run the website's program, not just its story data. The browser enforces its origin, permission and process protections; these are not Talewright guarantees. |
| Run an author's desktop game or launcher | You run executable code with the permissions allowed by your OS and account. A Talewright-looking UI does not authenticate that executable. |
| Build a project, register C# extensions or use a customized host | Build tasks, dependencies, extensions and host code are executable software. Talewright does not isolate them from the host process. |

Talemark data and image references do not provide a general-purpose plugin
installation mechanism. Registered C# predicates, effects, rulesets, story
libraries, JavaScript and replacement frontends cross into executable-extension
territory. Talewright has no plugin or mod system.

## Responsibilities

**Talewright maintains defensive handling of what it processes:**

- Keep story references and framework-controlled writes within their intended
  filesystem or URL boundaries. Reject invalid paths rather than silently
  remapping them.
- Validate input structure and enforce operation-specific parsing, decompression,
  replay and execution safeguards. Account for actual reads where expansion is
  the risk; archive declarations alone are not a general resource guarantee.
- Manage owned streams, image transfers and temporary output lifetimes. Support
  cancellation where operations expose it, and report actionable failures.
- Reject failed candidates without replacing an accepted story or good published
  output with a partial result. A deferred image failure should remain visible
  and accessible without unnecessarily preventing continued play.
- Test those contracts and address defects. Asking a player to trust an author
  does not excuse a framework path-handling or parser defect.

**Authors and distributors** choose the host, executable code, dependencies and
distribution channel. They should test their complete published game on intended
devices, provide useful image descriptions, size artwork sensibly, explain
installation requirements and preserve deployment files. They are responsible
for their content and customizations; Talewright does not verify their releases.

**Players** decide whether to visit a site, run a downloaded executable, grant
permissions or load data from a distributor. Use browser and OS protections and
keep software updated. These choices do not certify the input or relieve
Talewright of its own defensive-processing responsibilities.

**Developers customizing Talewright** must reassess the boundaries they change.
Extensions execute in the host's context and can introduce capabilities or bypass
checks. Framework claims do not extend to modifications Talewright does not
control, or guarantee the safety of the resulting game.

## Guarantees, safeguards and non-guarantees

Here, a **guarantee** means an intended, testable framework contract, not a
statement that bugs are impossible. Path validation, explicit rejection and
staged output replacement are examples. A **best-effort safeguard** reduces a
specific risk but does not establish a safe game: lazy image reads, cancellation,
bounded text expansion and trigger-pass limits each address different operations.

There is **no maximum safe game size**. A small input can require expensive
parsing, decoding or evaluation; a large game can be usable when work is deferred.
Restrictions must identify the operation, failure mode, enforcement point and
failure behavior they protect. A compressed upload limit, expanded-text limit,
declared image size and decoded pixel allocation are different measurements.

Folder authoring and generated publication have no Talewright file-count or byte
ceiling. This does not promise that arbitrary games fit memory, disk, browser
storage, device capabilities or external hosting limits. Hosting guidance is
advisory, not certification or a refusal to publish.

Talewright does not promise content moderation, an official/verified-content
channel, malware detection, authenticated distributors, a new OS/browser sandbox,
bounded total CPU or memory, or safety of developer-customized programs.
Published content hashes are cache-version identifiers, not signatures or proof
that a distributor is trusted.

## Current safeguards and known limits

Each row links to the code that implements the operation, and to its tests where they exist.

| Operation and evidence | Current behavior | Known limits |
|---|---|---|
| [Folder and ZIP source loading](../../engine/src/Talewright.Talemark/Compilation/StorySource.cs) | Folders reject encountered symbolic links and nesting beyond 64 levels, without authored-size ceilings. ZIPs validate raw member paths before root selection; text loading enforces 1,000 source files, 5 MiB per file and 50 MiB total through actual reads. | ZIP text limits do not bound archive metadata, unrelated members, image bytes, parser work or decoded memory. |
| [Browser upload and story adoption](../../player/src/Talewright.Player/Components/TalewrightApp.razor) | The default 20 MiB compressed upload limit precedes buffering into a seekable stream. Failed candidates are disposed; successful compilation replaces the accepted story. | The host can configure the upload limit. It is not a decompression or memory guarantee. Source loading and compilation are synchronous and cannot be cancelled. |
| [Retained ZIP images](../../player/src/Talewright.Player/Library/ArchiveStoryImages.cs) and [regressions](../../player/tests/Talewright.Player.E2E.Tests/ArchiveImageTests.cs) | Image entries are opened lazily; reads are serialized in at most 64 KiB chunks, check cancellation and reject actual bytes beyond the declared entry length. Owners release archive/input resources. | A legitimately large declaration is accepted. There is no combined text/image budget, decoded-pixel budget or total-memory guarantee. |
| [Folder image access and shared leases](../../player/src/Talewright.Player/Library/StoryImages.cs) | Access is catalogue-based, checks path components for links, shares transfers between occurrences and cancels/releases resources when leases retire. | Attribute checks followed by file opens are not an atomic filesystem boundary against concurrent replacement, and this is not a filesystem sandbox. |
| [Published HTTP stories](../../player/src/Talewright.Player/Library/PublishedStoryLibrary.cs) | Index paths and hash formats are validated, content URLs are segment-escaped, text is fetched before compilation and image requests are lazy. Failures are surfaced. | HTTP responses and images have no Talewright-wide byte or decode budget. Hashes are used in URLs, not verified against response bytes: cache versioning is not integrity verification. |
| [Publication](../../tooling/cli/src/Talewright.Cli/PublishCommand.cs) and [regressions](../../tooling/cli/tests/Talewright.Cli.Tests/PublishTests.cs) | Output is staged before replacement, with explicit archive/build failures and split-output rollback. There are no generated-output size ceilings. Interactive refusal cancels publication. | The command does not pass its cancellation token into build/archive work, so those steps cannot be cancelled cooperatively. Split replacement is not a crash-atomic transaction; there is no recovery guarantee after arbitrary process termination. |
| [Saves](../../engine/src/Talewright.Runtime/StoryHistory.cs) and [desktop save storage](../../engine/src/Talewright.Runtime/Saves/FileSaveStore.cs) | Save import/replay has separate byte/command validation; desktop save names are encoded within a selected root and writes use a same-directory temporary file before replacement. | Save/replay safeguards are not story-size limits or protection for arbitrary host code. |
| [Trigger evaluation](../../engine/src/Talewright.Rules/Triggers/TriggerEngine.cs) | Cascades stop with an explicit error if still eligible after 32 passes. | This bounds cascade passes, not the cost of each predicate, all runtime work or custom C# execution. No host-process isolation is supplied. |

When changing these safeguards, keep the measurements separate: compressed storage, expanded
text, streamed image bytes and decoder allocations are different limits. Removing authored-folder
ceilings does not mean folders are trusted, ZIP text bounds are not an archive-wide budget, and
publishing large games does not change the player's import rules or host limits. Any new restriction
should name the operation it protects and its failure behavior before a threshold is chosen.
