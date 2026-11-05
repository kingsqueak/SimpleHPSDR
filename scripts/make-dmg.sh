#!/usr/bin/env bash
# Build SimpleHPSDR.app (with station-engine inside) and a DMG for macOS.
# Usage: scripts/make-dmg.sh [rid]   (default osx-arm64)
# Output: artifacts/mac/SimpleHPSDR.app and artifacts/SimpleHPSDR-<version>-<arch>.dmg
#
# Both the app and the engine are self-contained (no .NET install needed).
# The engine goes in Contents/Resources/engine, where EngineHost looks for it;
# only this runtime's native libraries are kept. Ad-hoc signed (not notarized).
set -euo pipefail
cd "$(dirname "$0")/.."
# Homebrew's dotnet bundles its own runtime, linked to Homebrew's brotli, which
# wouldn't run on other Macs. Use Microsoft's when it's installed.
[ -x /usr/local/share/dotnet/dotnet ] && export PATH="/usr/local/share/dotnet:$PATH"
rid="${1:-osx-arm64}"
arch="${rid#osx-}"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleHPSDR/SimpleHPSDR.csproj)
out=artifacts/mac
app="$out/SimpleHPSDR.app"
stage="$out/stage"

rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/engine" "$stage"

echo "== app ($rid)"
dotnet publish src/SimpleHPSDR/SimpleHPSDR.csproj -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$app/Contents/MacOS"

echo "== engine ($rid)"
engine_version=$(scripts/engine-version.sh)
dotnet publish external/station-engine/StationEngine/StationEngine.csproj -c Release -r "$rid" -p:VersionPrefix="$engine_version" -p:VersionSuffix= \
  --self-contained true -o "$app/Contents/Resources/engine"
# Drop other platforms' native libraries.
for d in "$app/Contents/Resources/engine/runtimes/"*; do
  [ "$(basename "$d")" = "$rid" ] || rm -rf "$d"
done
find "$app/Contents" -name '*.pdb' -delete

cp src/SimpleHPSDR/Assets/AppIcon.icns "$app/Contents/Resources/"
cp LICENSE "$app/Contents/Resources/"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SimpleHPSDR $version</string>
  <key>CFBundleDisplayName</key><string>SimpleHPSDR</string>
  <key>CFBundleIdentifier</key><string>com.kc2rgw.simplehpsdr</string>
  <key>CFBundleExecutable</key><string>SimpleHPSDR</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key>
  <string>SimpleHPSDR uses the microphone for transmit audio.</string>
  <key>NSLocalNetworkUsageDescription</key>
  <string>SimpleHPSDR finds and talks to your radio over the local network.</string>
</dict>
</plist>
EOF

echo "== check"
scripts/check-mac-bundle.sh "$app"

echo "== sign (ad-hoc)"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

echo "== dmg"
ditto "$app" "$stage/SimpleHPSDR.app"
ln -s /Applications "$stage/Applications"
dmg="artifacts/SimpleHPSDR-$version-$arch.dmg"
rm -f "$dmg"
hdiutil create -volname "SimpleHPSDR $version" -srcfolder "$stage" -format UDZO -ov "$dmg" >/dev/null
rm -rf "$stage"
du -sh "$app" "$dmg"
echo "dmg -> $dmg"
