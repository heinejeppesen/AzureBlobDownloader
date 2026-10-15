<#
.SYNOPSIS
    Packages a Windows publish folder as a portable zip and an Inno Setup installer.
.EXAMPLE
    dotnet publish src/AzureBlobDownloader.App -c Release -r win-x64 -o publish/win-x64
    ./build/package-windows.ps1 -Rid win-x64 -PublishDir publish/win-x64 -Version 0.2.0
#>
param(
    [Parameter(Mandatory)] [ValidateSet('win-x64', 'win-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $Version,
    [string] $NumericVersion = ($Version -split '-')[0],
    [string] $OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$publish = (Resolve-Path $PublishDir).Path
$out = (Resolve-Path $OutDir).Path
$name = "AzureBlobDownloader-$Version-$Rid"

# Portable zip
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $out "$name.zip") -Force

# Installer
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it with "winget install JRSoftware.InnoSetup" or "choco install innosetup".'
}

& $iscc /Q `
    "/DAppVersion=$Version" `
    "/DNumericVersion=$NumericVersion" `
    "/DArch=$($Rid -replace '^win-', '')" `
    "/DSourceDir=$publish" `
    "/DOutputDir=$out" `
    "/DOutputName=$name-setup" `
    (Join-Path $PSScriptRoot 'windows\installer.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Get-ChildItem $out -Filter "$name*" | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
