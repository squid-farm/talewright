# Distribution and runtime safety

All stories are untrusted input. Talewright is an authoring framework and player,
not a content-review or certification service. A successful compile, publication
or play session does not establish that a game is harmless, authentic or suitable
for a particular reader.

This policy describes the framework **as provided**, not a new sandbox or a claim
that every current implementation meets every responsibility below. The
[implementation comparison](#implementation-comparison-and-follow-up-work) records
known limitations. Defects in Talewright remain Talewright's responsibility.

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
territory. This policy does not design a plugin or mod system.

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

## Implementation comparison and follow-up work

This comparison is against main `5a01392`, after #178, #196 and #260. It records
existing contracts and gaps; it does not authorize safeguard changes. Follow-up
candidates below need their own scope and acceptance evidence before implementation.

| Operation and evidence | Current behavior | Limitation or focused follow-up candidate |
|---|---|---|
| [Folder and ZIP source loading](../../engine/src/Talewright.Talemark/Compilation/StorySource.cs) | Folders reject encountered symbolic links and nesting beyond 64 levels, without authored-size ceilings. ZIPs validate raw member paths before root selection; text loading enforces 1,000 source files, 5 MiB per file and 50 MiB total through actual reads. | ZIP text limits do not bound archive metadata, unrelated members, image bytes, parser work or decoded memory. Evaluate metadata/discovery work separately, without reintroducing a total-game budget. |
| [Browser upload and story adoption](../../player/src/Talewright.Player/Components/TalewrightApp.razor) | The default 20 MiB compressed upload limit precedes buffering into a seekable stream. Failed candidates are disposed; successful compilation replaces the accepted story. | The host can configure the upload limit. It is not a decompression or memory guarantee. Source loading and compilation are synchronous and do not accept a cancellation token; scope cancellable adoption work separately. |
| [Retained ZIP images](../../player/src/Talewright.Player/Library/ArchiveStoryImages.cs) and [regressions](../../player/tests/Talewright.Player.E2E.Tests/ArchiveImageTests.cs) | Image entries are opened lazily; reads are serialized in at most 64 KiB chunks, check cancellation and reject actual bytes beyond the declared entry length. Owners release archive/input resources. | A legitimately large declaration is accepted. There is no combined text/image budget, decoded-pixel budget or total-memory guarantee. Define decoder/transfer failure expectations and adversarial image tests before choosing any new resource restriction. |
| [Folder image access and shared leases](../../player/src/Talewright.Player/Library/StoryImages.cs) | Access is catalogue-based, checks path components for links, shares transfers between occurrences and cancels/releases resources when leases retire. | Attribute checks followed by file opens are not an atomic filesystem boundary against concurrent replacement. Clarify the supported concurrent-mutation model and test any future hardening without claiming a filesystem sandbox. |
| [Published HTTP stories](../../player/src/Talewright.Player/Library/PublishedStoryLibrary.cs) | Index paths and hash formats are validated, content URLs are segment-escaped, text is fetched before compilation and image requests are lazy. Failures are surfaced. | HTTP responses and images have no Talewright-wide byte or decode budget. Hashes are used in URLs, not verified against response bytes. Do not describe cache versioning as integrity verification; any integrity feature needs a separately defined purpose and trust model. |
| [Publication](../../tooling/cli/src/Talewright.Cli/PublishCommand.cs) and [regressions](../../tooling/cli/tests/Talewright.Cli.Tests/PublishTests.cs) | Output is staged before replacement, with explicit archive/build failures and split-output rollback. There are no generated-output size ceilings. Interactive refusal cancels publication. | The command receives but does not propagate its cancellation token into build/archive work. Add cooperative cancellation and tests proving good output survives interruption. Split replacement is not a crash-atomic transaction; do not promise recovery from arbitrary process termination. |
| [Saves](../../engine/src/Talewright.Runtime/StoryHistory.cs) and [desktop save storage](../../engine/src/Talewright.Runtime/Saves/FileSaveStore.cs) | Save import/replay has separate byte/command validation; desktop save names are encoded within a selected root and writes use a same-directory temporary file before replacement. | Save/replay safeguards are not story-size limits or protection for arbitrary host code. Preserve their independent tests when changing persistence. |
| [Trigger evaluation](../../engine/src/Talewright.Rules/Triggers/TriggerEngine.cs) | Cascades stop with an explicit error if still eligible after 32 passes. | This bounds cascade passes, not the cost of each predicate, all runtime work or custom C# execution. No host-process isolation is supplied. |

For #178, distinguish compressed storage, expanded text, streamed image bytes and
decoder allocations; retain path validation, disposal and visible deferred failure.
For #196, removing authored-folder ceilings must not be described as trusting
folders, and retained ZIP text bounds must not be called an archive-wide budget.
For #260, publication freedom does not change player import rules or host limits.

The smallest follow-up slices are cancellable source/adoption operations,
cancellable publication with output-preservation tests, archive-discovery resource
handling, and image transfer/decode resource handling. Each should establish
measurable operation-specific failure behavior first. No thresholds, new framework,
reader override UI or blanket removal of existing defenses is approved here.
