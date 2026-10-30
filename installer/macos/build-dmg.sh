#!/usr/bin/env bash
# Builds slaw.app for one macOS architecture and wraps it in a drag-to-install
# disk image with the turntable background. Runs on macOS only (swift, iconutil,
# codesign, hdiutil); needs dmgbuild (pip install dmgbuild).
#
#   installer/macos/build-dmg.sh <osx-arm64|osx-x64> <tag> <output-dir>
#   installer/macos/build-dmg.sh osx-arm64 v0.2.0-beta.1 out/macos
#
# Writes <output-dir>/slaw-<tag>-<rid>.dmg. The app is ad-hoc signed, not
# notarized: Gatekeeper asks once before the first launch.
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "usage: $0 <osx-arm64|osx-x64> <tag> <output-dir>" >&2
    exit 2
fi
rid="$1"
tag="$2"
output_dir="$3"

case "$rid" in
    osx-arm64|osx-x64) ;;
    *) echo "error: '$rid' is not a macOS runtime identifier (osx-arm64 or osx-x64)" >&2; exit 2 ;;
esac
if ! [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; then
    echo "error: '$tag' is not a version tag like v1.2.3 or v1.2.3-beta.1" >&2
    exit 2
fi
semver="${tag#v}"
# CFBundleVersion is numeric major.minor.patch; prerelease labels live only in the tag
bundle_version="${semver%%[-+]*}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dmgbuild="${DMGBUILD:-dmgbuild}"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

app="$work_dir/slaw.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

echo "[dmg] publishing slaw $semver for $rid"
# Not single-file: inside a bundle the loose layout starts faster and keeps the
# native libraries (Skia, HarfBuzz, AvaloniaNative) where codesign can see them
dotnet publish "$repo_root/src/Slaw/Slaw.csproj" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=false -p:DebugType=None -p:Version="$semver" \
    -o "$app/Contents/MacOS"

echo "[dmg] rendering artwork"
swift "$repo_root/installer/macos/render-assets.swift" "$work_dir/assets"
cp "$work_dir/assets/slaw.icns" "$app/Contents/Resources/slaw.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>slaw</string>
    <key>CFBundleDisplayName</key>
    <string>slaw</string>
    <key>CFBundleIdentifier</key>
    <string>labs.slopstack.slaw</string>
    <key>CFBundleExecutable</key>
    <string>slaw</string>
    <key>CFBundleIconFile</key>
    <string>slaw.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$bundle_version</string>
    <key>CFBundleVersion</key>
    <string>$bundle_version</string>
    <key>CFBundleGetInfoString</key>
    <string>slaw $tag, slopstack labs</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.music</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST
plutil -lint "$app/Contents/Info.plist" >/dev/null

echo "[dmg] ad-hoc signing slaw.app"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

dmg="$output_dir/slaw-$tag-$rid.dmg"
rm -f "$dmg"
echo "[dmg] building $(basename "$dmg")"
"$dmgbuild" -s "$repo_root/installer/macos/dmg-settings.py" \
    -D app="$app" -D background="$work_dir/assets/background.png" \
    "slaw $tag" "$dmg"
hdiutil verify "$dmg" >/dev/null
echo "[dmg] wrote $dmg"
