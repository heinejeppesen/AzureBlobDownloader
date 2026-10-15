# Azure Blob Downloader

A desktop app for downloading shared folders from Azure Blob Storage, made for people who are not
Azure experts. Paste the link you received, sign in with your own work account, tick what you want and
download it. A simpler alternative to Azure Storage Explorer when an organisation shares files with
customers or partners (Microsoft Entra B2B guests).

## Features

- **Paste a link and go**: accepts container and folder links, also when copied from an e-mail (Outlook Safe
  Links, surrounding text, invisible characters).
- **Signs in to the right organisation**: always against the sharing organisation's tenant, so guests do not
  get the "Server failed to authenticate the request" error. Windows sign-in broker (WAM) on Windows, the
  system browser elsewhere; the sign-in is remembered securely (DPAPI, Keychain, keyring).
- **Uses only Azure Storage**: no Azure Resource Manager or Microsoft Graph calls, and the user's own token
  (no SAS), so storage logs show who downloaded what.
- **Browse and select**: folder tree with tick boxes, folder sizes on demand, total size of the selection.
- **Large, reliable downloads**: parallel files and parts, resumes after network drops, sleep, restarts or an
  expired sign-in; files are checked against their MD5 checksum (or size); keeps the computer awake.
- **Checks before it starts**: free space, long paths, names Windows does not allow, upper/lower case clashes.
- **No setup for end users when IT deploys it**, or a one-time setup screen: organisation domain and app ID.

Platforms: Windows 11 (x64, arm64), macOS on Apple silicon, Ubuntu 24.04 or newer (x64). Built with .NET 10
and Avalonia; the packages include everything they need.

## Documentation

| | |
|---|---|
| [docs/INSTALL.md](docs/INSTALL.md) | Installing on Windows, macOS and Linux |
| [docs/ADMIN.md](docs/ADMIN.md) | For the organisation sharing the files: app registration, configuration, deployment, Conditional Access |
| [docs/DESIGN.md](docs/DESIGN.md) | Design decisions |
| [docs/CI.md](docs/CI.md) | Building, CI, releases and security updates |
| [docs/TEST-PLAN.md](docs/TEST-PLAN.md) | Manual test plan |

## Building

Requires the .NET 10 SDK.

```bash
dotnet build AzureBlobDownloader.slnx
```

```bash
dotnet test --solution AzureBlobDownloader.slnx
```

```bash
dotnet run --project src/AzureBlobDownloader.App
```

Packages (installer, DMG, AppImage) are built by GitHub Actions; see [docs/CI.md](docs/CI.md).

## Security

Please report vulnerabilities privately; see [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
