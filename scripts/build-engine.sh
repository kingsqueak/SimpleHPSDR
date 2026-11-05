#!/usr/bin/env bash
# Publish station-engine for one runtime into artifacts/engine/<rid>.
# Usage: scripts/build-engine.sh [rid]   (default: this machine's)
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-$(dotnet --info | awk '/RID:/ {print $2; exit}')}"
engine_version=$(scripts/engine-version.sh)
dotnet publish external/station-engine/StationEngine/StationEngine.csproj \
  -p:VersionPrefix="$engine_version" -p:VersionSuffix= -c Release -r "$rid" --self-contained false -o "artifacts/engine/$rid"
echo "engine -> artifacts/engine/$rid"
