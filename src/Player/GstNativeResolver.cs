using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// 负责跨平台原生动态库重定向解析与执行环境目录配置。
/// 优先探测应用同级目录，次级匹配系统或开发环境。
/// </summary>
internal static partial class GstNativeResolver
{
    private static readonly Lock s_lock = new();
    private static bool s_isRegistered;

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectory(string lpPathName);

    private static readonly string[] s_macOsSearchDirs =
    [
        "/Library/Frameworks/GStreamer.framework/Versions/1.0/lib",
        "/opt/homebrew/lib",
        "/usr/local/lib"
    ];

    private static readonly string[] s_macOsBinDirs =
    [
        "/Library/Frameworks/GStreamer.framework/Commands",
        "/opt/homebrew/bin",
        "/usr/local/bin"
    ];

    /// <summary>
    /// 注册当前程序集的原生动态库重定向解析器，并补全进程执行环境。
    /// </summary>
    public static void EnsureRegistered()
    {
        if (s_isRegistered) return;
        lock (s_lock)
        {
            if (s_isRegistered) return;

            ConfigureEnvironment();
            NativeLibrary.SetDllImportResolver(typeof(GstNativeResolver).Assembly, ResolveDllImport);
            s_isRegistered = true;
        }
    }

    private static void ConfigureEnvironment()
    {
        var baseDir = AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            SetDllDirectory(baseDir);

            List<string> dirs = [baseDir];

            var devRoot = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64")
                          ?? Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_X86_64");
            if (!string.IsNullOrEmpty(devRoot))
            {
                dirs.Add(Path.Combine(devRoot, "bin"));
            }

            AppendPathsToEnvironment(dirs);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GST_PLUGIN_PATH_1_0")))
            {
                var bundledPluginDir = Path.Combine(baseDir, "lib", "gstreamer-1.0");
                if (Directory.Exists(bundledPluginDir))
                {
                    Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", bundledPluginDir);
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            List<string> dirs = [baseDir];
            dirs.AddRange(s_macOsBinDirs);
            AppendPathsToEnvironment(dirs);

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GST_PLUGIN_PATH_1_0")))
            {
                string[] candidatePluginDirs =
                [
                    Path.Combine(baseDir, "lib", "gstreamer-1.0"),
                    "/opt/homebrew/lib/gstreamer-1.0",
                    "/Library/Frameworks/GStreamer.framework/Versions/1.0/lib/gstreamer-1.0",
                    "/usr/local/lib/gstreamer-1.0"
                ];
                foreach (var pDir in candidatePluginDirs)
                {
                    if (Directory.Exists(pDir))
                    {
                        Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", pDir);
                        break;
                    }
                }
            }
        }
    }

    private static void AppendPathsToEnvironment(IEnumerable<string> directories)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var existing = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> toAdd = [];

        foreach (var dir in directories)
        {
            if (Directory.Exists(dir) && !existing.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                toAdd.Add(dir);
            }
        }

        if (toAdd.Count > 0)
        {
            var newPath = currentPath.Length > 0
                ? $"{currentPath}{Path.PathSeparator}{string.Join(Path.PathSeparator, toAdd)}"
                : string.Join(Path.PathSeparator, toAdd);
            Environment.SetEnvironmentVariable("PATH", newPath);
        }
    }

    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (OperatingSystem.IsWindows())
        {
            string? targetDll = libraryName switch
            {
                "libgstreamer-1.0.so.0" => "gstreamer-1.0-0.dll",
                "libgobject-2.0.so.0" => "gobject-2.0-0.dll",
                "libglib-2.0.so.0" => "glib-2.0-0.dll",
                "libgio-2.0.so.0" => "gio-2.0-0.dll",
                _ => null
            };

            if (targetDll is null) return IntPtr.Zero;

            var localPath = Path.Combine(AppContext.BaseDirectory, targetDll);
            if (File.Exists(localPath))
            {
                if (NativeLibrary.TryLoad(localPath, out var localHandle))
                {
                    return localHandle;
                }

                var err = Marshal.GetLastPInvokeError();
                if (err == 126)
                {
                    AppLogger.Warn("GstNativeResolver",
                        $"加载原生库 '{targetDll}' 失败 (Win32 错误码 126: 找不到指定的模块)。可能缺少运行依赖，若系统未安装 Visual C++ 2015-2022 运行库，请前往微软官方下载安装：https://aka.ms/vc14/vc_redist.x64.exe");
                }
                else
                {
                    AppLogger.Warn("GstNativeResolver", $"从路径 '{localPath}' 加载原生库 '{targetDll}' 失败：Win32 错误码 {err}");
                }
            }

            var devRoot = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64")
                          ?? Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_X86_64");
            if (!string.IsNullOrEmpty(devRoot))
            {
                var devPath = Path.Combine(devRoot, "bin", targetDll);
                if (NativeLibrary.TryLoad(devPath, out var devHandle))
                {
                    return devHandle;
                }
            }

            if (NativeLibrary.TryLoad(targetDll, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            string[] targetNames = libraryName switch
            {
                "libgstreamer-1.0.so.0" => ["libgstreamer-1.0.0.dylib", "libgstreamer-1.0.dylib"],
                "libgobject-2.0.so.0" => ["libgobject-2.0.0.dylib", "libgobject-2.0.dylib"],
                "libglib-2.0.so.0" => ["libglib-2.0.0.dylib", "libglib-2.0.dylib"],
                "libgio-2.0.so.0" => ["libgio-2.0.0.dylib", "libgio-2.0.dylib"],
                _ => []
            };

            if (targetNames.Length == 0) return IntPtr.Zero;

            foreach (var name in targetNames)
            {
                var localPath = Path.Combine(AppContext.BaseDirectory, name);
                if (NativeLibrary.TryLoad(localPath, out var localHandle))
                {
                    return localHandle;
                }
            }

            foreach (var dir in s_macOsSearchDirs)
            {
                foreach (var name in targetNames)
                {
                    var fullPath = Path.Combine(dir, name);
                    if (NativeLibrary.TryLoad(fullPath, out var handle))
                    {
                        return handle;
                    }
                }
            }

            foreach (var name in targetNames)
            {
                if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }
}
