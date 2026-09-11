#!/usr/bin/env bash
set -euo pipefail

# 默认安装目标路径：~/.local/share/qmtui/
TARGET_DIR="${1:-$HOME/.local/share/qmtui}"

MIRROR_URLS=(
    "https://ghfast.top/https://raw.githubusercontent.com/Viemean/assets/main/qmtui/qafp-runtime-arm64.tar.gz"
    "https://fastly.jsdelivr.net/gh/Viemean/assets@main/qmtui/qafp-runtime-arm64.tar.gz"
    "https://cdn.jsdelivr.net/gh/Viemean/assets@main/qmtui/qafp-runtime-arm64.tar.gz"
    "https://raw.githubusercontent.com/Viemean/assets/main/qmtui/qafp-runtime-arm64.tar.gz"
)

# 检测非 ARM64 架构下的 QEMU 模拟器
ARCH=$(uname -m)
if [ "$ARCH" != "aarch64" ] && [ "$ARCH" != "arm64" ]; then
    if ! command -v qemu-aarch64-static >/dev/null 2>&1 && ! command -v qemu-aarch64 >/dev/null 2>&1; then
        echo "==> 检测到系统架构为 $ARCH，需要 QEMU ARM64 用户态模拟器运行 QAFP。"
        echo "==> 正在准备安装 QEMU 模拟器..."
        SUDO_CMD=""
        if [ "$EUID" -ne 0 ]; then
            if command -v sudo >/dev/null 2>&1; then
                SUDO_CMD="sudo"
            else
                echo "错误: 缺少 QEMU 模拟器且当前无 sudo 权限。" >&2
                exit 1
            fi
        fi

        if command -v pacman >/dev/null 2>&1; then
            $SUDO_CMD pacman -S --needed --noconfirm qemu-user
        elif command -v apt-get >/dev/null 2>&1; then
            $SUDO_CMD apt-get update
            $SUDO_CMD apt-get install -y qemu-user-static
        elif command -v dnf >/dev/null 2>&1; then
            $SUDO_CMD dnf install -y qemu-user-static
        else
            echo "错误: 未识别到支持的系统包管理器，请手动安装 qemu-user 或 qemu-user-static。" >&2
            exit 1
        fi
    fi
fi

echo "==> 正在配置 qmtui QAFP 听歌识曲运行时..."
echo "==> 安装目录: ${TARGET_DIR}"

mkdir -p "${TARGET_DIR}"

SUCCESS=false
for url in "${MIRROR_URLS[@]}"; do
    echo "==> Trying mirror: ${url}..."
    if curl -fsSL --connect-timeout 6 --retry 1 "${url}" | tar -xz -C "${TARGET_DIR}/"; then
        SUCCESS=true
        break
    fi
done

if [ "${SUCCESS}" = false ]; then
    echo "Error: Failed to download QAFP runtime from all available mirrors."
    exit 1
fi

if [ -f "${TARGET_DIR}/qafp/qafp_runner" ]; then
    chmod 755 "${TARGET_DIR}/qafp/qafp_runner"
fi

if [ -f "${TARGET_DIR}/qafp/sysroot/system/bin/linker64" ]; then
    chmod 755 "${TARGET_DIR}/qafp/sysroot/system/bin/linker64"
fi

echo "==> QAFP runtime successfully installed to: ${TARGET_DIR}/qafp"
