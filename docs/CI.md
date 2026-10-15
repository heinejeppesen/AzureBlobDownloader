# CI and releases (GitHub Actions)

Everything is in one workflow file: [`.github/workflows/build.yml`](../.github/workflows/build.yml).

## If you know Azure DevOps

| Azure DevOps | GitHub Actions | In this repo |
|---|---|---|
| Pipeline (`azure-pipelines.yml`) | Workflow (`.github/workflows/*.yml`) | `build.yml`, shown as **Build** under the *Actions* tab |
| Pipeline run | Workflow run | One per push, pull request, tag, manual start or weekly schedule |
| Stage / job / step | Job / step (no stages; `needs:` orders jobs) | `test` → `package` → `release` |
| Microsoft-hosted agent (`vmImage`) | GitHub-hosted runner (`runs-on`) | `windows-latest`, `macos-latest`, `ubuntu-latest` |
| Strategy matrix | `strategy.matrix` | Tests on 3 OS; packages for 4 targets |
| Trigger (`trigger:`, `pr:`, `schedules:`) | `on:` (`push`, `pull_request`, `schedule`, `workflow_dispatch`) | see the top of `build.yml` |
| Tasks (`DotNetCoreCLI@2`, …) | Actions (`uses: owner/name@version`) or plain `run:` commands | `actions/checkout`, `actions/setup-dotnet`, upload/download artifact, pinned to commit SHAs (see [Security updates](#security-updates)); the rest is `dotnet`/scripts |
| Pipeline artifacts | Artifacts (on the run page) | One per package file, kept 30 days |
| Releases / GitHub release task | GitHub Releases | Draft release created on `v*` tags |
| Variable groups / secret variables | Repository *Secrets and variables* | None needed yet (signing will need some) |
| Service connections | `permissions:` + the automatic `GITHUB_TOKEN`, or secrets | Read-only by default; only the release job may write |
| Dependabot (Azure Repos: extension) | Dependabot, built in | Security alerts and updates (repository settings) |

## Everyday use

- **See runs**: repository → **Actions** → **Build**. Click a run for its jobs, logs and **Artifacts** (bottom of the page).
- **Start a run by hand**: Actions → Build → **Run workflow** (top right of the run list) → choose the branch.
- **Re-run** a failed run: open it → **Re-run jobs** (all, or only the failed ones).
- **Get the packages of a run**: the run page → *Artifacts*, or `gh run download <run-id> -R heinejeppesen/AzureBlobDownloader`.
- **Notifications**: GitHub e-mails you when a run you started (or the scheduled run) fails. Settings → Notifications → *Actions*.

## What runs when

| Event | Tests (3 OS) | Packages (4 targets) | Draft release |
|---|---|---|---|
| Push to `main`, pull request, manual run | yes | yes | – |
| Tag `v1.2.3` | yes | yes, version 1.2.3 | yes, with `SHA256SUMS.txt` |
| Weekly schedule (Monday 05:17 UTC) | yes, as a security check | – | – |

Versions: tag builds use the tag (`v1.1.0` → `1.1.0`); all other builds are `<Version in Directory.Build.props>-ci.<run number>`.

## Packages

| Platform | Packages | Made by |
|---|---|---|
| Windows x64, arm64 | `…-setup.exe` (Inno Setup, per user by default), `….zip` | `build/package-windows.ps1`, `build/windows/installer.iss` |
| macOS Apple silicon | `….dmg` with `Azure Blob Downloader.app` (ad-hoc signed) | `build/package-macos.sh`, `build/macos/` |
| Linux x64 | `….AppImage`, `….tar.gz` | `build/package-linux.sh` (appimagetool, version and checksum pinned) |

Building a package locally: `dotnet publish src/AzureBlobDownloader.App -c Release -r <rid> -o publish`, then the
matching script (the Windows installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)).

Not done yet (needs accounts): code signing on Windows (e.g. Azure Trusted Signing) and macOS (Apple Developer ID +
notarization; `build/package-macos.sh` already signs with the hardened runtime when `MACOS_SIGN_IDENTITY` is set).

## Making a release

1. In [`CHANGELOG.md`](../CHANGELOG.md), rename `## [Unreleased]` to the version and date, e.g.
   `## [1.1.0] - 2026-12-01`; it becomes the top of the release notes (the release fails without it). Set `<Version>`
   in `Directory.Build.props` to the same version. Commit and push, and wait for the run to be green.
2. Tag that commit and push the tag:
   ```bash
   git tag v1.1.0
   ```
   ```bash
   git push origin v1.1.0
   ```
3. The run builds version 1.1.0 and creates a **draft** release with all packages, `SHA256SUMS.txt` and the notes
   (changelog section + [`.github/release-notes.md`](../.github/release-notes.md) + GitHub's change list).
   Repository → **Releases** → the draft → check text and files → **Publish release**.
4. Set `<Version>` to the next planned version (e.g. `1.1.1`), so development builds are `1.1.1-ci.<run>` and never
   look older than the release, and add an empty `## [Unreleased]` section to the changelog. Note user-visible
   changes there as they are made.

Versions follow [Semantic Versioning](https://semver.org): bug fixes raise the last number (1.0.1), new features the
middle one (1.1.0), changes that break existing setups the first one (2.0.0).

## Security updates

- Every build checks all NuGet packages (also indirect ones) against the GitHub Advisory Database. **High or critical
  vulnerabilities fail the build**; low and moderate ones show as warnings (`NU1901`–`NU1904`). The *Vulnerable packages*
  step in the Ubuntu test job lists them with links.
- The **weekly scheduled run** does this even when nobody commits, so a newly published advisory turns the run red and you
  get an e-mail. GitHub switches scheduled workflows off after 60 days without activity in the repository; it shows a banner
  on the Actions tab and you re-enable it there with one click.
- **Dependabot security alerts and updates** (repository *Settings → Advanced Security*): GitHub e-mails you about a
  vulnerable dependency and opens a pull request that updates it. The pull request runs the full CI; merge when green.
- Updating packages yourself: change the version in `Directory.Packages.props`, build and test locally, push.
- **Actions are pinned to a commit SHA** (`uses: actions/checkout@<sha> # v7.0.1`), so a moved or hijacked tag cannot
  change what runs, including in the release job that may write. To update one, look up the commit of the new version
  (`gh api repos/actions/checkout/commits/v7.0.2 --jq .sha`) and replace both the SHA and the comment. Dependabot
  security updates also cover actions and keep this format.
- The .NET runtime is bundled in every package and comes from the newest .NET 10 SDK on the runner, so any rebuild after
  Microsoft's monthly patch day includes the latest .NET security fixes. Customers get them by installing the new version.

## Cost

Public repositories: GitHub-hosted runners are free. Private repositories: the account's included minutes are used, and
macOS minutes count 10×, Windows 2× (Settings → Billing shows the usage).
