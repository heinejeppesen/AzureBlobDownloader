# Design decisions

| Topic | Decision |
|---|---|
| Platforms | Windows 11 x64 + arm64, macOS on Apple silicon, Ubuntu 24.04+ x64. Current OS versions only. |
| Stack | .NET 10 (LTS), Avalonia 12, self-contained publish (no .NET install needed). |
| Tenant | Set per deployment (setup screen or a config file; see [ADMIN.md](ADMIN.md)). Authority is always `https://login.microsoftonline.com/{TenantId}`, never `/common` or `/organizations`, so guests get a token from the sharing organisation that Azure Storage accepts. |
| Auth library | MSAL.NET + Microsoft.Identity.Client.Extensions.Msal, wrapped in a `TokenCredential` for Azure.Storage.Blobs. Chosen over Azure.Identity for sign-out/switch account, direct AADSTS codes for plain-language errors, and explicit keyring detection. |
| Scope | Only `https://storage.azure.com/user_impersonation`. The credential refuses any other audience. No Azure Resource Manager, no Microsoft Graph calls. |
| Windows broker (WAM) | On by default on Windows (`UseWindowsBroker`): it passes device claims, which Conditional Access policies requiring compliant devices need. System browser on macOS/Linux. No device code flow. |
| Token cache | DPAPI / Keychain / libsecret. Linux without a working keyring: in-memory cache for the session only, never a plaintext file. |
| Links | Only `https`, port 443, hosts matching `AllowedHosts` (all ending in `.blob.core.windows.net`), validated before any sign-in. Copied e-mail text, Outlook Safe Links and invisible characters are handled; ambiguous text is refused rather than guessed. |
| Browsing | A flattened, virtualized tree (one level listed at a time with `/` as delimiter) so folders with very many files stay fast. Folder sizes are counted on demand; one flat listing gives the totals of a folder and all its sub-folders. |
| Download engine | Own queue on Azure.Storage.Blobs rather than Azure.Storage.DataMovement: DataMovement resumes from its own checkpoint store and writes straight to the target, while this app needs `.partial` + rename, skip-by-size-and-time, ETag-guarded resume, MD5 while writing, pausing on 401 and pre-flight checks. |
| Download layout | `<destination>/<link root name>/<blob path>`: the container name (or the linked folder's name) is the top folder, so a later download of the same share lands in the same tree and resumes/skips. Account links: one folder per container. |
| Parallelism and memory | 4 files × 4 parts × 8 MiB by default (`MaxConcurrentFiles`, `MaxConcurrentChunksPerFile`, `ChunkSizeMiB`). Parts are read in parallel but written in order, so at most files × parts buffers exist (128 MiB by default) and resume state is a single "bytes valid" number. |
| Resume | `name.partial` + `name.partial.json` (ETag, size, bytes valid; saved every 5 s after a flush to disk). Restart: same size and modified time (±2 s) = done; partial with the same ETag = continue (re-hashing the existing part for MD5); otherwise start over. Every ranged GET has `If-Match: <ETag>`; a changed blob restarts the file. |
| Integrity | Content-MD5 when the blob has one (computed while writing), otherwise size. |
| Failures | Network errors (incl. 5xx/429/timeouts, sleep/resume): retried per file with backoff up to 60 s, indefinitely. Expired or rejected sign-in (HTTP 401): one silent refresh, else one sign-in prompt (same account enforced) while all files wait. Disk full or destination gone: the whole download pauses with the reason. Other per-file errors: that file fails; "Retry failed" looks it up again. |
| Local names | Blob names are mapped deterministically: `.`/`..`/empty segments are skipped (never written outside the destination), names Windows does not allow are renamed, upper/lower case and file/folder clashes get a number. Reported before the download starts. |
| Pause / cancel / close | Pause keeps partial files. Cancel deletes partial files of unfinished files, keeps finished ones. Closing during a download asks, then pauses (partials kept for next time). |
| Keep awake | Windows `SetThreadExecutionState`; macOS `caffeinate -i -w <pid>`; Linux `systemd-inhibit --what=sleep:idle` (ends with the app). |
| Proxy | System proxy for all traffic, default credentials for authenticating proxies. |
| Configuration | Ships with blank `TenantId`/`ClientId`. Layers: shipped → machine-wide (IT) → per-user → `--config`. Missing IDs → setup screen (domain or Tenant ID, and Client ID), saved in the per-user file; no admin rights needed on any OS. |
| Packaging | Windows: Inno Setup installer (per user by default) + zip. macOS: `.app` in a DMG, ad-hoc signed. Linux: AppImage + tar.gz. Built by GitHub Actions; see [CI.md](CI.md). |
| Not yet | Code signing (Windows, Apple Developer ID + notarization) and auto-update. |
| Telemetry | None. A local log file without tokens. |
