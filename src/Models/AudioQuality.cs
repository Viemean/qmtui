using Terminal.Gui.Text;

namespace QmTui.Models;

public enum AudioQualityTier
{
    HiRes = 0,
    SQ = 1,
    HQ = 2,
    Standard = 3,
    Master = 4,
    Premium = 5,
    Atmos51 = 6,
    Atmos71 = 7,
    Dolby = 8
}

public static class AudioQualityHelper
{
    public static string GetBadge(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "母带",
        AudioQualityTier.Premium => "臻品",
        AudioQualityTier.Atmos51 => "5.1",
        AudioQualityTier.Atmos71 => "7.1",
        AudioQualityTier.Dolby => "杜比",
        AudioQualityTier.HiRes => "Hi-Res",
        AudioQualityTier.SQ => "SQ",
        AudioQualityTier.HQ => "HQ",
        AudioQualityTier.Standard => "标准",
        _ => "标准"
    };

    public static string GetPrefix(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "AI00",
        AudioQualityTier.Premium => "Q000",
        AudioQualityTier.Atmos51 => "Q001",
        AudioQualityTier.Atmos71 => "Q001",
        AudioQualityTier.Dolby => "Q000",
        AudioQualityTier.HiRes => "RS01",
        AudioQualityTier.SQ => "F000",
        AudioQualityTier.HQ => "M800",
        AudioQualityTier.Standard => "M500",
        _ => "M500"
    };

    public static string GetExtension(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => ".flac",
        AudioQualityTier.Premium => ".flac",
        AudioQualityTier.Atmos51 => ".flac",
        AudioQualityTier.Atmos71 => ".flac",
        AudioQualityTier.Dolby => ".flac",
        AudioQualityTier.HiRes => ".flac",
        AudioQualityTier.SQ => ".flac",
        AudioQualityTier.HQ => ".mp3",
        AudioQualityTier.Standard => ".mp3",
        _ => ".mp3"
    };

    public static (string Key, AudioQualityTier Tier, string Prefix, string Extension)[] ProbeRequests { get; } =
    [
        ("req_master", AudioQualityTier.Master, "AI00", ".flac"),
        ("req_premium", AudioQualityTier.Premium, "Q000", ".flac"),
        ("req_atmos51", AudioQualityTier.Atmos51, "Q001", ".flac"),
        ("req_atmos71", AudioQualityTier.Atmos71, "Q001", ".flac"),
        ("req_dolby", AudioQualityTier.Dolby, "Q000", ".flac"),
        ("req_hires", AudioQualityTier.HiRes, "RS01", ".flac"),
        ("req_sq", AudioQualityTier.SQ, "F000", ".flac"),
        ("req_320", AudioQualityTier.HQ, "M800", ".mp3"),
        ("req_128", AudioQualityTier.Standard, "M500", ".mp3")
    ];

    public static string GetDefaultSpec(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "24bit / 192kHz",
        AudioQualityTier.Premium => "臻品音质",
        AudioQualityTier.Atmos51 => "5.1 声道",
        AudioQualityTier.Atmos71 => "7.1 全景声",
        AudioQualityTier.Dolby => "Dolby Atmos",
        AudioQualityTier.HiRes => "24bit / 96kHz",
        AudioQualityTier.SQ => "16bit / 44.1kHz",
        AudioQualityTier.HQ => "320kbps",
        AudioQualityTier.Standard => "128kbps",
        _ => "128kbps"
    };

