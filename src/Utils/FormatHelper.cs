namespace QmTui.Utils;

/// <summary>
/// 通用数据与度量值格式化辅助类
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// 将字节数格式化为人类可读的字符串（B, KB, MB, GB）
    /// </summary>
    /// <param name="bytes">字节数</param>
    /// <returns>格式化后的文件大小字符串；若小于等于 0 则返回空字符串</returns>
    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F1}KB";
        return $"{bytes}B";
    }
}
