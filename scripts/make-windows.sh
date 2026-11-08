#!/usr/bin/env bash
# Build the Windows zip: SimpleHPSDR.exe with station-engine in engine\.
# Usage: scripts/make-windows.sh   (Git Bash on Windows; also runs on a Mac or
# Linux box to check the contents, but only Windows gives the .exe its icon)
# Output: artifacts/SimpleHPSDR-<version>-win-x64.zip
#
# win-x64 only: the engine ships its ASIO and audio libraries for x64, not arm64.
# Self-contained (no .NET install needed). Not signed, so Windows warns on first run.
set -euo pipefail
cd "$(dirname "$0")/.."
rid=win-x64
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleHPSDR/SimpleHPSDR.csproj | tr -d '\r')
out=artifacts/win
app="$out/SimpleHPSDR-$version"

rm -rf "$out"
mkdir -p "$app/engine"

echo "== app ($rid)"
dotnet publish src/SimpleHPSDR/SimpleHPSDR.csproj -c Release -r "$rid" --self-contained true \
  -p:UseAppHost=true -o "$app"
rm -f "$app"/onnxruntime*.lib   # import libraries, not needed at run time

echo "== engine ($rid)"
engine_version=$(scripts/engine-version.sh)
dotnet publish external/station-engine/StationEngine/StationEngine.csproj -c Release -r "$rid" -p:VersionPrefix="$engine_version" -p:VersionSuffix= \
  --self-contained true -o "$app/engine"
# Drop other platforms' native libraries.
for d in "$app/engine/runtimes/"*; do
  [ "$(basename "$d")" = "$rid" ] || rm -rf "$d"
done
find "$app" -name '*.pdb' -delete

# wdsp and miniaudio need the Visual C++ runtime, which a fresh Windows may
# not have; Microsoft allows shipping these beside the app.
if [ -d /c/Windows/System32 ]; then
  for dll in vcruntime140.dll vcruntime140_1.dll msvcp140.dll; do
    cp "/c/Windows/System32/$dll" "$app/engine/"
  done
fi

cp LICENSE "$app/" 2>/dev/null || true

zip="artifacts/SimpleHPSDR-$version-win-x64.zip"
rm -f "$zip"
if command -v 7z >/dev/null; then
  (cd "$out" && 7z a -tzip -bd "../$(basename "$zip")" "SimpleHPSDR-$version" >/dev/null)
else
  (cd "$out" && zip -qr "../$(basename "$zip")" "SimpleHPSDR-$version")
fi
du -sh "$app" "$zip"
echo "zip -> $zip"
