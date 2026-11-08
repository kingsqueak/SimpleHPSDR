#!/bin/sh
# Build the optional DRM decoder and copy it into a package folder.
# Usage: scripts/build-drm.sh <rid> <folder>
#
# The decoder is SimpleDream's native bridge to DecDRM (external/SimpleDream,
# GPL-3.0-or-later, with DecDRM, FDK-AAC and Opus inside). The app shows DRM
# only when it finds this library, so DRM is easy to take out: delete the
# external/SimpleDream submodule, this script and its calls in make-dmg.sh,
# make-linux.sh and make-windows.sh, and the app simply has no DRM.
#
# Without Rust or the submodule it warns and skips, unless DRM_REQUIRED=1 (CI).
set -eu
cd "$(dirname "$0")/.."
rid="$1"
out="$2"
crate=external/SimpleDream/native

case "$rid" in
  osx-arm64)   target=aarch64-apple-darwin;      lib=libsimpledream_native.dylib ;;
  osx-x64)     target=x86_64-apple-darwin;       lib=libsimpledream_native.dylib ;;
  linux-x64)   target=x86_64-unknown-linux-gnu;  lib=libsimpledream_native.so ;;
  linux-arm64) target=aarch64-unknown-linux-gnu; lib=libsimpledream_native.so ;;
  win-x64)     target=x86_64-pc-windows-msvc;    lib=simpledream_native.dll ;;
  *) if [ "${DRM_REQUIRED:-0}" = 1 ]; then echo "build-drm: no DRM decoder for $rid" >&2; exit 1; fi
     echo "build-drm: no DRM decoder for $rid" >&2; exit 0 ;;
esac

skip() {
  if [ "${DRM_REQUIRED:-0}" = 1 ]; then echo "build-drm: $1" >&2; exit 1; fi
  echo "build-drm: $1; building without DRM" >&2
  exit 0
}
[ -f "$crate/Cargo.toml" ] || skip "external/SimpleDream is missing (git submodule update --init --recursive)"

# On Linux, DRM_DOCKER_IMAGE builds it in an older distribution's container,
# so it needs no newer glibc than that (CI: Debian 12, glibc 2.36). Built on
# the host, it needs the host's, which may be newer than the users' systems.
case "$rid" in
  linux-*)
    if [ -n "${DRM_DOCKER_IMAGE:-}" ]; then
      rm -rf .drm-out && mkdir .drm-out
      # Runs as root to install the build tools, then hands its files back.
      docker run --rm -v "$PWD:/w" -w /w -e DRM_REQUIRED=1 -e OWNER="$(id -u):$(id -g)" "$DRM_DOCKER_IMAGE" sh -c \
        'apt-get update -qq && apt-get install -y -qq --no-install-recommends cmake libasound2-dev pkg-config > /dev/null \
         && scripts/build-drm.sh "$0" /w/.drm-out; status=$?; \
         chown -R "$OWNER" /w/.drm-out external/SimpleDream/native/target 2> /dev/null; exit $status' "$rid"
      cp -R .drm-out/. "$out/"
      rm -rf .drm-out
      echo "build-drm: built in $DRM_DOCKER_IMAGE -> $out"
      exit 0
    fi ;;
esac

command -v cargo > /dev/null || skip "no cargo (Rust)"

# Match the app's minimum macOS.
export MACOSX_DEPLOYMENT_TARGET=14.0
if command -v rustup > /dev/null; then rustup target add "$target" > /dev/null 2>&1 || true; fi
cargo build --release --manifest-path "$crate/Cargo.toml" --target "$target"
cp "$crate/target/$target/release/$lib" "$out/"
# The licences that travel with it.
dec=external/SimpleDream/external/DecDRM
lic="$out/drm-licenses"
mkdir -p "$lic"
cp external/SimpleDream/LICENSE "$lic/SimpleDream-LICENSE"
cp "$dec/LICENSE" "$lic/DecDRM-LICENSE"
cp "$dec/third_party/fdk-aac/NOTICE" "$lic/FDK-AAC-NOTICE"
cp "$dec/third_party/opus/COPYING" "$lic/Opus-COPYING"
cp "$dec/third_party/libxaac/LICENSE" "$lic/libxaac-LICENSE"
cp "$dec/third_party/libxaac/NOTICE" "$lic/libxaac-NOTICE"
case "$rid" in
  osx-*) install_name_tool -id "@rpath/$lib" "$out/$lib" ;;
esac
echo "build-drm: $lib -> $out"
