#!/usr/bin/env python3
"""
GStreamer Windows x64 音频运行库提取部署脚本。
"""

import argparse
import os
import shutil
import struct
import subprocess
import sys
import urllib.request

DEFAULT_VERSION = "1.28.8"
INSTALLER_URL_TEMPLATE = "https://gstreamer.freedesktop.org/data/pkg/windows/{version}/msvc/gstreamer-1.0-msvc-x86_64-{version}.exe"

# 音频插件白名单（位于 lib/gstreamer-1.0/）
AUDIO_PLUGIN_WHITELIST = {
    # 核心与管道调度
    "gstcoreelements.dll",
    "gstplayback.dll",
    "gsttypefindfunctions.dll",
    "gstautodetect.dll",
    "gstapp.dll",
    # 音频流解析与转换
    "gstaudioparsers.dll",
    "gstaudioconvert.dll",
    "gstaudioresample.dll",
    "gstvolume.dll",
    "gstwavparse.dll",
    "gstid3demux.dll",
    "gstapetag.dll",
    # 音频解码器
    "gstflac.dll",
    "gstmpg123.dll",
    "gstisomp4.dll",
    "gstogg.dll",
    "gstvorbis.dll",
    "gstopus.dll",
    # Windows 原生音频输出
    "gstwasapi.dll",
    "gstwasapi2.dll",
    "gstdirectsound.dll",
    # 网络流与协议客户端
    "gstsoup.dll",
    "gsttcp.dll",
}


def parse_pe_imports(filepath: str) -> list[str]:
    """纯 Python 解析 Windows PE 文件的导入表，提取所依赖的 DLL 文件名列表。"""
    try:
        with open(filepath, "rb") as f:
            data = f.read()

        if len(data) < 64 or data[:2] != b"MZ":
            return []

        pe_offset = struct.unpack("<I", data[0x3C:0x40])[0]
        if data[pe_offset : pe_offset + 4] != b"PE\x00\x00":
            return []

        magic = struct.unpack("<H", data[pe_offset + 24 : pe_offset + 26])[0]
        is_64 = magic == 0x20B
        import_rva_offset = pe_offset + 24 + (120 if is_64 else 104)
        import_rva, import_size = struct.unpack("<II", data[import_rva_offset : import_rva_offset + 8])

        if import_rva == 0 or import_size == 0:
            return []

        num_sections = struct.unpack("<H", data[pe_offset + 6 : pe_offset + 8])[0]
        opt_hdr_size = struct.unpack("<H", data[pe_offset + 20 : pe_offset + 22])[0]
        sec_offset = pe_offset + 24 + opt_hdr_size

        sections = []
        for i in range(num_sections):
            hdr = data[sec_offset + i * 40 : sec_offset + (i + 1) * 40]
            vsize, vrva, rsize, roffset = struct.unpack("<IIII", hdr[8:24])
            sections.append((vrva, max(vsize, rsize), roffset))

        def rva_to_offset(rva: int):
            for vrva, size, roff in sections:
                if vrva <= rva < vrva + size:
                    return roff + (rva - vrva)
            return None

        import_file_offset = rva_to_offset(import_rva)
        if not import_file_offset:
            return []

        dlls = []
        pos = import_file_offset
        while True:
            desc = data[pos : pos + 20]
            if len(desc) < 20 or desc == b"\x00" * 20:
                break
            name_rva = struct.unpack("<I", desc[12:16])[0]
            name_off = rva_to_offset(name_rva)
            if name_off:
                end = data.find(b"\x00", name_off)
                dll_name = data[name_off:end].decode("latin1", errors="ignore")
                dlls.append(dll_name)
            pos += 20

        return dlls
    except Exception:
        return []


