# UI test container image

The browser and Linux Photino UI workflows run in a prebuilt public image,
`ghcr.io/squid-farm/talewright-ui-tests`. Its definition is in
[.github/docker/ui-tests/](../../.github/docker/ui-tests), and the current tag is in
[image.json](../../.github/docker/ui-tests/image.json).

The image includes:

- Playwright .NET browsers and their Linux dependencies (Chromium, Firefox, WebKit)
- Native Linux UI dependencies used by `player/tests/run_e2e.py linux` (WebKitGTK, AT-SPI, Xvfb, D-Bus, Xfwm4)
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

To roll out a new image:

1. Update the image definition and bump the tag in `image.json` (`pw<playwright>-r<revision>`).
2. Merge; the push to `main` publishes the new tag.
3. Copy the published immutable digest from the workflow summary.
4. Update the image references in `tests.yml` and `performance.yml`.
5. Verify the browser and native jobs on a PR.

The consuming workflows can lag behind `image.json`: they keep using the last image they were
switched to until step 4. The verifier always runs from the checkout.

Don't publish PR code, don't use a mutable `latest` tag, and never replace an existing tag with
different content. The package must stay public; a private or missing package stops the UI jobs
from starting, including on fork PRs. Rebuild under a new revision to pick up distro security
updates; WebKitGTK inside an existing image never updates itself.

## Running as non-root with Nx

The image only provides dependencies (including Node 22/npm for Nx); it doesn't replace Nx. Jobs keep
their Nx affected-job selection, runner commands and artifact paths. Changes to the image definition
select the CI contracts plus this workflow's candidate build, browser story-loading tests and native
probe, not the whole product suite. Changes to the consuming workflow references select the groups
they drive; see the [CI-infrastructure matrix](nx-ci.md#ci-infrastructure-change-matrix).

Non-root jobs need a writable checkout (including `.nx/installation`), home directory, npm cache,
and NuGet cache. GitHub-hosted Ubuntu runners and the image user both use UID/GID 1001.
When reproducing with a local bind mount, run Docker with the checkout owner's UID/GID and supply a
writable `HOME`; do not make the checkout globally writable or run native tests as root.
