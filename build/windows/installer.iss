; Inno Setup script for Azure Blob Downloader (Windows x64 and arm64).
; Built by build/package-windows.ps1, which passes the defines below.
;
; Installs per user without admin rights by default (%LOCALAPPDATA%\Programs\Azure Blob Downloader);
; an administrator can choose "all users" (Program Files) in the first dialog or with /ALLUSERS.
; Silent install for IT:  setup.exe /VERYSILENT /ALLUSERS   (or /CURRENTUSER)

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error SourceDir (the dotnet publish output) must be defined
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef OutputName
  #define OutputName "AzureBlobDownloader-setup"
#endif

#define AppName "Azure Blob Downloader"
#define AppExe "AzureBlobDownloader.exe"

[Setup]
; Never change AppId: upgrades find the existing installation by it.
AppId={{99DBB9BC-3DCC-42CF-978D-124CB1EBF354}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#SourcePath}..\..\src\AzureBlobDownloader.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Per-user installs: remove the remembered sign-in (token cache), logs and the last download folder.
; The per-user settings file in %APPDATA% (Tenant ID, Client ID) is kept for a reinstall.
Type: filesandordirs; Name: "{localappdata}\AzureBlobDownloader"; Check: not IsAdminInstallMode