def download_file(url: str, dest_path: str):
    if os.path.exists(dest_path) and os.path.getsize(dest_path) > 0:
        print(f"[setup_win_gst] 安装包已存在: {dest_path}")
        return

    print(f"[setup_win_gst] 正在下载 GStreamer 官方安装包: {url}")
    os.makedirs(os.path.dirname(dest_path), exist_ok=True)
    temp_path = dest_path + ".download"

    def report_hook(block_num, block_size, total_size):
        downloaded = block_num * block_size
        if total_size > 0:
            percent = downloaded * 100 / total_size
            mb = downloaded / (1024 * 1024)
            total_mb = total_size / (1024 * 1024)
            sys.stdout.write(f"\r  -> 下载进度: {percent:.1f}% ({mb:.1f} MB / {total_mb:.1f} MB)")
            sys.stdout.flush()

    urllib.request.urlretrieve(url, temp_path, reporthook=report_hook)
    print()
    os.replace(temp_path, dest_path)
    print(f"[setup_win_gst] 下载完成: {dest_path}")


def locate_gst_directories(root_dir: str):
    bin_dir = None
    plugins_dir = None
    gio_modules_dir = None

    for current_dir, _, files in os.walk(root_dir):
        if "gstreamer-1.0-0.dll" in files and bin_dir is None:
            bin_dir = current_dir
        if "gstcoreelements.dll" in files and plugins_dir is None:
            plugins_dir = current_dir
        if "gioopenssl.dll" in files and gio_modules_dir is None:
            gio_modules_dir = current_dir

    return bin_dir, plugins_dir, gio_modules_dir


def unpack_installer(installer_path: str, extract_dir: str):
    os.makedirs(extract_dir, exist_ok=True)
    is_windows = sys.platform == "win32"

    if is_windows:
        cmd = [
            installer_path,
            f"/DIR={extract_dir}",
            "/TYPE=runtime",
            "/CURRENTUSER",
            "/VERYSILENT",
            "/NORESTART",
        ]
        print(f"[setup_win_gst] 正在执行 Windows 静默解包: {' '.join(cmd)}")
        subprocess.run(cmd, check=True)
    elif shutil.which("innoextract"):
        cmd = ["innoextract", "-e", "-d", extract_dir, installer_path]
        print(f"[setup_win_gst] 正在使用 innoextract 解包: {' '.join(cmd)}")
        subprocess.run(cmd, check=True)
    elif shutil.which("wine"):
        abs_extract = os.path.abspath(extract_dir)
        cmd = [
            "wine",
            installer_path,
            f"/DIR=Z:{abs_extract}",
            "/TYPE=runtime",
            "/CURRENTUSER",
            "/VERYSILENT",
            "/NORESTART",
        ]
        print(f"[setup_win_gst] 正在通过 Wine 静默解包: {' '.join(cmd)}")
        subprocess.run(cmd, check=True)
    else:
        raise RuntimeError("未找到解包工具。请在 Windows 环境运行，或在 Linux 下安装 innoextract 或 wine。")


