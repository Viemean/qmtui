using System.Reflection;
using System.Text;

namespace QmTui.Utils;

/// <summary>
/// 静态 Web 资源加载器：支持磁盘外部热重载与二进制内置程序集保底
/// </summary>
public static class StaticResourceHelper
{
    private static readonly Assembly s_assembly = typeof(StaticResourceHelper).Assembly;

    /// <summary>
    /// 读取静态 Web 文件内容（优先物理磁盘，缺失时自动回退至二进制内嵌资源）。
    /// </summary>
    /// <param name="fileName">相对静态资源目录的文件名称。</param>
    /// <returns>读取到的文件文本；未找到或读取失败返回空字符串。</returns>
    public static string LoadStaticText(string fileName)
    {
        var filePath = ResolveDiskFilePath(fileName);
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try
            {
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StaticResourceHelper", $"Failed to read {filePath}: {ex.Message}");
            }
        }

        return LoadEmbeddedText(fileName);
    }

    /// <summary>
    /// 从当前程序集资源流中读取内嵌文本。
    /// </summary>
    /// <param name="fileName">内嵌资源文件名称。</param>
    /// <returns>内嵌文本内容；读取失败返回空字符串。</returns>
    public static string LoadEmbeddedText(string fileName)
    {
        var resourceName = $"QmTui.www.{fileName}";
        try
        {
            using var stream = s_assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("StaticResourceHelper", $"Failed to load embedded resource: {resourceName}", ex);
        }

        return "";
    }

    /// <summary>
    /// 解析外部物理磁盘文件绝对路径。
    /// </summary>
    /// <param name="fileName">相对静态资源目录的文件名称。</param>
    /// <returns>存在时的物理绝对路径；不存在返回 <see langword="null"/>。</returns>
    public static string? ResolveDiskFilePath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var p1 = Path.Combine(baseDir, "www", fileName);
        if (File.Exists(p1)) return p1;

        var curDir = Directory.GetCurrentDirectory();
        var p2 = Path.Combine(curDir, "www", fileName);
        if (File.Exists(p2)) return p2;

        var p3 = Path.Combine("/usr/share/qmtui/www", fileName);
        if (File.Exists(p3)) return p3;

        var p4 = Path.Combine("/usr/share/qqmusic-tui/www", fileName);
        if (File.Exists(p4)) return p4;

        return null;
    }
}
