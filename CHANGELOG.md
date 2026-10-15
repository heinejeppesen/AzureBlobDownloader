# Changelog

Notable changes per release. Versions follow [Semantic Versioning](https://semver.org).
The release workflow uses the section for the version being released as its release notes.

## [Unreleased]

- Windows: native libraries are loaded only from the app's own folder and System32, never from the current folder
  or `PATH`, so a planted DLL cannot be loaded into the app.

## [1.0.0] - 2026-10-15

First public release.

- Paste the link you received (a container or a folder; also when copied from an e-mail or an Outlook Safe Link),
  sign in with your own work account and download.
- Signs in against the sharing organisation's tenant, so B2B guests get a token Azure Storage accepts; Windows
  sign-in broker (WAM) on Windows, system browser on macOS and Linux; the sign-in is remembered securely.
- Folder tree with tick boxes, folder sizes on demand and the total size of the selection.
- Parallel, resumable downloads: continue after network drops, sleep, restarts and an expired sign-in; files are
  checked against their MD5 checksum (or size); the computer is kept awake.
- Checks before downloading: free space, long paths, names Windows does not allow, upper/lower case clashes.
- One-time setup (organisation domain or Tenant ID, and Client ID), or settings deployed by IT.
- Windows installer (x64, arm64), macOS DMG (Apple silicon), Linux AppImage; portable zip and tar.gz.
