## Downloads

| Platform | File |
|---|---|
| Windows 11 (most PCs) | `AzureBlobDownloader-{{VERSION}}-win-x64-setup.exe` |
| Windows 11 on ARM | `AzureBlobDownloader-{{VERSION}}-win-arm64-setup.exe` |
| macOS (Apple silicon) | `AzureBlobDownloader-{{VERSION}}-osx-arm64.dmg` |
| Linux x64 (Ubuntu 24.04 or newer) | `AzureBlobDownloader-{{VERSION}}-linux-x64.AppImage` |

Portable versions without installation: the `.zip` (Windows) and `.tar.gz` (Linux) files.
How to install and start the app: [docs/INSTALL.md](https://github.com/{{REPOSITORY}}/blob/v{{VERSION}}/docs/INSTALL.md).

> **Before anyone can sign in**, the organisation that shares the files registers the app once in Microsoft
> Entra ID and gives users two values (its domain and the app's Client ID):
> [docs/ADMIN.md](https://github.com/{{REPOSITORY}}/blob/v{{VERSION}}/docs/ADMIN.md).

### Not code-signed

The packages are not signed with a paid publisher certificate, so:

- **Windows** shows "Windows protected your PC" the first time: choose *More info* → *Run anyway*.
  On PCs with **Smart App Control** turned on, unsigned apps are blocked.
- **macOS** says it cannot verify the app: allow it once under *System Settings → Privacy & Security* → *Open Anyway*.

To check that a download is exactly the file built here, compare its SHA-256 checksum with `SHA256SUMS.txt`:

- Windows (PowerShell): `Get-FileHash .\<file> -Algorithm SHA256`
- macOS: `shasum -a 256 <file>`
- Linux: `sha256sum -c SHA256SUMS.txt --ignore-missing`
