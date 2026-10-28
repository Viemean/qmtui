#!/usr/bin/env bash
set -euo pipefail

# 默认版本号与目标架构 (仅支持 Apple Silicon ARM64)
VERSION="0.4.9"
ARCH="arm64"
RID="osx-arm64"

# 解析入参：版本号
for arg in "$@"; do
    case "$arg" in
        aarch64|arm64)
            ARCH="arm64"
            ;;
        *)
            if [[ "$arg" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]]; then
                VERSION="$arg"
            fi
            ;;
    esac
done

OUTPUT_FILE="qmtui-${VERSION}-darwin-${ARCH}.tar.gz"

echo "==> Building macOS self-contained release package for ${ARCH} (${RID})..."
dotnet publish -c Release -r "$RID" --self-contained true -p:PublishAot=false -p:PublishSingleFile=true QmTui.csproj

PUBLISH_DIR="bin/Release/net10.0/${RID}/publish"
BIN_PATH="${PUBLISH_DIR}/qmtui"
if [ ! -f "$BIN_PATH" ]; then
    BIN_PATH="${PUBLISH_DIR}/QQMusic.Tui"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "Error: 未找到发布二进制产物: $BIN_PATH"
    exit 1
fi

# 组装归档打包目录
STAGE_DIR=$(mktemp -d /tmp/qmtui_mac_stage.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

cp -r "${PUBLISH_DIR}"/* "$STAGE_DIR/"
rm -f "$STAGE_DIR"/*.exe "$STAGE_DIR"/*.pdb
chmod 755 "$STAGE_DIR/qmtui"
ln -sf qmtui "$STAGE_DIR/qqmusic-tui"

# 打包 Web 播放器静态资源
if [ -d "www" ]; then
    mkdir -p "$STAGE_DIR/www"
    cp -r www/* "$STAGE_DIR/www/"
fi

# 压缩打包
tar -czvf "$OUTPUT_FILE" -C "$STAGE_DIR" .

echo "==> Successfully created macOS package: $OUTPUT_FILE"
