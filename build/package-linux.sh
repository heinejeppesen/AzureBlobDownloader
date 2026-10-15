#!/usr/bin/env bash
# Packages a Linux publish folder as a tar.gz and an AppImage. Runs on Linux x64.
#
#   dotnet publish src/AzureBlobDownloader.App -c Release -r linux-x64 -o publish/linux-x64
#   bash build/package-linux.sh publish/linux-x64 0.2.0 dist
#
# The AppImage uses the static type-2 runtime: it needs FUSE 3 (fusermount3), which Ubuntu 24.04 has,
# but not libfuse2. Without FUSE it can still run with --appimage-extract-and-run.
set -euo pipefail

publish_dir="$1"
version="$2"
out_dir="${3:-dist}"

appimagetool_version="1.9.1"
appimagetool_sha256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
name="AzureBlobDownloader-$version-linux-x64"
mkdir -p "$out_dir"

chmod +x "$publish_dir/AzureBlobDownloader"
tar -czf "$out_dir/$name.tar.gz" -C "$publish_dir" .

# AppDir layout
appdir="$work/AzureBlobDownloader.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -R "$publish_dir"/. "$appdir/usr/bin/"

icon="$appdir/usr/share/icons/hicolor/256x256/apps/azure-blob-downloader.png"
if command -v convert >/dev/null; then
  convert "$root/src/AzureBlobDownloader.App/Assets/app-icon.png" -resize 256x256 "$icon"
else
  cp "$root/src/AzureBlobDownloader.App/Assets/app-icon.png" "$icon"
fi
cp "$icon" "$appdir/azure-blob-downloader.png"

cat > "$appdir/usr/share/applications/azure-blob-downloader.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Azure Blob Downloader
Comment=Download shared folders from Azure Blob Storage
Exec=AzureBlobDownloader
Icon=azure-blob-downloader
Categories=Network;FileTransfer;
Terminal=false
EOF
cp "$appdir/usr/share/applications/azure-blob-downloader.desktop" "$appdir/"

cat > "$appdir/AppRun" <<'EOF'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/bin/AzureBlobDownloader" "$@"
EOF
chmod +x "$appdir/AppRun"

# appimagetool, pinned and checked
tool="$work/appimagetool"
curl -fsSL -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-x86_64.AppImage"
echo "$appimagetool_sha256  $tool" | sha256sum -c -
chmod +x "$tool"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$out_dir/$name.AppImage"
chmod +x "$out_dir/$name.AppImage"
ls -lh "$out_dir/$name".*
