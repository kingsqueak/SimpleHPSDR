#!/bin/sh
# Build the Linux packages of SimpleHPSDR, with station-engine inside, for this
# machine's architecture. Both are self-contained, so no .NET install is needed.
# Run on Ubuntu; the .rpm and Arch packages need fpm (gem install fpm) and are
# skipped without it.
#
# Writes dist/simplehpsdr_<version>-1_<arch>.deb, an .rpm and a .pkg.tar.zst
set -eu
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleHPSDR/SimpleHPSDR.csproj)
ARCH=$(dpkg --print-architecture)
case "$ARCH" in
  amd64) RID=linux-x64; LINUX_ARCH=x86_64 ;;
  arm64) RID=linux-arm64; LINUX_ARCH=aarch64 ;;
  *) echo "Unsupported architecture $ARCH" >&2; exit 1 ;;
esac

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/usr/lib/simplehpsdr"
mkdir -p "$STAGE/DEBIAN" "$APP/engine" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
    "$STAGE/usr/share/icons/hicolor/512x512/apps" "$STAGE/usr/share/doc/simplehpsdr" dist

dotnet publish src/SimpleHPSDR/SimpleHPSDR.csproj -c Release -r "$RID" --self-contained true -o "$APP"
dotnet publish external/station-engine/StationEngine/StationEngine.csproj -c Release -r "$RID" \
    --self-contained true -o "$APP/engine"
# Drop other platforms' native libraries.
for d in "$APP/engine/runtimes/"*; do
  [ "$(basename "$d")" = "$RID" ] || rm -rf "$d"
done
find "$APP" -name '*.pdb' -delete

install -m 0644 src/SimpleHPSDR/Assets/icon.png "$STAGE/usr/share/icons/hicolor/512x512/apps/simplehpsdr.png"

# Invariant globalization: nothing here is locale-sensitive, and it saves
# depending on a particular ICU version.
cat > "$STAGE/usr/bin/simplehpsdr" << 'EOF'
#!/bin/sh
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
exec /usr/lib/simplehpsdr/SimpleHPSDR "$@"
EOF
chmod 0755 "$STAGE/usr/bin/simplehpsdr"

cat > "$STAGE/usr/share/applications/simplehpsdr.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=SimpleHPSDR
GenericName=OpenHPSDR radio
Comment=A clean, simple interface for OpenHPSDR radios
Exec=simplehpsdr
Icon=simplehpsdr
Terminal=false
Categories=HamRadio;Network;
Keywords=sdr;hpsdr;anan;radio;
EOF

cat > "$STAGE/usr/share/doc/simplehpsdr/copyright" << 'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: SimpleHPSDR
Source: https://github.com/kingsqueak/SimpleHPSDR

Files: *
Copyright: 2026 KC2RGW Chris
License: GPL-3+
 On Debian systems, the full text of the GNU General Public License
 version 3 can be found in /usr/share/common-licenses/GPL-3.

Files: usr/lib/simplehpsdr/engine/*
Copyright: 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR) and contributors
License: GPL-2+
 On Debian systems, the full text of the GNU General Public License
 version 2 can be found in /usr/share/common-licenses/GPL-2.
EOF

# Anything a bundled native library can't find on this system.
find "$APP/engine/runtimes" -name '*.so*' -exec ldd {} \; 2>/dev/null | grep 'not found' | sort -u || true

SIZE=$(du -sk "$STAGE" | cut -f1)
cat > "$STAGE/DEBIAN/control" << EOF
Package: simplehpsdr
Version: ${VERSION}-1
Section: hamradio
Priority: optional
Architecture: ${ARCH}
Installed-Size: ${SIZE}
Depends: libc6, libstdc++6, libgcc-s1, zlib1g, libssl3t64, libasound2t64, libx11-6, libice6, libsm6, libfontconfig1, libgomp1
Maintainer: KC2RGW Chris <2788495+kingsqueak@users.noreply.github.com>
Homepage: https://github.com/kingsqueak/SimpleHPSDR
Description: Clean, simple interface for OpenHPSDR radios
 A panadapter, waterfall and transceiver controls for OpenHPSDR radios,
 built on Station Engine, with native audio for low latency.
EOF

dpkg-deb --root-owner-group -Zxz --build "$STAGE" "dist/simplehpsdr_${VERSION}-1_${ARCH}.deb"

if command -v fpm > /dev/null; then
  rm -rf "$STAGE/DEBIAN"
  fpm_common() {
    fpm -s dir -C "$STAGE" -n simplehpsdr -v "$VERSION" --iteration 1 -a "$LINUX_ARCH" \
        --license GPL-3.0-or-later --url https://github.com/kingsqueak/SimpleHPSDR \
        --maintainer "KC2RGW Chris <2788495+kingsqueak@users.noreply.github.com>" \
        --description "Clean, simple interface for OpenHPSDR radios" "$@" usr
  }
  fpm_common -t rpm -p "dist/simplehpsdr-${VERSION}-1.${LINUX_ARCH}.rpm" \
      -d glibc -d libstdc++ -d libgcc -d zlib -d openssl-libs -d alsa-lib -d libX11 -d libICE -d libSM \
      -d fontconfig -d libgomp
  fpm_common -t pacman -p "dist/simplehpsdr-${VERSION}-1-${LINUX_ARCH}.pkg.tar.zst" \
      -d glibc -d gcc-libs -d zlib -d openssl -d alsa-lib -d libx11 -d libice -d libsm -d fontconfig
fi
ls -la dist/
