#!/usr/bin/env bash
# Print the Station Engine version the submodule is pinned to, such as 2.0.63, or
# 2.0.68-simplehpsdr.1 for a tag on kingsqueak/station-engine that adds patches sent
# upstream but not yet in a Zeus release. The
# engine's own default is a placeholder, so the build scripts pass this in. CI checks the
# submodule out without tags, so fall back to asking its remote which tag this commit is.
# Fails rather than let a build ship the wrong number.
set -euo pipefail
cd "$(dirname "$0")/../external/station-engine"
tag=$(git describe --tags --exact-match 2>/dev/null || true)
if [ -z "$tag" ]; then
  sha=$(git rev-parse HEAD)
  tag=$(git ls-remote --tags origin | awk -v sha="$sha" '$1 == sha { sub("refs/tags/", "", $2); sub(/\^\{\}$/, "", $2); print $2 }' | sort -V | tail -1)
fi
version="${tag#v}"
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "engine-version: no release tag on $(git rev-parse --short HEAD)" >&2
  exit 1
fi
echo "$version"
