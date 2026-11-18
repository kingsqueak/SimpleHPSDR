#!/usr/bin/env bash
# Publish station-engine for one runtime into artifacts/engine/<rid>.
# Usage: scripts/build-engine.sh [rid]   (default: this machine's)
set -euo pipefail
cd "$(dirname "$0")/.."
# The repo pins SDK 10.x (global.json); Homebrew's dotnet is 11-only and also
# links against Homebrew's brotli, which breaks publish. Prefer Microsoft's.
[ -x /usr/local/share/dotnet/dotnet ] && export PATH="/usr/local/share/dotnet:$PATH"
rid="${1:-$(dotnet --info | awk '/RID:/ {print $2; exit}')}"
engine_version=$(scripts/engine-version.sh)
# Start from an empty folder: publishing over an old one keeps any file that is
# newer than the one replacing it (LiteDB 5.0.20's DLL outlived the 5.0.21 update).
rm -rf "artifacts/engine/$rid"
dotnet publish external/station-engine/StationEngine/StationEngine.csproj \
  -p:Version="$engine_version" -c Release -r "$rid" --self-contained false -o "artifacts/engine/$rid"
echo "engine -> artifacts/engine/$rid"
