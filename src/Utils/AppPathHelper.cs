using System.IO;

namespace QmTui.Utils;

/// <summary>
/// 全局路径管理类（统一使用 ~/.config/qmtui, ~/.cache/qmtui, ~/.local/share/qmtui）
/// </summary>
public static class AppPathHelper
{
    private static readonly string s_userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 配置文件目录 (~/.config/qmtui)
    /// </summary>
    public static string ConfigDir { get; } = EnsureDirectory(Path.Combine(".config"), "qmtui");

    /// <summary>
    /// 缓存目录 (~/.cache/qmtui)
    /// </summary>
    public static string CacheDir { get; } = EnsureDirectory(Path.Combine(".cache"), "qmtui");

    /// <summary>
    /// 数据共享目录 (~/.local/share/qmtui)
    /// </summary>
    public static string DataDir { get; } = EnsureDirectory(Path.Combine(".local", "share"), "qmtui");

    /// <summary>
    /// 日志目录
    /// </summary>
    public static string LogDir { get; } = ResolveLogDir();

    private static string ResolveLogDir()
    {
        var logDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qmtui", "logs")
            : Path.Combine(CacheDir, "logs");

        try
        {
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
        catch
        {
        }

        return logDir;
    }

    private static string EnsureDirectory(string parentRel, string dirName)
    {
        var dir = Path.Combine(s_userHome, parentRel, dirName);

        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch
        {
        }

        return dir;
    }
}
