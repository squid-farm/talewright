# UI test container image

The browser and Linux Photino UI workflows run in a prebuilt public image:

- `ghcr.io/squid-farm/talewright-ui-tests:pw1.63.0-r1`
- Definition: [.github/docker/ui-tests/](../../.github/docker/ui-tests/)

The image includes:

- Playwright .NET browsers and their Linux dependencies (Chromium, Firefox, WebKit)
- Native Linux UI dependencies used by `tests/run_e2e.py linux` (WebKitGTK, AT-SPI, Xvfb, D-Bus, Xfwm4)
- Node 22/npm for Nx orchestration compatibility
- Python 3 with `python3-gi` and `python3-pyatspi`

It is a dependency environment only. The repository checkout is still built and tested from source in each run.

## Compatibility contract

`python3 .github/docker/ui-tests/verify.py` is run inside containerized UI jobs and fails fast when:

- the checked-out `Microsoft.Playwright` package version differs from image metadata,
- the checked-out `global.json` SDK request differs from image metadata,
- the job is running as root, or
- Python GI/AT-SPI bindings are unavailable.

When dependency versions change, publish a new image first, then update workflow image references.

## Publishing and rollout

Workflow: [.github/workflows/publish-ui-test-image.yml](../../.github/workflows/publish-ui-test-image.yml)

The tag in [image.json](../../.github/docker/ui-tests/image.json) is the source of truth, and the
image is published only when it changes:

- If that tag is not in GHCR yet, the workflow builds and verifies the candidate. It publishes
  automatically on `push` to `main`; pull requests only validate.
- If the tag already exists and the image definition is unchanged, nothing is built or published.
- If the image definition changed but the tag already exists, the workflow fails and asks for a
  revision bump. Pull requests get this check before merge.
- `workflow_dispatch` on `main` publishes the current tag only if it is missing (for example, to retry
  a failed publish). Existing tags are never overwritten.

Suggested rollout:

1. Update the image definition and bump the tag in `image.json` (`pw<playwright>-r<revision>`).
2. Merge; the push to `main` publishes the new tag.
3. Copy the published immutable digest from the workflow summary.
4. Update consuming workflow references.
5. Verify browser and native jobs on a PR.

Do not publish PR code, do not use mutable `latest`, and do not replace an existing version tag with different content.

After the first publication, set the package visibility to public and verify an anonymous pull before
enabling consumer jobs. A private or missing package prevents those jobs from starting, including fork PRs.
Rebuild under a new revision for distro security updates; installed WebKitGTK packages do not update
themselves inside an existing image.

The initial `pw1.63.0-r1` image has been published and its anonymous manifest access verified.
Its digest is `sha256:d636d1fc9c49ff2eaa65341c1056738b35a2b4dac8e455df2738b8c9da091469`.

## Nx handoff

This change incorporates the `a533824` handoff and subsequent `394d6c6` update (including the `ddee903`
main/schema integration) from `feature/123-nx-orchestration`. Containerized jobs
retain its affected-job selection, Nx runner commands, artifact paths, and standalone coverage.
The image provides Node 22/npm for those commands; it does not replace Nx. `.github/**` is already a
shared Nx input, so image definitions and workflow reference changes select broad verification.

Non-root jobs need a writable checkout (including `.nx/installation`), home directory, npm cache,
and NuGet cache. GitHub-hosted Ubuntu runners and the image user both use UID/GID 1001.
When reproducing with a local bind mount, run Docker with the checkout owner's UID/GID and supply a
writable `HOME`; do not make the checkout globally writable or run native tests as root.

## Local validation

The following is historical local rollout evidence, not the current hosted CI status.

The candidate image built successfully. A non-root Chromium run passed 86 tests with one intentionally
skipped standalone case. The native accessibility/chooser probe passed with Docker's default security
profiles and without disabling WebKit sandboxing. One earlier probe attempt failed with an AT-SPI
reply timeout; subsequent probe runs passed. Full Firefox/WebKit, native regression/standalone, and
GitHub Actions validation remain rollout gates; no net CI speedup is claimed before measuring pulls.

After integrating Nx, the native `e2e-linux-probe` target also passed as non-root inside the published
image using rootless Podman (the local Docker service was stopped). This verifies the Nx target,
dependency presence, and writable runtime paths; it does not replace GitHub Actions job-container
validation or the earlier Docker-default-profile probe.

The merged Nx browser adapter executed the full Chromium regression suite and correctly failed on
`Overflow_keyboard_modal_and_themed_background_work(dark: False)` at the toolbar assertion (85 passed,
one failed, one standalone skip). A focused rerun of story-loading and toolbar cases passed all 14
cases in Firefox and WebKit; Chromium passed the 12 story-loading cases but failed both toolbar
variants. Both toolbar variants still failed in that local environment after the `394d6c6` update,
at assertion line 155. Those local failures were not counted as successful validation.

## Hosted validation and included scope

The image rollout from #129 was merged into the Nx branch and is included in #132, not a separate
pending rollout. This includes the GHCR publisher workflow and container provisioning for both regular
and performance browser/native jobs. Nx commands, affected gates, artifact handling, and aggregate
verification remain in place.

[Build run 39897624656](https://github.com/squid-farm/talewright/actions/runs/39897624656)
at `1b5bc32` passed all three containerized browser matrix jobs (regular regressions and published-web
smoke), the containerized Linux desktop job (probe, regular regressions, and published-desktop smoke),
editor verification, unit tests, package verification, checks, and both verification aggregates.
This satisfies the regular hosted container rollout gates despite the earlier local toolbar failures.
The separate real-file performance workflow has not been executed as part of this evidence; its
container changes still need their scheduled/manual performance run. No measured CI speedup is claimed.
