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

- `push` to `main` touching image files runs candidate validation only.
- `workflow_dispatch` on `main` with a versioned tag matching the image metadata publishes the
  validated candidate to GHCR. Existing tags are not overwritten.

Suggested rollout:

1. Update image definition and metadata. For the initial rollout, land the publishing workflow
   before enabling consumer references: this PR's new GHCR reference does not exist until publication.
2. Run the publish workflow manually with a new versioned tag (`pw<playwright>-r<revision>`).
3. Copy the published immutable digest from the workflow summary.
4. Update consuming workflow references.
5. Verify browser and native jobs on a PR.

Do not publish PR code, do not use mutable `latest`, and do not replace an existing version tag with different content.

After the first publication, set the package visibility to public and verify an anonymous pull before
enabling consumer jobs. A private or missing package prevents those jobs from starting, including fork PRs.
Rebuild under a new revision for distro security updates; installed WebKitGTK packages do not update
themselves inside an existing image.

## Nx handoff

This change is stacked on `feature/123-nx-orchestration`. Its current base contains the initial graph,
not the other agent's in-progress workflow orchestration changes. Before merging, incorporate that
branch's final commits and preserve its affected-job selection, Nx runner commands, artifact paths,
and standalone coverage. The image provides Node 22/npm for those commands; it does not replace Nx.

## Local validation

The candidate image built successfully. A non-root Chromium run passed 86 tests with one intentionally
skipped standalone case. The native accessibility/chooser probe passed with Docker's default security
profiles and without disabling WebKit sandboxing. One earlier probe attempt failed with an AT-SPI
reply timeout; subsequent probe runs passed. Full Firefox/WebKit, native regression/standalone, and
GitHub Actions validation remain rollout gates; no net CI speedup is claimed before measuring pulls.
