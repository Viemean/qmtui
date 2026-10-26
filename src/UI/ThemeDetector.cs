using System;
using System.IO;
using System.Text.RegularExpressions;

namespace QmTui.UI;

/// <summary>
/// 终端与系统环境主题明暗检测器。
/// </summary>
internal static partial class ThemeDetector
{
    /// <summary>
    /// 检测当前运行环境是否处于浅色背景模式。
    /// </summary>
    /// <returns>若检测为浅色背景返回 true，否则返回 false。</returns>
    public static bool DetectIsLight()
    {
        try
        {
            // 检查终端环境变量 COLORFGBG (格式通常为 "fg;bg" 或 "fg;...;bg")
            var colorFgBg = Environment.GetEnvironmentVariable("COLORFGBG");
            if (!string.IsNullOrEmpty(colorFgBg))
            {
                var parts = colorFgBg.Split(';');
                if (parts.Length >= 2 && int.TryParse(parts[^1], out var bgIndex))
                {
                    // ANSI 16 色标准：0-6 及 8 为深色底；7、9-15 为浅色底
                    if (bgIndex >= 7 && bgIndex != 8)
                    {
                        return true;
                    }
                    return false;
                }
            }

            // Kitty 终端环境探测
            var isKitty = Environment.GetEnvironmentVariable("KITTY_PID") != null ||
                          Environment.GetEnvironmentVariable("KITTY_WINDOW_ID") != null ||
                          string.Equals(Environment.GetEnvironmentVariable("TERM"), "xterm-kitty", StringComparison.OrdinalIgnoreCase);
            if (isKitty)
            {
                if (TryDetectKittyBackground(out bool isKittyLight))
                {
                    return isKittyLight;
                }
            }

            // Alacritty 终端环境探测
            var isAlacritty = Environment.GetEnvironmentVariable("ALACRITTY_WINDOW_ID") != null ||
                              Environment.GetEnvironmentVariable("ALACRITTY_LOG") != null;
            if (isAlacritty)
            {
                if (TryDetectAlacrittyBackground(out bool isAlacrittyLight))
                {
                    return isAlacrittyLight;
                }
            }

            // Linux 桌面外观偏好检测 (gsettings)
            if (OperatingSystem.IsLinux())
            {
                if (TryDetectDesktopAppearance(out bool isDesktopLight))
                {
                    return isDesktopLight;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryDetectKittyBackground(out bool isLight)
    {
        isLight = false;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var confPath = Path.Combine(home, ".config", "kitty", "kitty.conf");
            if (!File.Exists(confPath))
            {
                return false;
            }

            var text = File.ReadAllText(confPath);
            var match = KittyBgRegex().Match(text);
            if (match.Success)
            {
                var hex = match.Groups[1].Value;
                if (hex.Length == 6)
                {
                    int r = Convert.ToInt32(hex[0..2], 16);
                    int g = Convert.ToInt32(hex[2..4], 16);
                    int b = Convert.ToInt32(hex[4..6], 16);
                    double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                    isLight = luminance > 128;
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryDetectAlacrittyBackground(out bool isLight)
    {
        isLight = false;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tomlPath = Path.Combine(home, ".config", "alacritty", "alacritty.toml");
            if (File.Exists(tomlPath))
            {
                var text = File.ReadAllText(tomlPath);
                var match = AlacrittyBgRegex().Match(text);
                if (match.Success)
                {
                    var hex = match.Groups[1].Value.TrimStart('#');
                    if (hex.Length == 6)
                    {
                        int r = Convert.ToInt32(hex[0..2], 16);
                        int g = Convert.ToInt32(hex[2..4], 16);
                        int b = Convert.ToInt32(hex[4..6], 16);
                        double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
                        isLight = luminance > 128;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryDetectDesktopAppearance(out bool isLight)
    {
        isLight = false;
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = "gsettings";
            proc.StartInfo.Arguments = "get org.gnome.desktop.interface color-scheme";
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            if (proc.Start())
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(500);
                if (output.Contains("prefer-light", StringComparison.OrdinalIgnoreCase))
                {
                    isLight = true;
                    return true;
                }
                if (output.Contains("prefer-dark", StringComparison.OrdinalIgnoreCase))
                {
                    isLight = false;
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    [GeneratedRegex(@"^\s*background\s+#?([0-9a-fA-F]{6})", RegexOptions.Multiline)]
    private static partial Regex KittyBgRegex();

    [GeneratedRegex(@"background\s*=\s*['""]#?([0-9a-fA-F]{6})['""]", RegexOptions.Multiline)]
    private static partial Regex AlacrittyBgRegex();
}
