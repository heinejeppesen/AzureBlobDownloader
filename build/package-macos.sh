#!/usr/bin/env bash
# Packages a macOS publish folder as "Azure Blob Downloader.app" inside a DMG. Runs on macOS only.
#
#   dotnet publish src/AzureBlobDownloader.App -c Release -r osx-arm64 -o publish/osx-arm64
#   bash build/package-macos.sh publish/osx-arm64 0.2.0 0.2.0 42 dist
#
# Signing: ad hoc by default (Apple silicon refuses unsigned code). Set MACOS_SIGN_IDENTITY to a
# "Developer ID Application: …" identity in the keychain to sign with the hardened runtime instead
# (notarization is a separate step, not done here yet).
set -euo pipefail

publish_dir="$1"
version="$2"          # e.g. 0.2.0 or 0.2.0-ci.17 (file name)
short_version="$3"    # e.g. 0.2.0 (CFBundleShortVersionString, numbers only)
build_number="$4"     # e.g. 17 (CFBundleVersion)
out_dir="${5:-dist}"

app_name="Azure Blob Downloader"
bundle_id="${BUNDLE_ID:-io.github.heinejeppesen.azureblobdownloader}"
sign_identity="${MACOS_SIGN_IDENTITY:--}"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

app="$work/$app_name.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out_dir"
cp -R "$publish_dir"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/AzureBlobDownloader"

# Icon: all sizes from the 1024 px PNG.
iconset="$work/AppIcon.iconset"
mkdir -p "$iconset"
source_icon="$root/src/AzureBlobDownloader.App/Assets/app-icon.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$source_icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$source_icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"

sed -e "s|@BUNDLE_ID@|$bundle_id|g" -e "s|@VERSION@|$short_version|g" -e "s|@BUILD@|$build_number|g" \
  "$root/build/macos/Info.plist" > "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

# Sign every file in Contents/MacOS (the bundle signature requires all of them to be signed, including the
# .NET assemblies, which get their signature in extended attributes), then the bundle itself. The main
# executable is left to the bundle signature: signing it checks the whole bundle, so it must come last.
main_executable="$app/Contents/MacOS/AzureBlobDownloader"
if [ "$sign_identity" = "-" ]; then
  sign_args=(--force --sign - --timestamp=none)
else
  sign_args=(--force --sign "$sign_identity" --timestamp --options runtime --entitlements "$root/build/macos/entitlements.plist")
fi
find "$app/Contents/MacOS" -type f ! -path "$main_executable" -print0 | while IFS= read -r -d '' file; do
  if ! output=$(codesign "${sign_args[@]}" "$file" 2>&1); then
    echo "$output" >&2
    exit 1
  fi
done
codesign "${sign_args[@]}" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

# DMG with the app and a shortcut to Applications, for drag-and-drop install.
staging="$work/dmg"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
dmg="$out_dir/AzureBlobDownloader-$version-osx-arm64.dmg"
rm -f "$dmg"
hdiutil create -volname "$app_name" -srcfolder "$staging" -ov -format UDZO "$dmg"
ls -lh "$dmg"
