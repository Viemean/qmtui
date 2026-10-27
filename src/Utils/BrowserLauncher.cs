using System.Diagnostics;

namespace QmTui.Utils;

/// <summary>
/// 跨平台默认浏览器唤起辅助工具
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// 使用系统默认浏览器打开指定 URL
    /// </summary>
    /// <param name="url">目标网页地址</param>
    /// <returns>若成功发起唤起返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start \"\" \"{url}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    return true;
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"\"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }

            // Linux: 优先尝试 xdg-open，回退通用 Shell 关联启动
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }
            catch
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("BrowserLauncher", $"无法通过默认浏览器打开 URL '{url}': {ex.Message}");
            return false;
        }
    }
}
