# Manual test plan

Run on each platform: Windows 11 (x64, arm64), macOS (Apple silicon), Ubuntu 24.04+.
How to install each package: see [INSTALL.md](INSTALL.md). Log files (never contain tokens):

- Windows: `%LOCALAPPDATA%\AzureBlobDownloader\logs\`
- macOS: `~/Library/Application Support/AzureBlobDownloader/logs/`
- Linux: `~/.local/share/AzureBlobDownloader/logs/`

## Install and setup

| # | Case | Expected |
|---|---|---|
| 0a | Windows installer, per user (default) | No admin prompt; installs to `%LOCALAPPDATA%\Programs\Azure Blob Downloader`; Start-menu entry; app starts. |
| 0b | Windows installer over an older version | Replaces it in place; settings and sign-in kept. |
| 0c | Windows uninstall | Removed from *Apps*; `%LOCALAPPDATA%\AzureBlobDownloader` removed, `%APPDATA%\AzureBlobDownloader` kept. |
| 0d | macOS DMG | Drag to Applications; *Open Anyway* once; app shows its icon and version (*About* / Finder *Get Info*). |
| 0e | Linux AppImage | Starts after `chmod +x`; sign-in opens the browser. |
| 0f | First start without settings | Setup screen; after Save the app is ready; values in the per-user settings file. |

## Sign-in and access

| # | Case | Expected |
|---|---|---|
| 1 | B2B **guest** signs in with the container link | Signed in; *Connection details* shows Guest, token tenant = your Tenant ID; the container opens. |
| 2 | **Member** signs in | Same, shown as Member. |
| 3 | Guest whose **invitation is not accepted** | "This account is not set up for these files" (AADSTS50020 / 90072). |
| 4 | User **without the role** (not in the reader group) | "No access to these files" (AuthorizationPermissionMismatch), or "Your account has not been given access" (AADSTS50105) when assignment is required. |
| 5 | **Removed container** / wrong link | "Shared folder not found". |
| 6 | Restart the app | Still signed in, no prompt (token cache: DPAPI / Keychain / keyring). |
| 7 | Switch account, Sign out | Both work; sign-out clears the tree. |

## Browse and select

| # | Case | Expected |
|---|---|---|
| 8 | Open folders, tick a folder, tick single files | Tri-state boxes; ticking a folder counts it; selection total updates. |
| 9 | Folder with many files (10,000+) | Opens with a running count; scrolling stays smooth. |

## Download

| # | Case | Expected |
|---|---|---|
| 10 | Download a selection to a local folder | Folder picker opens at the last folder; files land in `<folder>/<container>/…`; finished screen says how many were checked by MD5. |
| 11 | Download the same selection again to the same folder | Pre-flight says the files are already downloaded; nothing is downloaded again. |
| 12 | **Pull the network cable** (or turn Wi-Fi off) mid-download for 1–5 minutes | "The connection was lost. Retrying automatically…"; continues by itself when the network is back; files verify. |
| 13 | **Sleep** the computer mid-download, wake it | Continues by itself (may show the retry message briefly). While downloading, the computer does not go to sleep on its own. |
| 14 | Pause, wait, Resume | Continues where it stopped (partial files kept). |
| 15 | Close the app mid-download (confirm), start again, download the same selection to the same folder | Pre-flight says partly downloaded files will continue; only the remaining parts are downloaded. |
| 16 | Cancel mid-download | Finished files kept, `.partial` files removed. |
| 17 | **Network drive** destination (mapped drive / UNC on Windows, mounted SMB share on macOS/Linux) | Free space shown; download works; disconnecting the share pauses with "The destination folder cannot be reached"; Resume continues. |
| 18 | Destination with too little free space | Pre-flight: "There is not enough free space…", download cannot start. |
| 19 | Very large file (10 GB+) | Steady memory (about 128 MB for transfers); progress, speed and time left update. |
| 20 | A file open in another program at the destination (Windows) | That file fails with "The file could not be saved…"; others complete; Retry failed works after closing it. |
| 21 | Names Windows does not allow (`a:b.txt`, `CON.txt`) or names differing only in case | Listed in the pre-flight report as renamed; stored under the new names. |
| 22 | Sign-in expires during a long download (or revoke the session in Entra) | "Your sign-in has expired" banner; signing in as the same account continues; another account is refused. |
