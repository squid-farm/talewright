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
| Push to `main` | **Publish Samples** builds the web player, checks that a sample opens and a choice works at the site root, then deploys to [GitHub Pages](https://play.trytalewright.com/). |
| Valid version-tag push | **Publish NuGet** pushes four packages to nuget.org using OIDC; the Marketplace job publishes all six inspected platform VSIXs; then the GitHub release job attaches them and publishes generated notes. Linux x64 is runtime-tested; the other five platforms are provisional. All publishing jobs use environment `release`. |
| PR / manual | Validation only. |

The browser and Linux Photino jobs in **Tests** and **Real-file performance** run inside the versioned
public GHCR UI image (`ghcr.io/squid-farm/talewright-ui-tests:*`). Publish/update procedure is documented in
[UI test container image](ui-test-image.md).

## Repository settings

1. **Settings > Pages > Source:** GitHub Actions, and allow `main` in the `github-pages` environment's
   deployment rules. The custom domain is `play.trytalewright.com`; the workflow publishes at the site root (`/`).
2. Keep the **release** environment's reviewer approval and `v*` tag deployment restriction.
   Store `VSCE_PAT` (Marketplace publisher `squid-farm`, **Marketplace > Manage** scope) and
   `NUGET_USER` (nuget.org username) as **environment secrets**, not repository secrets.
   The caller explicitly forwards only `NUGET_USER`, declared as a required secret in the reusable
   workflow. The called job's `environment: release` supplies its environment-secret value after
   approval. Without that explicit forwarding, the secret resolves to an empty string in the
   reusable workflow. Do not use blanket secret inheritance or add a long-lived NuGet API key.
   Approve the publishing deployments only after reviewing the validated tag/run.
3. For the UI test image package, keep GHCR visibility public so fork PRs can pull it without credentials.
   The first publish may default to private, so confirm and adjust package visibility explicitly.
4. After confirming the emitted context on a PR, require **CI verification** rather than conditional
   leaf jobs. The Nx-aware aggregate rejects failures, cancellations, and unexpected skips while
   permitting legitimately unselected jobs. See [Nx CI orchestration](nx-ci.md).
5. **Account-side correction required:** change the nuget.org Trusted Publishing policy's workflow
   filename from **`build.yml` to `publish-nuget.yml`**, keeping repository owner `squid-farm`,
   repository `talewright`, and environment `release`. Enter the filename only, not a path.
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

The existing editor `package-check` target now publishes a self-contained Linux x64 language
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

After publication, verify the live `squid-farm.talewright` listing, clean supported-environment
installation, platform compatibility filtering and a subsequent version update before declaring
#234 complete. Automated offline validation does not establish a live listing or live update.

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

The editor's existing package target stamps the mapped version and prerelease flag **before**
package/installed-editor validation and artifact upload. It restores the source manifest afterward.
Publication never rebuilds or repackages the VSIXs. Marketplace publishing iterates all six
verified files with the same mapped version and channel, retaining `--skip-duplicate` so a retry
can resume after partial platform publication. GitHub Releases attaches Linux x64 as
`talewright-linux-x64.vsix` and the others as `talewright-TARGET.vsix`.
The original `talewright-vscode` CI artifact holds Linux x64; `talewright-vscode-TARGET`
artifacts hold provisional targets. The release gate, Marketplace job and GitHub release job
all download and verify the full set.
Ordinary main/PR/manual packaging is unchanged;
ordinary NuGet CI builds remain unpublished `0.0.0-ci.RUN_NUMBER`.

## Cutting the first preview

After this pipeline and its bundled-server delivery are reviewed/merged, and the NuGet policy is
corrected, tag the exact reviewed `main` commit:

```bash
git switch main
git pull --ff-only origin main
python3 .github/scripts/package_version.py --tag v0.1.0-preview.1 --release-json
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
gh run list --workflow build.yml --event push --limit 5
# Select the run for this tag:
gh run watch RUN_ID --exit-status
```

Approve the pending **release** deployments in GitHub; `gh run watch` waits while approval is pending.
Tag/manual reruns do not bypass the prerequisite or validation gates. A manual workflow dispatch
never publishes, even when run against a tag.

## Releases, retries and author downloads

- A rerun uses the original tag's workflow definitions, not fixes subsequently merged to `main`.
  The `v0.1.0-preview.1` run failed before NuGet authentication because its reusable-workflow call
  did not forward `NUGET_USER`; Marketplace publication succeeded independently. Rerunning that
  unchanged tag will not pick up the forwarding fix. After merging the correction, cut a new
  reviewed preview tag (for example `v0.1.0-preview.2`); do not move the already-published tag.
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
  Generated notes include an author/source-install explanation and a pre-1.0 save compatibility
  warning. Curate specific migration/save-breaking details in the release notes as needed.
- **Never move or reuse a release tag.** Fix mistakes with a new version. Version-tag runs are not
  cancelled by newer CI runs. If artifacts expire before a retry, rerun full validation on the
  unchanged tag; never substitute locally built artifacts.
- Version tags additionally build self-contained CLI/debugger archives for the six existing RIDs.
  The existing release gate requires their build/asset checks and six SHA-256 files; the GitHub job
  attaches those archives/checksums alongside the VSIXs without rebuilding them after approval.
  See the [author setup guide](../guide/cli-installation.md) for download instructions and
  [technical CLI setup](cli-setup.md) for per-command prerequisites and verification limits.
  Cross-building Windows/macOS/ARM does not verify execution on those platforms.
- Player release downloads, installers, package managers and Open VSX remain separate follow-ups.

## Offline validation

The existing `talewright-ci-tests:ci-check` target includes `tests/test_release.py`, covering
version boundaries/order/collisions, the complete six-platform VSIX set, native architecture,
identity/channel/bundling, validation gating, scoped
permissions, and release API retry/error cases without network publication:

```bash
./nx run talewright-ci-tests:ci-check
cd tooling/vscode-talewright
npm ci
TALEWRIGHT_RELEASE_TAG=v0.1.0-preview.1 npm run package
TALEWRIGHT_RELEASE_TAG=v0.1.0-preview.1 npm run test:package
# Repeat package/test:package with TALEWRIGHT_VSIX_TARGET set to each provisional target.
# Once all six packages exist:
python3 ../../.github/scripts/verify_release_vsix.py artifacts v0.1.0-preview.1 --all
```

Run `xvfb-run -a npm run test:editor` after packaging to check the installed all-in-one client/server
and restricted workspace behavior. A passing client unit test alone is not proof of bundling.
