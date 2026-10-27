#!/usr/bin/env bash
set -euo pipefail

# 默认版本号、目标架构与编译模式
VERSION="0.4.7"
ARCH="x64"
USE_AOT=false

# 解析入参：版本号、架构 (x64 / arm64)、模式标志 (--aot)
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
        RID="win-x64"
        MINGW_CXX="x86_64-w64-mingw32-g++"
        ;;
    arm64)
        RID="win-arm64"
        MINGW_CXX="aarch64-w64-mingw32-g++"
        if [ "$USE_AOT" = false ]; then
            echo "Error: arm64 仅支持 GitHub Actions / Windows 环境下的 Native AOT 编译 (--aot)，本地 Linux 仅提供 x64 自包含开发打包。"
            exit 1
        fi
        ;;
esac

OUTPUT_FILE="qmtui-${VERSION}-windows-${ARCH}.zip"

if [ "$USE_AOT" = true ]; then
    echo "==> Building Windows Native AOT release package for ${ARCH} (${RID})..."
else
    echo "==> Building Windows self-contained release package for ${ARCH} (${RID}) (Linux cross-compile fallback)..."
fi

# 1. 编译原生录音辅助工具 (qmtui-recorder.exe)
if [ -f "src/Native/qmtui-recorder.cpp" ]; then
    if command -v "$MINGW_CXX" >/dev/null 2>&1; then
        echo "==> Compiling native recorder helper via ${MINGW_CXX}..."
        "$MINGW_CXX" -O3 -static -s -o qmtui-recorder.exe src/Native/qmtui-recorder.cpp -lole32
    elif command -v g++ >/dev/null 2>&1 && [[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]]; then
        echo "==> Compiling native recorder helper via Windows g++..."
        g++ -O3 -static -s -o qmtui-recorder.exe src/Native/qmtui-recorder.cpp -lole32
    elif [ ! -f "qmtui-recorder.exe" ]; then
        echo "Error: C++ compiler not found and prebuilt qmtui-recorder.exe does not exist."
        exit 1
    else
        echo "==> Using existing qmtui-recorder.exe..."
    fi
fi

# 2. 发布 .NET 二进制
if [ "$USE_AOT" = true ]; then
    echo "==> Publishing .NET Native AOT binary (${RID})..."
    dotnet publish -c Release -r "${RID}" -p:PublishAot=true QmTui.csproj
else
    echo "==> Publishing .NET self-contained binary (${RID})..."
    dotnet publish -c Release -r "${RID}" --self-contained true -p:PublishAot=false QmTui.csproj
fi

PUBLISH_DIR="bin/Release/net10.0-windows10.0.26100.0/${RID}/publish"
if [ ! -d "$PUBLISH_DIR" ]; then
    PUBLISH_DIR="bin/Release/net10.0/${RID}/publish"
fi

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Error: Publish directory not found at $PUBLISH_DIR"
    exit 1
fi

# 3. 复制原生录音二进制
if [ -f "qmtui-recorder.exe" ]; then
    cp -f qmtui-recorder.exe "$PUBLISH_DIR/"
fi

# 4. 复制静态 Web 资源（若存在）
if [ -d "www" ]; then
    mkdir -p "$PUBLISH_DIR/www"
    cp -rf www/* "$PUBLISH_DIR/www/"
fi

# 5. 抽取并注入 GStreamer 极简音频运行库与插件
GST_SCRIPT="./setup_win_gst.py"
[ ! -f "$GST_SCRIPT" ] && GST_SCRIPT=".bin/setup_win_gst.py"
if [ -f "$GST_SCRIPT" ]; then
    echo "==> Bundling minimal GStreamer Windows audio runtime..."
    if [ "$ARCH" = "x64" ]; then
        python3 "$GST_SCRIPT" --dest "$PUBLISH_DIR"
    fi
fi

# 6. 打包为最终 Zip 归档文件
echo "==> Creating release archive: ${OUTPUT_FILE}..."
rm -f "${OUTPUT_FILE}"

if command -v zip >/dev/null 2>&1; then
    (cd "${PUBLISH_DIR}" && zip -r -q -9 "${OLDPWD}/${OUTPUT_FILE}" .)
else
    python3 -c "
import shutil, os, sys
publish_dir = sys.argv[1]
output_zip = sys.argv[2]
base_name = output_zip[:-4] if output_zip.endswith('.zip') else output_zip
shutil.make_archive(base_name, 'zip', publish_dir)
" "$PUBLISH_DIR" "$OUTPUT_FILE"
fi

echo "==> Package generated successfully: ${OUTPUT_FILE}"
ls -lh "${OUTPUT_FILE}"
