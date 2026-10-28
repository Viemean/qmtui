#!/usr/bin/env bash
set -euo pipefail

# 默认版本号、目标架构与编译模式
VERSION="0.4.7"
ARCH="arm64"
USE_AOT=true

# 解析入参：版本号、架构 (arm64 / x64)、模式标志 (--aot / --self-contained)
for arg in "$@"; do
    case "$arg" in
        --aot|-aot|aot)
            USE_AOT=true
            ;;
        --self-contained|self-contained)
            USE_AOT=false
            ;;
        x86_64|amd64|x64)
            ARCH="x64"
            ;;
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

case "$ARCH" in
    x64)
        RID="osx-x64"
        ;;
    arm64)
        RID="osx-arm64"
        ;;
    *)
        echo "Error: 不支持的目标架构: $ARCH (仅支持 arm64 / x64)"
        exit 1
        ;;
esac

# 若在非 macOS 环境下编译 AOT，由于缺少苹果 ld64 链接器与 macOS SDK，自动回退为自包含编译
if [ "$USE_AOT" = true ] && [ "$(uname -s)" != "Darwin" ]; then
    echo "Notice: 检测到当前非 macOS 宿主环境，缺乏苹果原生 Mach-O ld64 链接器；自动回退为自包含 (self-contained) 交叉编译模式。"
    USE_AOT=false
fi

OUTPUT_FILE="qmtui-${VERSION}-darwin-${ARCH}.tar.gz"

if [ "$USE_AOT" = true ]; then
    echo "==> Building macOS Native AOT release package for ${ARCH} (${RID})..."
    dotnet publish -c Release -r "$RID" -p:PublishAot=true QmTui.csproj
else
    echo "==> Building macOS self-contained release package for ${ARCH} (${RID})..."
    dotnet publish -c Release -r "$RID" --self-contained true -p:PublishAot=false QmTui.csproj
fi

BIN_PATH="bin/Release/net10.0/${RID}/publish/qmtui"
if [ ! -f "$BIN_PATH" ]; then
    BIN_PATH="bin/Release/net10.0/${RID}/publish/QQMusic.Tui"
fi

if [ ! -f "$BIN_PATH" ]; then
    echo "Error: 未找到发布二进制产物: $BIN_PATH"
    exit 1
fi

# 组装归档打包目录
STAGE_DIR=$(mktemp -d /tmp/qmtui_mac_stage.XXXXXX)
trap 'rm -rf "$STAGE_DIR"' EXIT

cp "$BIN_PATH" "$STAGE_DIR/qmtui"
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