    public static AudioQualityTier Parse(string? name)
    {
        if (string.IsNullOrEmpty(name)) return AudioQualityTier.SQ;
        if (name.Contains("母带", StringComparison.OrdinalIgnoreCase) || name.Contains("Master", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Master;
        if (name.Contains("5.1", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Atmos51;
        if (name.Contains("7.1", StringComparison.OrdinalIgnoreCase) || name.Contains("全景声", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Atmos71;
        if (name.Contains("杜比", StringComparison.OrdinalIgnoreCase) || name.Contains("Dolby", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Dolby;
        if (name.Contains("臻品", StringComparison.OrdinalIgnoreCase) || name.Contains("Premium", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.Premium;
        if (name.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HiRes;
        if (name.Contains("SQ", StringComparison.OrdinalIgnoreCase) || name.Contains("flac", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.SQ;
        if (name.Contains("HQ", StringComparison.OrdinalIgnoreCase) || name.Contains("320", StringComparison.OrdinalIgnoreCase)) return AudioQualityTier.HQ;
        return AudioQualityTier.Standard;
    }

    public static AudioQualityTier DetermineLocalOrWebDavTier(string? quality, string? filePath = null)
    {
        // 1. 若有实际文件路径，优先基于文件扩展名与编码特性判定，避免旧缓存默认的"标准 128k"造成误判
        if (!string.IsNullOrEmpty(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".flac" or ".ape" or ".wav" or ".aiff" or ".dsd" or ".dff" or ".dsf")
            {
                if (!string.IsNullOrEmpty(quality) && (quality.Contains("Hi-Res", StringComparison.OrdinalIgnoreCase) || quality.Contains("24bit", StringComparison.OrdinalIgnoreCase)))
                {
                    return AudioQualityTier.HiRes;
                }
                if (!string.IsNullOrEmpty(quality) && (quality.Contains("Master", StringComparison.OrdinalIgnoreCase) || quality.Contains("母带", StringComparison.OrdinalIgnoreCase)))
                {
                    return AudioQualityTier.Master;
                }
                return AudioQualityTier.SQ;
            }
            if (ext is ".m4a" or ".aac" or ".ogg" or ".opus")
            {
                return AudioQualityTier.HQ;
            }
            if (ext is ".mp3")
            {
                if (!string.IsNullOrEmpty(quality) && (quality.Contains("HQ", StringComparison.OrdinalIgnoreCase) || quality.Contains("320", StringComparison.OrdinalIgnoreCase)))
                {
                    return AudioQualityTier.HQ;
                }
                return AudioQualityTier.Standard;
            }
        }

        // 2. 若无路径或未匹配扩展名，则解析 quality 字符串
        if (!string.IsNullOrEmpty(quality))
        {
            return Parse(quality);
        }

        return AudioQualityTier.Standard;
    }

    public static IReadOnlyList<AudioQualityTier> SelectionOrder { get; } =
    [
        AudioQualityTier.Master,
        AudioQualityTier.Premium,
        AudioQualityTier.Atmos51,
        AudioQualityTier.Atmos71,
        AudioQualityTier.Dolby,
        AudioQualityTier.HiRes,
        AudioQualityTier.SQ,
        AudioQualityTier.HQ,
        AudioQualityTier.Standard
    ];

    public static int GetSelectionIndex(AudioQualityTier tier)
    {
        for (var i = 0; i < SelectionOrder.Count; i++)
        {
            if (SelectionOrder[i] == tier) return i;
        }
        return -1;
    }

    public static IReadOnlyList<AudioQualityTier> GetFallbackTiers(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => [AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.Premium or AudioQualityTier.Atmos51 or AudioQualityTier.Atmos71 or AudioQualityTier.Dolby =>
            [AudioQualityTier.HiRes, AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.HiRes => [AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.SQ => [AudioQualityTier.HQ, AudioQualityTier.Standard],
        AudioQualityTier.HQ => [AudioQualityTier.Standard],
        _ => []
    };

    public static string GetQualityName(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Master => "臻品母带",
        AudioQualityTier.Premium => "臻品音质",
        AudioQualityTier.Atmos51 => "臻品全景声 5.1",
        AudioQualityTier.Atmos71 => "臻品全景声 7.1",
        AudioQualityTier.Dolby => "杜比全景声",
        AudioQualityTier.HiRes => "Hi-Res",
        AudioQualityTier.SQ => "SQ 无损",
        AudioQualityTier.HQ => "HQ 高品质",
        _ => "标准音质"
    };
}

public record QualityOption(
    AudioQualityTier Tier,
    string Badge,
    string Name,
    string Spec,
    string BitrateInfo,
    bool Available,
    string? PlayUrl = null,
    long FileSizeBytes = 0
)
{
    public string DisplayText(bool isCurrent)
    {
        var mark = isCurrent ? " ✓" : "";
        var paddedName = PadRightDisplay(Name, 16);
        var paddedSpec = PadRightDisplay(Spec, 18);

        if (!Available)
        {
            return $"{paddedName}  {paddedSpec}  (无音源)";
        }

        var sizeStr = FormatFileSize(FileSizeBytes);
        var paddedSize = string.IsNullOrEmpty(sizeStr) ? "" : PadLeftDisplay(sizeStr, 8);
        var bitratePart = string.IsNullOrEmpty(BitrateInfo) ? "" : $"{{{BitrateInfo}}}";

        if (string.IsNullOrEmpty(paddedSize) && string.IsNullOrEmpty(bitratePart))
        {
            return $"{paddedName}  {paddedSpec}{mark}";
        }

        var sizeCol = string.IsNullOrEmpty(paddedSize) ? new string(' ', 8) : paddedSize;
        var bitrateCol = string.IsNullOrEmpty(bitratePart) ? "" : $"  {bitratePart}";
        return $"{paddedName}  {paddedSpec}  {sizeCol}{bitrateCol}{mark}";
    }

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

    private static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.GetColumns() > 0 ? rune.GetColumns() : 1;
        }
        return width;
    }

    private static string PadRightDisplay(string text, int targetWidth)
    {
        int w = GetDisplayWidth(text);
        int pad = targetWidth - w;
        return pad > 0 ? text + new string(' ', pad) : text;
    }

    private static string PadLeftDisplay(string text, int targetWidth)
    {
        int w = GetDisplayWidth(text);
        int pad = targetWidth - w;
        return pad > 0 ? new string(' ', pad) + text : text;
    }
}
