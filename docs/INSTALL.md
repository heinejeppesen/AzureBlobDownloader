# Installing Azure Blob Downloader

Download from the **[latest release](https://github.com/heinejeppesen/AzureBlobDownloader/releases/latest)**.
Each package includes everything it needs; no .NET installation is required. The packages are not signed
with a paid publisher certificate, so Windows and macOS show a warning the first time (see below).

| Platform | File | |
|---|---|---|
| Windows 11 (most PCs) | `AzureBlobDownloader-<version>-win-x64-setup.exe` | installer |
| Windows 11 on ARM | `AzureBlobDownloader-<version>-win-arm64-setup.exe` | installer |
| Windows, no install | `AzureBlobDownloader-<version>-win-x64.zip` / `-win-arm64.zip` | portable |
| macOS, Apple silicon | `AzureBlobDownloader-<version>-osx-arm64.dmg` | |
| Linux x64 (Ubuntu 24.04 or newer) | `AzureBlobDownloader-<version>-linux-x64.AppImage` | single file |
| Linux x64, no AppImage | `AzureBlobDownloader-<version>-linux-x64.tar.gz` | portable |

Each release also has `SHA256SUMS.txt`. To check that a download is exactly the file built by this project,
compare its checksum: `Get-FileHash .\<file> -Algorithm SHA256` (Windows PowerShell), `shasum -a 256 <file>`
(macOS) or `sha256sum -c SHA256SUMS.txt --ignore-missing` (Linux).

Development builds of every commit are on the Actions tab (Build → a run → *Artifacts*).

## Windows

1. Run the `-setup.exe`. If SmartScreen says "Windows protected your PC", choose **More info** → **Run anyway**.
   If Windows blocks it with no way to continue, **Smart App Control** is on (some new Windows 11 PCs); it blocks
   all apps without a paid signature, and the only way around it is to turn Smart App Control off
   (*Windows Security → App & browser control*).
2. The installer asks whether to install for you only (no admin rights needed, the default) or for all users.
3. Start *Azure Blob Downloader* from the Start menu.

Upgrading: run the new installer; it replaces the old version. Uninstall from *Settings → Apps*; this also
removes the remembered sign-in and logs, but keeps the Tenant ID / Client ID settings for a reinstall.

For IT (silent install): `AzureBlobDownloader-<version>-win-x64-setup.exe /VERYSILENT /ALLUSERS`
(or `/CURRENTUSER`). Settings for all users go in `%ProgramData%\AzureBlobDownloader\` (see [ADMIN.md](ADMIN.md#configuration)).

Portable zip: unzip anywhere and run `AzureBlobDownloader.exe`.

## macOS

1. Open the `.dmg` and drag **Azure Blob Downloader** to **Applications**.
2. Start it. macOS says it cannot verify the app: click **Done** (not *Move to Bin*), then open
   **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to
   "Azure Blob Downloader was blocked", and confirm.
3. The first time the app remembers your sign-in, macOS asks to allow Keychain access: choose **Always Allow**.
   (Because the app is not signed with a Developer ID yet, each new version may ask again.)

Terminal alternative for step 2: `xattr -dr com.apple.quarantine "/Applications/Azure Blob Downloader.app"`

## Linux

```bash
chmod +x AzureBlobDownloader-*-linux-x64.AppImage
./AzureBlobDownloader-*-linux-x64.AppImage
```

- Needs a desktop session, a web browser for sign-in and, to remember the sign-in, a keyring
  (Ubuntu Desktop has GNOME Keyring). Without a keyring you sign in again each time the app starts.
- The AppImage needs FUSE 3, which Ubuntu 24.04 includes. If it does not start, run it with
  `--appimage-extract-and-run`, or use the tar.gz: unpack it and run `./AzureBlobDownloader`.
- To get a menu entry, use an AppImage manager such as Gear Lever or AppImageLauncher.

## Where the app keeps its files

| | Windows | macOS | Linux |
|---|---|---|---|
| Settings (Tenant ID, Client ID) | `%APPDATA%\AzureBlobDownloader\` | `~/Library/Application Support/AzureBlobDownloader/` | `~/.config/AzureBlobDownloader/` |
| Sign-in cache, logs, last folder | `%LOCALAPPDATA%\AzureBlobDownloader\` | `~/Library/Application Support/AzureBlobDownloader/` | `~/.local/share/AzureBlobDownloader/` |

Logs never contain sign-in tokens.
