#!/usr/bin/env bash
# Builds the macOS .app bundle.
#
#   bash tools/publish-macos.sh [osx-arm64|osx-x64]
#
# Produces dist/MindMap.app, self-contained (no .NET runtime needed on the
# target Mac) and AD-HOC signed. There is no notarization, so on first launch
# the user must right-click the app and choose Open once.
set -euo pipefail

RID="${1:-osx-arm64}"
root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/MindMap/MindMap.csproj"
stage="$root/artifacts/publish-$RID"
app="$root/dist/MindMap.app"
iconset="$root/artifacts/MindMap.iconset"
icon="$root/artifacts/MindMap.icns"

if [[ "$(uname -s)" != "Darwin" ]]; then
  # dotnet cross-publishes fine, but iconutil and codesign only exist on macOS.
  echo "warning: not running on macOS - the bundle will not be signed." >&2
fi

version="$(grep -o '<Version>[^<]*</Version>' "$proj" | head -n1 | sed -e 's/<[^>]*>//g')"
version="${version:-0.0.0}"

echo "==> publishing $RID (self-contained, v$version)"
rm -rf "$stage" "$app" "$iconset"
dotnet publish "$proj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$stage"

echo "==> assembling bundle"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$stage"/. "$app/Contents/MacOS/"
sed "s/__VERSION__/$version/g" "$root/tools/macos/Info.plist" > "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/MindMap"

if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  echo "==> creating app icon"
  mkdir -p "$iconset"
  src_icon="$root/MindMap/Assets/app-icon.png"
  sips -z 16 16 "$src_icon" --out "$iconset/icon_16x16.png" >/dev/null
  sips -z 32 32 "$src_icon" --out "$iconset/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$src_icon" --out "$iconset/icon_32x32.png" >/dev/null
  sips -z 64 64 "$src_icon" --out "$iconset/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$src_icon" --out "$iconset/icon_128x128.png" >/dev/null
  sips -z 256 256 "$src_icon" --out "$iconset/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$src_icon" --out "$iconset/icon_256x256.png" >/dev/null
  sips -z 512 512 "$src_icon" --out "$iconset/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$src_icon" --out "$iconset/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$src_icon" --out "$iconset/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$iconset" -o "$icon"
  cp "$icon" "$app/Contents/Resources/MindMap.icns"
else
  echo "warning: sips/iconutil unavailable - app icon was not bundled." >&2
fi

if command -v codesign >/dev/null 2>&1; then
  echo "==> ad-hoc signing"
  codesign --force --deep --sign - "$app"
  codesign --verify --verbose "$app"
fi

echo
echo "Built: $app"
echo "First launch: right-click the app -> Open (ad-hoc signed, not notarized)."