def extract_minimal_gst(extract_dir: str, target_dir: str):
    bin_src, plugin_src, gio_src = locate_gst_directories(extract_dir)

    if not bin_src or not plugin_src:
        raise RuntimeError(f"在解包目录中未找到 GStreamer bin 或 plugin 目录: {extract_dir}")

    target_bin = os.path.abspath(target_dir)
    target_plugin = os.path.join(target_bin, "lib", "gstreamer-1.0")
    target_gio = os.path.join(target_bin, "lib", "gio", "modules")

    os.makedirs(target_bin, exist_ok=True)
    os.makedirs(target_plugin, exist_ok=True)

    # 1. 复制白名单中的音频插件至 lib/gstreamer-1.0/
    copied_plugins = []
    for item in os.listdir(plugin_src):
        src_path = os.path.join(plugin_src, item)
        if not os.path.isfile(src_path):
            continue
        if item.lower() in AUDIO_PLUGIN_WHITELIST:
            dst_path = os.path.join(target_plugin, item)
            shutil.copy2(src_path, dst_path)
            copied_plugins.append(dst_path)

    # 2. 复制 GIO TLS 模块（支持 HTTPS / WebDAV 流加密）
    copied_gio = []
    if gio_src and os.path.exists(gio_src):
        os.makedirs(target_gio, exist_ok=True)
        for item in os.listdir(gio_src):
            if item.lower() in ("gioopenssl.dll", "giognutls.dll"):
                src_path = os.path.join(gio_src, item)
                dst_path = os.path.join(target_gio, item)
                shutil.copy2(src_path, dst_path)
                copied_gio.append(dst_path)

    # 3. 递归计算依赖闭包集合（仅复制被音频插件与核心显式引入的 bin/ DLL）
    seed_files = [os.path.join(bin_src, "gstreamer-1.0-0.dll")] + copied_plugins + copied_gio
    scanner_path = os.path.join(bin_src, "gst-plugin-scanner.exe")
    if os.path.exists(scanner_path):
        seed_files.append(scanner_path)

    required_bin_dlls = set()
    queue = list(seed_files)
    visited = set()

    # 构建 bin/ 下所有文件的快速不区分大小写索引
    bin_files_map = {f.lower(): f for f in os.listdir(bin_src) if os.path.isfile(os.path.join(bin_src, f))}

    while queue:
        curr_file = queue.pop(0)
        if curr_file in visited:
            continue
        visited.add(curr_file)

        imported_dlls = parse_pe_imports(curr_file)
        for dep in imported_dlls:
            dep_lower = dep.lower()
            if dep_lower in bin_files_map:
                actual_name = bin_files_map[dep_lower]
                if actual_name not in required_bin_dlls:
                    required_bin_dlls.add(actual_name)
                    dep_src_path = os.path.join(bin_src, actual_name)
                    if dep_src_path not in visited:
                        queue.append(dep_src_path)

    # 确保核心入口与扫描器被复制
    for essential in ("gstreamer-1.0-0.dll", "gst-plugin-scanner.exe"):
        if essential.lower() in bin_files_map:
            required_bin_dlls.add(bin_files_map[essential.lower()])

    copied_bin_count = 0
    copied_bin_bytes = 0
    for dll in required_bin_dlls:
        src_path = os.path.join(bin_src, dll)
        dst_path = os.path.join(target_bin, dll)
        shutil.copy2(src_path, dst_path)
        copied_bin_count += 1
        copied_bin_bytes += os.path.getsize(src_path)

    plugin_bytes = sum(os.path.getsize(p) for p in copied_plugins)
    gio_bytes = sum(os.path.getsize(p) for p in copied_gio)
    total_bytes = copied_bin_bytes + plugin_bytes + gio_bytes
    total_mb = total_bytes / (1024 * 1024)

    print(f"[setup_win_gst] 提取完毕:")
    print(f"  • 核心运行时 DLL: {copied_bin_count} 个 ({copied_bin_bytes / (1024 * 1024):.1f} MB)")
    print(f"  • 音频与解码插件: {len(copied_plugins)} 个 ({plugin_bytes / (1024 * 1024):.1f} MB)")
    print(f"  • GIO TLS 加密模块: {len(copied_gio)} 个 ({gio_bytes / (1024 * 1024):.1f} MB)")
    print(f"  • 极简运行时总量: {copied_bin_count + len(copied_plugins) + len(copied_gio)} 个文件, 仅 {total_mb:.1f} MB")


def main():
    parser = argparse.ArgumentParser(description="GStreamer Windows x64 极简音频运行库提取与部署脚本")
    parser.add_argument("--dest", required=True, help="目标放置目录（通常为可执行文件所在根目录）")
    parser.add_argument("--version", default=DEFAULT_VERSION, help=f"GStreamer 版本号（默认: {DEFAULT_VERSION}）")
    parser.add_argument("--cache-dir", default=os.path.expanduser("~/.cache/qmtui/gst_installer"), help="安装包缓存目录")
    args = parser.parse_args()

    url = INSTALLER_URL_TEMPLATE.format(version=args.version)
    installer_filename = f"gstreamer-1.0-msvc-x86_64-{args.version}.exe"
    installer_path = os.path.join(args.cache_dir, installer_filename)
    extract_temp_dir = os.path.join(args.cache_dir, f"extracted_{args.version}")

    # 1. 确保安装包存在
    download_file(url, installer_path)

    # 2. 静默解包
    bin_found, _, _ = locate_gst_directories(extract_temp_dir)
    if not bin_found:
        unpack_installer(installer_path, extract_temp_dir)
    else:
        print(f"[setup_win_gst] 使用本地已解包缓存: {extract_temp_dir}")

    # 3. 抽取依赖闭包极简音频库至目标目录
    extract_minimal_gst(extract_temp_dir, args.dest)


if __name__ == "__main__":
    main()
