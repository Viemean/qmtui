using System.IO;

namespace QmTui.Utils;

/// <summary>
/// 全局路径管理帮助类（全面使用 ~/.config/qmtui, ~/.cache/qmtui, ~/.local/share/qmtui，自动无缝迁移旧配置）
/// </summary>
public static class AppPathHelper
{
    private static readonly string s_userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 配置文件目录 (~/.config/qmtui)
    /// </summary>
    public static string ConfigDir { get; } = ResolveAndMigrateDir(Path.Combine(".config"), "qmtui", "qmtui");

    /// <summary>
    /// 缓存目录 (~/.cache/qmtui)
    /// </summary>
    public static string CacheDir { get; } = ResolveAndMigrateDir(Path.Combine(".cache"), "qmtui", "qmtui");

    /// <summary>
    /// 数据共享目录 (~/.local/share/qmtui)
    /// </summary>
    public static string DataDir { get; } = ResolveAndMigrateDir(Path.Combine(".local", "share"), "qmtui", "qmtui");

    private static string ResolveAndMigrateDir(string parentRel, string newName, string legacyName)
    {
        var newDir = Path.Combine(s_userHome, parentRel, newName);
        var legacyDir = Path.Combine(s_userHome, parentRel, legacyName);

        try
        {
            if (!Directory.Exists(newDir))
            {
                if (Directory.Exists(legacyDir))
                {
                    // 仅当新目录尚未存在时，从旧目录执行一次性迁移
                    MigrateDirectory(legacyDir, newDir);
                }
                else
                {
                    Directory.CreateDirectory(newDir);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AppPathHelper", $"自动迁移或创建目录失败: {newDir} (原目录: {legacyDir})", ex);
        }

        return newDir;
    }

    private static void MigrateDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(targetDir, fileName);
            if (!File.Exists(destFile))
            {
                File.Copy(file, destFile, overwrite: false);
            }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            var destSubDir = Path.Combine(targetDir, dirName);
            MigrateDirectory(subDir, destSubDir);
        }
    }
}
