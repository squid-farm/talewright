# Releasing and CI

## Workflows

**Build** runs on PRs to `main`, pushes to `main`, version tags and manual runs. It builds Release,
checks the libraries and the CLI/language-server tools from clean, isolated consumers, and calls the reusable
**Tests** workflow (unit tests, Chromium, Firefox, WebKit, real Linux Photino and editor/package
checks). The tag uses the same full Nx verification as `main`, not a separate release build.
Publishing consumes that run's `nuget-packages` and `talewright-vscode` artifacts only after
**CI verification** and the release artifact gate succeed:

| Trigger | What happens |
|---|---|
| Push to `main` | Full verification only; the hosted player is not deployed. |
| Valid version-tag push | After verification, **Publish Samples** builds that tagged web player, checks that a sample opens and a choice works at the site root, then deploys to [GitHub Pages](https://play.trytalewright.com/). The root represents the most recently deployed eligible tag, including previews. **Publish NuGet** pushes four packages to nuget.org using OIDC; the Marketplace job publishes all six inspected platform VSIXs; then the GitHub release job attaches them and publishes generated notes. Linux x64 is runtime-tested; the other five platforms are provisional. Package publishing jobs use environment `release`. |
| PR / manual | Validation only. |

## Preview compatibility notice

Use this text in 1.0 preview release notes and tester announcements. The first paragraph matches the
notice that `.github/scripts/github_release.py` adds to generated release notes; keep them in step.
The compatibility promise starts with 1.0.0-preview.1, which has not shipped yet. The project lead
decides when to publish a release and when to contact testers.

> **1.0 preview: what stays stable.** From 1.0.0-preview.1, Talemark syntax, story files
> and saves made by a preview will keep working in later 1.x releases. We will break this only to fix
> a severe defect, such as a security or data-loss bug, and will announce it in advance, with an error
> that names the replacement and migration notes. Tooling, debugger scenario files and C# extension
> APIs may still change before 1.0.
>
> Editing your story can still make its existing saves unavailable; keep the story files that match
> each save. Autosave is on by default and keeps one rolling recovery save, not a backup. Please do
> not share private story content without permission.

The browser and Linux Photino jobs in **Tests** and **Real-file performance** run inside the versioned
public GHCR UI image (`ghcr.io/squid-farm/talewright-ui-tests:*`). See
[UI test container image](ui-test-image.md) for how to publish and update it.

## Repository settings

1. **Settings > Pages > Source:** GitHub Actions, and allow `v*` tags in the `github-pages`
   environment's deployment rules. The custom domain is `play.trytalewright.com`; the workflow
   publishes at the site root (`/`). Invalid public tags fail validation before deployment.
2. Keep the **release** environment's reviewer approval and `v*` tag deployment restriction.
   Store `VSCE_PAT` (Marketplace publisher `squid-farm`, **Marketplace > Manage** scope) and
   `NUGET_USER` (nuget.org username) as **environment secrets**, not repository secrets.
   The caller explicitly forwards only `NUGET_USER`, declared as a required secret in the reusable
   workflow. The called job's `environment: release` supplies its environment-secret value after
   approval. Without that explicit forwarding, the secret resolves to an empty string in the
   reusable workflow. Do not use blanket secret inheritance or add a long-lived NuGet API key.
   Approve the publishing deployments only after reviewing the validated tag/run.
3. Keep the UI test image package public on GHCR so fork PRs can pull it without credentials.
   A newly created package may default to private.
4. Make **CI verification** the required status check, not the conditional leaf jobs. The Nx-aware aggregate rejects failures, cancellations, and unexpected skips while
   permitting legitimately unselected jobs. See [Nx CI orchestration](nx-ci.md).
5. The nuget.org Trusted Publishing policy uses workflow filename **`publish-nuget.yml`** (filename
   only, not a path), repository owner `squid-farm`, repository `talewright`, and environment `release`.
   NuGet validates the OIDC **`job_workflow_ref`**, which identifies the **called reusable workflow**,
   not the top-level caller (`build.yml`). See the
   [NuGet claim validator](https://github.com/NuGet/NuGetGallery/blob/c25bab040e19d24593fc0c298a0cedb51bf57dcb/src/NuGetGallery.Services/Authentication/Federated/GitHubTokenPolicyValidator.cs)
   and [Trusted Publishing documentation](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).
   Ensure policy ownership/scopes permit **new packages and new versions** for `Talewright`,
   `Talewright.Player`, `Talewright.Cli`, and `Talewright.LanguageServer`. Authentication uses
   `NuGet/login@v1` to exchange OIDC for a one-hour key just before pushing.
6. Only the NuGet publishing job/caller grants `id-token: write`; only the GitHub release job grants
   `contents: write`. Marketplace needs only `contents: read` and the environment PAT.
   Pages and GHCR image permissions remain separate.

## All-in-one extension packages

The editor `package-check` target publishes a self-contained Linux x64 language
server into the VSIX before running package checks; `editor-smoke` installs **that VSIX** and
uses the default bundled executable. It checks diagnostics, completion, hover and navigation
with external `dotnet` blocked and runtime lookup disabled, plus a real restricted-workspace
pass proving that the server does not start. No story-project code is built or executed.

Tagged builds additionally cross-package `linux-arm64`, `win32-x64`, `win32-arm64`, `darwin-x64`
and `darwin-arm64` in the **provisional-vsix** matrix. Each package includes a self-contained
server for its actual .NET RID (`linux-*`, `win-*` or `osx-*`). Each runs the package-content
checks, including native PE/ELF/Mach-O architecture checks, but **not** installed-editor execution.
These five platforms are provisionally supported; the Marketplace README and generated release
notes explicitly disclose that they do not have Linux x64's runtime validation.

The release artifact gate requires exactly one package for each of the six targets before **any**
publication. It rejects universal/unsupported or mislabeled VSIXs, missing native/runtime assets,
non-executable servers, externally dependent runtime configurations, mismatched server/client
release versions, and non-bundled launch defaults before **any** publication. Changing only target
metadata cannot satisfy the gate. Publication does not rebuild packages and there is no universal fallback.
Supported environments and development overrides are documented in the
[extension README](../../tooling/vscode-talewright/README.md).

Automated offline validation can't check the live Marketplace. After publishing, check the
`squid-farm.talewright` listing, a clean install, platform filtering and updating from the previous
version by hand.

## Release identities

- NuGet accepts SemVer prerelease suffixes; VS Code Marketplace does **not** accept them in extension
  versions. Its channel is separate metadata set by `--pre-release`, not by a numeric version alone.
  See [VS Code prerelease publishing](https://code.visualstudio.com/api/working-with-extensions/publishing-extension#prerelease-extensions).
- Public tags support **`vMAJOR.MINOR.PATCH`** or **`vMAJOR.MINOR.PATCH-preview.N`**, where `N` is
  1 through 998. Other prerelease labels, leading zeros and build metadata are rejected for public
  publication, even though the general NuGet version helper accepts other SemVer labels.
- All four NuGet versions equal the tag without `v`.
- For the extension, reserve a block of 1,000 numeric patch versions for each NuGet patch:
  preview maps to `MAJOR.MINOR.(1000 * PATCH + N)`; stable maps to
  `MAJOR.MINOR.(1000 * PATCH + 999)`. Components must fit signed 32-bit integers.
  This is collision-free, previews increase, stable follows its previews, and the next patch's
  previews follow the prior stable release. Do not manually publish versions within reserved blocks.
  Choosing a later stable release can update prerelease users too, as documented by VS Code.

| Tag | NuGet | VSIX/Marketplace | Channel / GitHub |
|---|---|---|---|
| `v0.1.0-preview.1` | `0.1.0-preview.1` | `0.1.1` | prerelease |
| `v0.1.0-preview.2` | `0.1.0-preview.2` | `0.1.2` | prerelease |
| `v0.1.0` | `0.1.0` | `0.1.999` | stable |
| `v0.1.1-preview.1` | `0.1.1-preview.1` | `0.1.1001` | prerelease |

The editor's package target stamps the mapped version and prerelease flag **before**
package/installed-editor validation and artifact upload. It restores the source manifest afterward.
Publication never rebuilds or repackages the VSIXs. Marketplace publishing iterates all six
verified files with the same mapped version and channel, retaining `--skip-duplicate` so a retry
can resume after partial platform publication. GitHub Releases attaches Linux x64 as
`talewright-linux-x64.vsix` and the others as `talewright-TARGET.vsix`.
The original `talewright-vscode` CI artifact holds Linux x64; `talewright-vscode-TARGET`
artifacts hold provisional targets. The release gate, Marketplace job and GitHub release job
all download and verify the full set.
Main, PR and manual builds don't stamp a release version; their NuGet packages are unpublished
`0.0.0-ci.RUN_NUMBER` builds.

## Cutting a release

Tag the exact reviewed `main` commit. Replace `VERSION` with the new version, such as
`0.6.0-preview.1`:

```bash
git switch main
git pull --ff-only origin main
python3 .github/scripts/package_version.py --tag vVERSION --release-json
git tag vVERSION
git push origin vVERSION
gh run list --workflow build.yml --event push --limit 5
# Select the run for this tag:
gh run watch RUN_ID --exit-status
```

Approve the pending **release** deployments in GitHub; `gh run watch` waits while approval is pending.
Tag/manual reruns do not bypass the prerequisite or validation gates. A manual workflow dispatch
never publishes, even when run against a tag.

## Releases, retries and author downloads

- A rerun uses the original tag's workflow definitions, not fixes merged to `main` afterwards. If a
  workflow bug breaks a release, merge the fix and cut a new preview tag; don't move the old one.
- Publication is not atomic. NuGet pushes `Talewright`, `Talewright.Player`, `Talewright.Cli`, then
  `Talewright.LanguageServer`; Marketplace can run in parallel. The separate NuGet language-server
  tool is never a dependency of shipped games.
- After a transient error, use `gh run rerun RUN_ID --failed`. NuGet's `--skip-duplicate` handles
  already-published versions; pinned vsce 4.0.0's `--skip-duplicate` handles the exact extension
  version/target and HTTP 409 conflicts. Neither swallows authentication, network or validation
  failures. Existing NuGet/Marketplace versions are **not overwritten or content-compared**.
  Never publish differing artifacts under an existing version.
- The GitHub job runs only after both registries succeed. It verifies the tag still points at the
  validated commit, generates notes, creates a draft, uploads all six verified VSIXs, then publishes.
  Previews are marked prerelease and never selected as `latest`. Stable releases also do not
  automatically change `latest`; maintainers can promote one after review.
  Retries preserve existing notes and identical VSIX bytes, resume incomplete drafts, and fail if
  the release commit/channel or existing asset differs. Only an explicit HTTP 404 means “not found.”
- GitHub supplies versioned **Source code (zip)** and **Source code (tar.gz)** archives automatically.
  Use the [releases list](https://github.com/squid-farm/talewright/releases) to find previews:
  `/releases/latest` excludes prereleases and may be empty before a stable release.
  Generated notes include an author/source-install explanation and the
  [compatibility notice](#preview-compatibility-notice). A release containing an approved breaking
  change must add its migration notes.
- **Never move or reuse a release tag.** Fix mistakes with a new version. Version-tag runs are not
  cancelled by newer CI runs. If artifacts expire before a retry, rerun full validation on the
  unchanged tag; never substitute locally built artifacts.
- Version tags also build self-contained CLI/debugger archives for six RIDs. The release gate requires their build/asset checks and six SHA-256 files; the GitHub job
  attaches those archives/checksums alongside the VSIXs without rebuilding them after approval.
  See the [author setup guide](../guide/cli-installation.md) for download instructions and
  [technical CLI setup](cli-setup.md) for per-command prerequisites and verification limits.
  Cross-building Windows/macOS/ARM does not verify execution on those platforms.
- Player downloads, installers, package managers and Open VSX are not part of the release yet.

## Offline validation

The `talewright-ci-tests:ci-check` target includes `tests/test_release.py`, covering
version boundaries/order/collisions, the complete six-platform VSIX set, native architecture,
identity/channel/bundling, and release API retry/error cases without network publication. It also
parses the workflows to check publication safeguards: tag-only publication behind full verification,
the allowlist of write permissions, explicitly forwarded secrets bound to the `release` environment,
NuGet OIDC sign-in to nuget.org, and no `pull_request_target` triggers:

```bash
./nx run talewright-ci-tests:ci-check
cd tooling/vscode-talewright
npm ci
TALEWRIGHT_RELEASE_TAG=v0.5.0-preview.1 npm run package
TALEWRIGHT_RELEASE_TAG=v0.5.0-preview.1 npm run test:package
# Repeat package/test:package with TALEWRIGHT_VSIX_TARGET set to each provisional target.
# Once all six packages exist:
python3 ../../.github/scripts/verify_release_vsix.py artifacts v0.5.0-preview.1 --all
```

Run `xvfb-run -a npm run test:editor` after packaging to check the installed all-in-one client/server
and restricted workspace behavior. A passing client unit test alone is not proof of bundling.
