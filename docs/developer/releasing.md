# Releasing and CI

## Workflows

**Build** runs on PRs to `main`, pushes to `main`, version tags and manual runs. It builds Release,
checks the libraries and the CLI/language-server tools from clean, isolated consumers, and calls the reusable
**Tests** workflow (unit tests, Chromium, Firefox, WebKit and real Linux Photino). Publishing happens
only after both pass:

| Trigger | What happens |
|---|---|
| Push to `main` | **Publish Samples** builds the web player, checks that a sample opens and a choice works at the Pages base path, then deploys to [GitHub Pages](https://squid-farm.github.io/talewright/). |
| Version tag `v*` | **Publish NuGet** pushes the already-verified packages to GitHub Packages with the run's `GITHUB_TOKEN`. |
| PR / manual | Validation only. |

The browser and Linux Photino jobs in **Tests** and **Real-file performance** run inside the versioned
public GHCR UI image (`ghcr.io/squid-farm/talewright-ui-tests:*`). Publish/update procedure is documented in
[UI test container image](ui-test-image.md).

## Repository settings

1. **Settings > Pages > Source:** GitHub Actions, and allow `main` in the `github-pages` environment's
   deployment rules. A custom domain is optional; the workflow reads the base path from Pages.
2. Let the publishing jobs use their scoped Pages/OIDC and `packages: write` permissions. No PAT secret
   is needed. After the first publication, check package visibility and repository/Actions access;
   publishing doesn't set those for you.
3. For the UI test image package, keep GHCR visibility public so fork PRs can pull it without credentials.
   The first publish may default to private, so confirm and adjust package visibility explicitly.
4. After confirming the emitted context on a PR, require **CI verification** rather than conditional
   leaf jobs. The Nx-aware aggregate rejects failures, cancellations, and unexpected skips while
   permitting legitimately unselected jobs. See [Nx CI orchestration](nx-ci.md).

## Cutting a release

Tag a validated commit:

```bash
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

- Tags are `vMAJOR.MINOR.PATCH` with optional SemVer prerelease identifiers. Leading zeros in numeric
  identifiers and build metadata (`+...`) are rejected.
- The tag sets the version of every package. Ordinary CI builds use an unpublished
  `0.0.0-ci.RUN_NUMBER`.
- Publication is not atomic: `Talewright` is pushed first, then `Talewright.Player`,
  `Talewright.Cli`, and `Talewright.LanguageServer`. The separate language-server
  tool installs `talewright-lsp`; it is never a dependency of shipped games.
  After a transient failure, rerun the failed run. Existing versions are skipped (not overwritten or
  compared) and missing ones are pushed. Other failures (authentication, network, validation) still fail.
- **Never move or reuse a release tag.** Fix mistakes with a new version.
- Version-tag runs are not cancelled by newer CI runs.
