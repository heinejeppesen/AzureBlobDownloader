# For administrators

This page is for the organisation that shares files through Azure Blob Storage: what to set up in
Microsoft Entra ID, and how to give the app its settings.

## How access works

- Each shared folder is a blob **container** (or a folder inside one) in a storage account with
  **Microsoft Entra authorization** (shared key access may be disabled; no anonymous access needed).
- People who download are users in your tenant, usually **B2B guests** who sign in with their own work
  account. They need **Storage Blob Data Reader** on the container or storage account (for example through
  a group).
- You send them the link to the container, e.g. `https://contosodata.blob.core.windows.net/shared-files`.
- The app downloads with the user's own token, so the storage logs (`StorageBlobLogs`) record who
  downloaded what.

## App registration

Create it once, in your tenant. Its **Directory (tenant) ID** (or one of your domains) and
**Application (client) ID** are the two values the app needs.

### 1. Register the app

Entra admin center → **App registrations** → **New registration**

| Field | Value |
|---|---|
| Name | `Azure Blob Downloader` |
| Supported account types | **Accounts in this organizational directory only (Single tenant)** |
| Redirect URI | leave empty here (added in step 2) |

Copy the **Application (client) ID**; it goes into the app's config as `ClientId`.

### 2. Authentication

**Authentication** → **Add a platform** → **Mobile and desktop applications**, add both:

| Redirect URI | Used for |
|---|---|
| `http://localhost` | Sign-in in the system browser (macOS, Linux, Windows fallback). The app listens on a random local port; Entra ignores the port for `localhost`. |
| `ms-appx-web://microsoft.aad.brokerplugin/<ClientId>` | Windows sign-in broker (WAM). Replace `<ClientId>` with the ID from step 1. |

Do not tick the suggested `nativeclient` or `msal<ClientId>://auth` URIs.

**Advanced settings → Allow public client flows: No.** Interactive sign-in does not need it, and leaving
it off means device code flow cannot be used with this app. (If sign-in ever fails with `AADSTS7000218`,
switch it to Yes.)

**Certificates & secrets:** none. This is a public client and has no secret.

### 3. API permissions

1. Remove the default **Microsoft Graph → User.Read** (not used).
2. **Add a permission** → **APIs my organization uses** → search **Azure Storage** →
   **Delegated permissions** → `user_impersonation`.
3. **Add a permission** → **Microsoft Graph** → **Delegated permissions** →
   `openid`, `profile`, `offline_access`. The sign-in library always requests these
   (`offline_access` lets a long download refresh its token without prompting).
4. **Grant admin consent for <tenant>.** Required: guests cannot consent themselves, and without it they
   get `AADSTS65001`.

Final list, all delegated and all with status *Granted*:

| API | Permission |
|---|---|
| Azure Storage | `user_impersonation` |
| Microsoft Graph | `openid`, `profile`, `offline_access` |

The app never calls Microsoft Graph or Azure Resource Manager; it only talks to the blob data plane
(`https://<account>.blob.core.windows.net`).

### 4. Enterprise application (optional, recommended)

Entra admin center → **Enterprise applications** → *Azure Blob Downloader*:

- **Properties → Assignment required? Yes**, then **Users and groups** → assign the groups of people who
  download (for example the groups that hold *Storage Blob Data Reader*). Unassigned users then get a clear
  `AADSTS50105` instead of a storage 403.
- **Properties → Visible to users? No** keeps it out of My Apps.

### 5. Notes for Conditional Access

- **Resource:** Azure Storage, app ID `e406a681-f3d4-42a8-90b6-c2b029497af1`.
  **Client:** Azure Blob Downloader (the app ID from step 1).
- **No Azure Resource Manager access.** Azure Storage Explorer needs Windows Azure Service Management API;
  this app never requests an ARM token, so its users do not need an exception for it.
- **Device compliance for guests** relies on the cross-tenant access inbound setting *Trust compliant
  devices*. How the device claim reaches Entra:
  - Windows: through the Windows sign-in broker (WAM), which the app uses by default.
  - macOS: through the Enterprise SSO plug-in (Company Portal) in the guest's home tenant, with Safari or
    Edge as default browser (Chrome needs the Microsoft SSO extension).
  - Linux: Intune for Linux with Microsoft Edge as the default browser.
- **Sign-in frequency** policies can require a new sign-in in the middle of a long download. The app pauses
  the queue, asks the user to sign in, and continues.
- **Device code flow** is not used and can stay blocked.

## Configuration

The app needs the **Tenant ID** and the **Client ID** of the app registration above. The shipped config file
leaves them blank, so the same build can be used by any organisation.

Settings are read from up to four files named `AzureBlobDownloader.config.json`, later ones overriding
earlier ones setting by setting (blank values never override):

| Layer | Windows | macOS | Linux | Written by |
|---|---|---|---|---|
| 1. Shipped | next to `AzureBlobDownloader.exe` | inside the app | next to the app | the package |
| 2. Machine-wide | `%ProgramData%\AzureBlobDownloader\` | `/Library/Application Support/AzureBlobDownloader/` | `/etc/AzureBlobDownloader/` | IT (Intune, GPO, MDM…) |
| 3. Per user | `%APPDATA%\AzureBlobDownloader\` | `~/Library/Application Support/AzureBlobDownloader/` | `~/.config/AzureBlobDownloader/` | the setup screen |
| 4. `--config <file>` | any path | any path | any path | whoever starts the app |

**If the Tenant ID or Client ID is still missing, the app shows a setup screen** at start. The user enters
two values once: the organisation's **domain** (e.g. `contoso.com`, or any user's e-mail address there) or
its Tenant ID, and the Client ID. A domain is looked up and the Tenant ID is what gets saved, so the tenant
is pinned; config files always hold the Tenant ID. The values are checked (the tenant must exist) and saved
in the per-user file. The user can change them later under *Connection details → Change settings…* (this
signs them out). No admin rights are needed: the per-user folder is the user's own, unlike Program Files, a
signed macOS app or an AppImage.

Ways to deploy:

- **Users enter the values**: ship as is; send users your domain and the Client ID together with the link.
- **IT deploys them**: place a machine-wide file with `TenantId` and `ClientId` (and optionally a narrower
  `AllowedHosts`); users never see the setup screen.
- **Your own build**: put the values in the shipped file at packaging time.

Example file (all settings optional except the two IDs):

```json
{
  "TenantId": "00000000-0000-0000-0000-000000000000",
  "ClientId": "00000000-0000-0000-0000-000000000000",
  "AllowedHosts": [ "contoso*.blob.core.windows.net" ],
  "UseWindowsBroker": true,
  "DisplayName": "Contoso downloads",
  "SupportContact": "Questions? Contact downloads@contoso.com",
  "MaxConcurrentFiles": 4,
  "MaxConcurrentChunksPerFile": 4,
  "ChunkSizeMiB": 8
}
```

`AllowedHosts` decides which addresses the app will send the user's sign-in token to. It defaults to
`*.blob.core.windows.net`, can be narrowed to your storage accounts (e.g. `contoso*.blob.core.windows.net`),
and **every entry must end in `.blob.core.windows.net`**, whichever file it comes from. It is not on the setup
screen, so users cannot be talked into widening it.
