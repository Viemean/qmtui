using System;
using System.Text.RegularExpressions;

namespace QmTui.Utils;

/// <summary>
/// 音频曲目元数据与标题文本处理辅助类
/// </summary>
public static partial class MusicTitleHelper
{
    [GeneratedRegex(@"^(?:\[?\d{1,3}\]?[\.\-_\s]+)(.+)")]
    private static partial Regex TrackNumberPrefixRegex();

    /// <summary>
    /// 剥离音轨序号前缀（如 "01. ", "02 - ", "01 ", "[01] ", "(01) " 等）
    /// </summary>
    /// <param name="raw">待清洗的原始标题或文件名</param>
    /// <returns>剥离音轨号后的干净标题</returns>
    public static string CleanTrackNumberPrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim();
        var match = TrackNumberPrefixRegex().Match(s);
        if (match.Success && match.Groups.Count > 1)
        {
            var cleaned = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(cleaned))
            {
                return cleaned;
            }
        }
        return s;
    }
}
