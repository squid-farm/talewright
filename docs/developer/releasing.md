# Releasing and CI

## Workflows

**Build** runs on PRs to `main`, pushes to `main`, version tags and manual runs. It builds Release,
checks the libraries and the CLI tool from clean, isolated consumer projects, and calls the reusable
**Tests** workflow (unit tests, Chromium, Firefox, WebKit and real Linux Photino). Publishing happens
only after both pass:

| Trigger | What happens |
|---|---|
| Push to `main` | **Publish Samples** builds the web player, checks that a sample opens and a choice works at the Pages base path, then deploys to [GitHub Pages](https://squid-farm.github.io/talewright/). |
| Version tag `v*` | **Publish NuGet** pushes the already-verified packages to GitHub Packages with the run's `GITHUB_TOKEN`. |
| PR / manual | Validation only. |

## Repository settings

1. **Settings > Pages > Source:** GitHub Actions, and allow `main` in the `github-pages` environment's
   deployment rules. A custom domain is optional; the workflow reads the base path from Pages.
2. Let the publishing jobs use their scoped Pages/OIDC and `packages: write` permissions. No PAT secret
   is needed. After the first publication, check package visibility and repository/Actions access;
   publishing doesn't set those for you.
3. Branch protection should require **Build** (it orchestrates the tests).

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
- Publication is not atomic: `Talewright` is pushed first, then `Talewright.Player` and `Talewright.Cli`.
  After a transient failure, rerun the failed run. Existing versions are skipped (not overwritten or
  compared) and missing ones are pushed. Other failures (authentication, network, validation) still fail.
- **Never move or reuse a release tag.** Fix mistakes with a new version.
- Version-tag runs are not cancelled by newer CI runs.
