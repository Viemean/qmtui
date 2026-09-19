#!/usr/bin/env bash
set -euo pipefail

# 入参：版本号、包构建号、目标架构 (amd64 / arm64)、.NET Runtime ID (RID)
VERSION="${1:-0.3.6}"
PKGREL="${2:-1}"
ARCH="${3:-amd64}"

case "$ARCH" in
    x86_64|amd64)
        ARCH="amd64"
        RID="${4:-linux-x64}"
        ;;
    aarch64|arm64)
        ARCH="arm64"
        RID="${4:-linux-arm64}"
        ;;
    *)
        ARCH="${3:-amd64}"
        RID="${4:-linux-x64}"
        ;;
esac

PKGNAME="qmtui"
OUTPUT_FILE="${PKGNAME}_${VERSION}-${PKGREL}_${ARCH}.deb"

BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
if [ ! -f "$BIN_PATH" ]; then
    BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "==> Binary not found at $BIN_PATH, building native AOT binary for Debian ${ARCH} (${RID})..."
    dotnet publish -c Release -r "${RID}" QmTui.csproj
    BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "Error: Binary not found at $BIN_PATH after publish."
    exit 1
fi

# 优先使用 dpkg-deb，缺失时自动回退至 ar + tar 打包模式
USE_DPKG=true
if ! command -v dpkg-deb >/dev/null 2>&1; then
    if ! command -v ar >/dev/null 2>&1; then
        echo "Error: Neither dpkg-deb nor ar found. Please install dpkg (e.g. yay -S dpkg) or binutils."
        exit 1
    fi
    USE_DPKG=false
fi

echo "==> Assembling fakeroot deb directory..."
STAGE_DIR=$(mktemp -d /tmp/qmtui_deb.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/DEBIAN" "$STAGE_DIR/usr/bin" "$STAGE_DIR/usr/share/qmtui"
cp "$BIN_PATH" "$STAGE_DIR/usr/bin/qmtui"
chmod 755 "$STAGE_DIR/usr/bin/qmtui"
ln -sf qmtui "$STAGE_DIR/usr/bin/qqmusic-tui"

if [ -d "www" ]; then
    mkdir -p "$STAGE_DIR/usr/share/qmtui/www"
    cp -r www/* "$STAGE_DIR/usr/share/qmtui/www/"
fi

# 生成 DEBIAN/postinst 钩子脚本
cat << 'EOF' > "$STAGE_DIR/DEBIAN/postinst"
#!/bin/sh
set -e
exit 0
EOF
chmod 755 "$STAGE_DIR/DEBIAN/postinst"

# 生成 DEBIAN/postrm 卸载清理脚本
cat << 'EOF' > "$STAGE_DIR/DEBIAN/postrm"
#!/bin/sh
set -e
exit 0
EOF
chmod 755 "$STAGE_DIR/DEBIAN/postrm"

# Debian Installed-Size 单位是 KiB
INSTALLED_SIZE=$(du -sk "$STAGE_DIR/usr" | awk '{print $1}')

DEBIAN_DEPS="libgstreamer1.0-0, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, libpulse0"

echo "==> Generating DEBIAN/control metadata..."
cat << EOF > "$STAGE_DIR/DEBIAN/control"
Package: ${PKGNAME}
Version: ${VERSION}-${PKGREL}
Section: sound
Priority: optional
Architecture: ${ARCH}
Maintainer: Yuzuki <lxf74663@gmail.com>
Installed-Size: ${INSTALLED_SIZE}
Depends: ${DEBIAN_DEPS}
Recommends: gstreamer1.0-libav
Suggests: wl-clipboard, xclip
Provides: qqmusic-tui
Replaces: qqmusic-tui, qqmusic-tui-bin
Conflicts: qqmusic-tui, qqmusic-tui-bin
Homepage: https://github.com/Viemean/qmtui
Description: Linux terminal music player (.NET 10 Native AOT)
 A high-performance terminal music player written in C# (.NET 10 Native AOT),
 featuring rich TUI interface, local Web UI remote control, and lossless GStreamer playback.
EOF

if [ "$USE_DPKG" = true ]; then
    echo "==> Packing Debian .deb package with dpkg-deb..."
    dpkg-deb --build --root-owner-group "$STAGE_DIR" "$OUTPUT_FILE"
else
    echo "==> Packing Debian .deb package with GNU ar and tar..."
    echo "2.0" > "$STAGE_DIR/debian-binary"
    tar --owner=0 --group=0 -czf "$STAGE_DIR/control.tar.gz" -C "$STAGE_DIR/DEBIAN" .
    tar --owner=0 --group=0 -czf "$STAGE_DIR/data.tar.gz" -C "$STAGE_DIR" usr
    ar -rc "$OUTPUT_FILE" "$STAGE_DIR/debian-binary" "$STAGE_DIR/control.tar.gz" "$STAGE_DIR/data.tar.gz"
fi

echo "==> Debian package generated successfully: ${OUTPUT_FILE}"
ls -lh "$OUTPUT_FILE"
