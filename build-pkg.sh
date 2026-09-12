#!/usr/bin/env bash
set -euo pipefail

# 入参：版本号、包构建号、目标架构、.NET Runtime ID (RID)
VERSION="${1:-0.3.4}"
PKGREL="${2:-1}"
ARCH="${3:-x86_64}"
RID="${4:-linux-x64}"
PKGNAME="qmtui-bin"
OUTPUT_FILE="${PKGNAME}-${VERSION}-${PKGREL}-${ARCH}.pkg.tar.zst"

echo "==> Building native AOT binary for ${ARCH} (${RID})..."
dotnet publish -c Release -r "${RID}" QmTui.csproj

BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
if [ ! -f "$BIN_PATH" ]; then
    BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "Error: Binary not found at $BIN_PATH"
    exit 1
fi

echo "==> Assembling fakeroot package directory..."
STAGE_DIR=$(mktemp -d /tmp/qmtui_pkg.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

mkdir -p "$STAGE_DIR/usr/bin" "$STAGE_DIR/usr/share/qmtui"
cp "$BIN_PATH" "$STAGE_DIR/usr/bin/qmtui"
chmod 755 "$STAGE_DIR/usr/bin/qmtui"
# 建立向下兼容软链接
ln -sf qmtui "$STAGE_DIR/usr/bin/qqmusic-tui"

if [ -d "www" ]; then
    mkdir -p "$STAGE_DIR/usr/share/qmtui/www"
    cp -r www/* "$STAGE_DIR/usr/share/qmtui/www/"
fi

# 生成 Pacman .INSTALL 安装后钩子脚本
cat << 'EOF' > "$STAGE_DIR/.INSTALL"
post_install() {
    :
}

post_upgrade() {
    post_install "$1"
}

post_remove() {
    :
}
EOF
chmod 755 "$STAGE_DIR/.INSTALL"

# 计算已安装文件总大小（字节）
INSTALLED_SIZE=$(du -sb "$STAGE_DIR/usr" | awk '{print $1}')
BUILD_DATE=$(date +%s)

echo "==> Generating .PKGINFO metadata..."
cat << EOF > "$STAGE_DIR/.PKGINFO"
pkgname = ${PKGNAME}
pkgbase = ${PKGNAME}
pkgver = ${VERSION}-${PKGREL}
pkgdesc = Linux terminal qqmusic player (.NET 10 Native AOT pre-built package)
url = https://github.com/Viemean/qmtui
builddate = ${BUILD_DATE}
packager = Yuzuki <lxf74663@gmail.com>
size = ${INSTALLED_SIZE}
arch = ${ARCH}
license = MIT
depend = gstreamer
depend = gst-plugins-base
depend = gst-plugins-good
depend = gst-plugins-bad
depend = libpulse
optdepend = gst-libav: additional audio codecs (AAC/M4A) support
optdepend = wl-clipboard: Wayland clipboard support for copying song links
optdepend = xclip: X11 clipboard support for copying song links
provides = qmtui
provides = qqmusic-tui
provides = qmtui-bin
conflict = qmtui
conflict = qqmusic-tui
conflict = qqmusic-tui-bin
replaces = qqmusic-tui
replaces = qqmusic-tui-bin
EOF
# 移除可能的空白行
sed -i '/^[[:space:]]*$/d' "$STAGE_DIR/.PKGINFO"

echo "==> Compressing Arch Linux package with zstd..."
tar -C "$STAGE_DIR" -c --zstd -f "$OUTPUT_FILE" .PKGINFO .INSTALL usr

echo "==> Package generated successfully: ${OUTPUT_FILE}"
ls -lh "$OUTPUT_FILE"
