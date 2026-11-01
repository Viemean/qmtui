using System.Text;
using System.Text.RegularExpressions;
using QmTui.Models;

namespace QmTui.Utils;

public static partial class LyricParser
{
    /// <summary>
    /// Base64 安全解码为 UTF-8 文本。
    /// </summary>
    /// <param name="b64">待解码的 Base64 字符串。</param>
    /// <returns>解码后的 UTF-8 字符串；解码失败返回空字符串。</returns>
    public static string DecodeBase64(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return "";
        try
        {
            var bytes = Convert.FromBase64String(b64.Trim());
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LyricParser", $"Base64 解码失败: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// <summary>
    /// 解析标准及多时间戳 LRC 文本
    /// </summary>
    public static List<(TimeSpan Timestamp, string Text)> ParseLrc(string? lrcText)
    {
        var rawItems = ParseRawLrcItems(lrcText);
        if (rawItems.Count == 0) return [];

        var result = new List<(TimeSpan Timestamp, string Text)>(rawItems.Count);
        foreach (var item in rawItems)
        {
            var text = item.RawContent.Contains('<')
                ? EnhancedTimestampRegex().Replace(item.RawContent, "").Trim()
                : item.RawContent;
            result.Add((item.Timestamp, text));
        }
        return result;
    }

    private readonly record struct RawLrcItem(TimeSpan Timestamp, string RawContent, int Order);

    private static List<RawLrcItem> ParseRawLrcItems(string? lrcText)
    {
        if (string.IsNullOrWhiteSpace(lrcText)) return [];

        List<RawLrcItem> list = [];
        int lineOrder = 0;
        Span<TimeSpan> tsBuf = stackalloc TimeSpan[8];

        foreach (var line in lrcText.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;

            int matchCount = 0;
            List<TimeSpan>? tsOverflow = null;
            int lastMatchEnd = 0;
            bool matchesContiguousAtStart = true;

            foreach (var match in TimestampRegex().EnumerateMatches(trimmed))
            {
                var matchSpan = trimmed.Slice(match.Index, match.Length);
                if (TryParseTimestampSpan(matchSpan, out var ts))
                {
                    if (matchCount < tsBuf.Length)
                    {
                        tsBuf[matchCount] = ts;
                    }
                    else
                    {
                        tsOverflow ??= new List<TimeSpan>(4);
                        tsOverflow.Add(ts);
                    }
                    matchCount++;

                    if (match.Index != lastMatchEnd)
                    {
                        matchesContiguousAtStart = false;
                    }
                    lastMatchEnd = match.Index + match.Length;
                }
            }

            if (matchCount == 0) continue;

            string content;
            if (matchesContiguousAtStart)
            {
                var contentSpan = trimmed[lastMatchEnd..].Trim();
                if (contentSpan.IsEmpty || contentSpan.SequenceEqual("//")) continue;

                if (contentSpan.Contains("&apos;", StringComparison.Ordinal))
                {
                    content = contentSpan.ToString().Replace("&apos;", "’");
                }
                else
                {
                    content = contentSpan.ToString();
                }
            }
            else
            {
                content = TimestampRegex().Replace(trimmed.ToString(), "").Trim().Replace("&apos;", "’");
                if (string.IsNullOrEmpty(content) || content == "//") continue;
            }

            int directCount = Math.Min(matchCount, tsBuf.Length);
            for (int i = 0; i < directCount; i++)
            {
                list.Add(new RawLrcItem(tsBuf[i], content, lineOrder++));
            }
            if (tsOverflow != null)
            {
                for (int i = 0; i < tsOverflow.Count; i++)
                {
                    list.Add(new RawLrcItem(tsOverflow[i], content, lineOrder++));
                }
            }
        }

        list.Sort((a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        return list;
    }

    private static bool TryParseTimestampSpan(ReadOnlySpan<char> span, out TimeSpan ts)
    {
        ts = default;
        if (span.Length < 6 || span[0] != '[' || span[^1] != ']') return false;

        var inner = span[1..^1];
        int colon = inner.IndexOf(':');
        if (colon <= 0 || !int.TryParse(inner[..colon], out int min)) return false;

        var secPart = inner[(colon + 1)..];
        int dot = secPart.IndexOfAny('.', ':');
        int sec, ms = 0;
        if (dot < 0)
        {
            if (!int.TryParse(secPart, out sec)) return false;
        }
        else
        {
            if (!int.TryParse(secPart[..dot], out sec)) return false;
            var msSpan = secPart[(dot + 1)..];
            if (msSpan.Length == 1)
            {
                if (!char.IsAsciiDigit(msSpan[0])) return false;
                ms = (msSpan[0] - '0') * 100;
            }
            else if (msSpan.Length == 2)
            {
                if (!int.TryParse(msSpan, out ms)) return false;
                ms *= 10;
            }
            else if (msSpan.Length >= 3)
            {
                if (!int.TryParse(msSpan[..3], out ms)) return false;
            }
        }

        ts = new TimeSpan(0, 0, min, sec, ms);
        return true;
    }

    /// <summary>
    /// 解析本地 LRC 文本为 LyricLine 列表（识别增强型 LRC 逐字时间戳与同时间戳双语原歌词与翻译）。
    /// </summary>
    /// <param name="lrcText">原始 LRC 文本内容。</param>
    /// <returns>结构化歌词行列表；若内容为空返回空列表。</returns>
    public static List<LyricLine> ParseSingleLrc(string? lrcText)
    {
        var rawItems = ParseRawLrcItems(lrcText);
        if (rawItems.Count == 0) return [];

        List<(TimeSpan Timestamp, string Text, List<WordSpan>? Words)> items = [];
        foreach (var raw in rawItems)
        {
            long lineMs = (long)raw.Timestamp.TotalMilliseconds;
            if (TryParseEnhancedWords(lineMs, raw.RawContent, out var cleanText, out var words))
            {
                items.Add((raw.Timestamp, cleanText, words));
            }
            else
            {
                var clean = raw.RawContent.Contains('<')
                    ? EnhancedTimestampRegex().Replace(raw.RawContent, "").Trim()
                    : raw.RawContent;
                items.Add((raw.Timestamp, clean, null));
            }
        }

        var result = new List<LyricLine>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var curr = items[i];

            // 查找所有与当前行时间戳相同（<= 50ms）的候选行
            int j = i + 1;
            while (j < items.Count && Math.Abs((items[j].Timestamp - curr.Timestamp).TotalMilliseconds) <= 50)
            {
                j++;
            }

            // 若同一时间戳有多行且不含制作人员/标题元信息，进行双语对齐归并
            if (j > i + 1)
            {
                bool anyMeta = false;
                for (int k = i; k < j; k++)
                {
                    if (IsMetaInfoLine(items[k].Timestamp, items[k].Text))
                    {
                        anyMeta = true;
                        break;
                    }
                }

                if (!anyMeta)
                {
                    int transIdx = i + 1;
                    if (j - i > 2)
                    {
                        for (int k = i + 1; k < j; k++)
                        {
                            if (HasChinese(items[k].Text))
                            {
                                transIdx = k;
                                break;
                            }
                        }
                    }

                    var transText = items[transIdx].Text;
                    result.Add(new LyricLine(curr.Timestamp, curr.Text, transText, curr.Words));
                    i = j - 1;
                    continue;
                }
            }

            result.Add(new LyricLine(curr.Timestamp, curr.Text, "", curr.Words));
        }

        return result;
    }

    public static bool TryParseEnhancedWords(
        long lineTimestampMs,
        string rawContent,
        out string cleanText,
        out List<WordSpan>? words)
    {
        words = null;
        cleanText = rawContent;
        if (string.IsNullOrWhiteSpace(rawContent) || !rawContent.Contains('<'))
        {
            return false;
        }

        var matches = EnhancedTimestampRegex().Matches(rawContent);
        if (matches.Count == 0)
        {
            return false;
        }

        List<WordSpan> wordList = [];
        var sbText = new StringBuilder();

        int firstIndex = matches[0].Index;
        if (firstIndex > 0)
        {
            string prefix = rawContent[..firstIndex].TrimStart();
            if (!string.IsNullOrEmpty(prefix))
            {
                sbText.Append(prefix);
                long firstTs = ParseEnhancedTimestampToMs(matches[0]);
                long dur = Math.Max(0, firstTs - lineTimestampMs);
                wordList.Add(new WordSpan(prefix, 0, dur));
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            long currentTs = ParseEnhancedTimestampToMs(match);
            long relOffset = Math.Max(0, currentTs - lineTimestampMs);

            int wordStart = match.Index + match.Length;
            int wordEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : rawContent.Length;

            string word = (wordEnd > wordStart) ? rawContent[wordStart..wordEnd] : "";
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            long durMs;
            if (i + 1 < matches.Count)
            {
                long nextTs = ParseEnhancedTimestampToMs(matches[i + 1]);
                durMs = Math.Max(50, nextTs - currentTs);
            }
            else
            {
                durMs = 500;
            }

            wordList.Add(new WordSpan(word, relOffset, durMs));
            sbText.Append(word);
        }

        if (wordList.Count > 0)
        {
            words = wordList;
            cleanText = sbText.ToString();
            return true;
        }

        return false;
    }

    private static long ParseEnhancedTimestampToMs(Match match)
    {
        int min = int.TryParse(match.Groups[1].Value, out var m) ? m : 0;
        int sec = int.TryParse(match.Groups[2].Value, out var s) ? s : 0;
        int ms = 0;
        if (match.Groups[3].Success)
        {
            var msStr = match.Groups[3].Value;
            if (msStr.Length == 1) ms = (msStr[0] - '0') * 100;
            else if (msStr.Length == 2) ms = int.Parse(msStr) * 10;
            else if (msStr.Length >= 3) ms = int.Parse(msStr[..3]);
        }
        return min * 60_000L + sec * 1_000L + ms;
    }

    private static bool HasChinese(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (c >= 0x4e00 && c <= 0x9fa5) return true;
        }
        return false;
    }

    /// <summary>
    /// 将原语言歌词与翻译歌词按时间戳对齐合并。
    /// </summary>
    /// <param name="rawLyric">原始语言歌词文本。</param>
    /// <param name="rawTrans">翻译语言歌词文本。</param>
    /// <returns>合并对齐后的歌词行列表。</returns>
    public static List<LyricLine> MergeLyrics(string rawLyric, string rawTrans)
    {
        if (IsQrcContent(rawLyric))
        {
            var qrcLines = ParseQrcLyrics(rawLyric);
            if (qrcLines.Count == 0)
            {
                qrcLines = [new LyricLine(TimeSpan.Zero, "暂无歌词")];
            }

            var transList = ParseLrc(rawTrans);
            var cleanTList = transList.FindAll(t =>
                !string.IsNullOrWhiteSpace(t.Text) &&
                t.Text != "//" &&
                !t.Text.Contains("享有") &&
                !t.Text.Contains("大模型") &&
                !t.Text.Contains("翻译贡献")
            );

            var qrcUsedSet = new HashSet<(TimeSpan, string)>(cleanTList.Count);
            var qrcResult = new List<LyricLine>(qrcLines.Count);

            for (int i = 0; i < qrcLines.Count; i++)
            {
                var qrcLine = qrcLines[i];
                string transText = "";
                bool isMeta = IsMetaInfoLine(qrcLine.Timestamp, qrcLine.Text);

                if (!isMeta && transList.Count > 0)
                {
                    var exact = transList.Find(t => Math.Abs((t.Timestamp - qrcLine.Timestamp).TotalMilliseconds) < 80);
                    if (exact.Text != null && !qrcUsedSet.Contains(exact) &&
                        exact.Text != "//" && !exact.Text.Contains("享有") && !exact.Text.Contains("大模型") && !exact.Text.Contains("翻译贡献"))
                    {
                        transText = exact.Text;
                        qrcUsedSet.Add(exact);
                    }
                    else if (cleanTList.Count > 0)
                    {
                        var candidates = cleanTList.FindAll(t => !qrcUsedSet.Contains(t) && Math.Abs((t.Timestamp - qrcLine.Timestamp).TotalMilliseconds) <= 300);
                        if (candidates.Count > 0)
                        {
                            var best = candidates[0];
                            var minDiff = Math.Abs((best.Timestamp - qrcLine.Timestamp).TotalMilliseconds);
                            for (int c = 1; c < candidates.Count; c++)
                            {
                                var diff = Math.Abs((candidates[c].Timestamp - qrcLine.Timestamp).TotalMilliseconds);
                                if (diff < minDiff)
                                {
                                    minDiff = diff;
                                    best = candidates[c];
                                }
                            }
                            transText = best.Text;
                            qrcUsedSet.Add(best);
                        }
                        else if (cleanTList.Count == qrcLines.Count && i < cleanTList.Count && !qrcUsedSet.Contains(cleanTList[i]))
                        {
                            transText = cleanTList[i].Text;
                            qrcUsedSet.Add(cleanTList[i]);
                        }
                    }
                }

                qrcResult.Add(new LyricLine(qrcLine.Timestamp, qrcLine.Text, transText, qrcLine.Words));
            }

            return qrcResult;
        }

        var origItems = ParseLrc(rawLyric);
        if (origItems.Count == 0)
        {
            origItems = [(TimeSpan.Zero, "暂无歌词")];
        }

        var transItems = ParseLrc(rawTrans);
        var cleanTrans = transItems.FindAll(t =>
            !string.IsNullOrWhiteSpace(t.Text) &&
            t.Text != "//" &&
            !t.Text.Contains("享有") &&
            !t.Text.Contains("大模型") &&
            !t.Text.Contains("翻译贡献")
        );

        var usedSet = new HashSet<(TimeSpan, string)>(cleanTrans.Count);
        var result = new List<LyricLine>(origItems.Count);

        for (int i = 0; i < origItems.Count; i++)
        {
            var orig = origItems[i];
            string transText = "";

            bool isMeta = IsMetaInfoLine(orig.Timestamp, orig.Text);

            if (!isMeta && transItems.Count > 0)
            {
                // 精确匹配 (< 80ms)
                var exact = transItems.Find(t => Math.Abs((t.Timestamp - orig.Timestamp).TotalMilliseconds) < 80);
                if (exact.Text != null && !usedSet.Contains(exact) &&
                    exact.Text != "//" && !exact.Text.Contains("享有") && !exact.Text.Contains("大模型") && !exact.Text.Contains("翻译贡献"))
                {
                    transText = exact.Text;
                    usedSet.Add(exact);
                }
                else if (cleanTrans.Count > 0)
                {
                    // 300ms 紧凑容差候选匹配，取时间距离最小的候选
                    var candidates = cleanTrans.FindAll(t => !usedSet.Contains(t) && Math.Abs((t.Timestamp - orig.Timestamp).TotalMilliseconds) <= 300);
                    if (candidates.Count > 0)
                    {
                        var best = candidates[0];
                        var minDiff = Math.Abs((best.Timestamp - orig.Timestamp).TotalMilliseconds);
                        for (int c = 1; c < candidates.Count; c++)
                        {
                            var diff = Math.Abs((candidates[c].Timestamp - orig.Timestamp).TotalMilliseconds);
                            if (diff < minDiff)
                            {
                                minDiff = diff;
                                best = candidates[c];
                            }
                        }
                        transText = best.Text;
                        usedSet.Add(best);
                    }
                    // 总行数完全一致时的同索引保底回退
                    else if (cleanTrans.Count == origItems.Count && i < cleanTrans.Count && !usedSet.Contains(cleanTrans[i]))
                    {
                        transText = cleanTrans[i].Text;
                        usedSet.Add(cleanTrans[i]);
                    }
                }
            }

            result.Add(new LyricLine(orig.Timestamp, orig.Text, transText));
        }

        return result;
    }

    /// <summary>
    /// 判断是否为无需挂载歌词翻译的标题行或幕后制作人员元信息行
    /// </summary>
    private static bool IsMetaInfoLine(TimeSpan ts, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();

        // 0 秒且包含连字符的标题行（例如 "Breathe - Machico (マチコ)/LIN"）
        if (ts.TotalSeconds <= 0.05 && t.Contains(" - "))
        {
            return true;
        }

        // 制作人员常见标识
        if (t.StartsWith("词：") || t.StartsWith("词:") || t.StartsWith("作词：") || t.StartsWith("作词:") ||
            t.StartsWith("曲：") || t.StartsWith("曲:") || t.StartsWith("作曲：") || t.StartsWith("作曲:") ||
            t.StartsWith("编曲：") || t.StartsWith("编曲:") || t.StartsWith("制作：") || t.StartsWith("制作:") ||
            t.StartsWith("制作人：") || t.StartsWith("制作人:") || t.StartsWith("监制：") || t.StartsWith("监制:") ||
            t.StartsWith("混音：") || t.StartsWith("混音:") || t.StartsWith("录音：") || t.StartsWith("录音:") ||
            t.StartsWith("母带：") || t.StartsWith("母带:") || t.StartsWith("出品：") || t.StartsWith("出品:") ||
            t.StartsWith("企划：") || t.StartsWith("企划:") || t.StartsWith("OP：") || t.StartsWith("OP:") ||
            t.StartsWith("SP：") || t.StartsWith("SP:"))
        {
            return true;
        }

        return false;
    }

    private static readonly string[] s_placeholderKeywords =
    [
        "纯音乐",
        "没有填词",
        "暂无歌词",
        "请您欣赏",
        "请欣赏",
        "instrumental",
        "no lyrics"
    ];

    /// <summary>
    /// 判断歌词是否为纯音乐、暂无歌词等无效占位内容。
    /// </summary>
    /// <param name="lyrics">待检查的歌词行序列。</param>
    /// <returns>若属于占位歌词返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool IsPlaceholderLyrics(IEnumerable<LyricLine>? lyrics)
    {
        if (lyrics == null) return true;

        var validLines = lyrics
            .Where(l => !IsMetaInfoLine(l.Timestamp, l.Text) && !string.IsNullOrWhiteSpace(l.Text))
            .ToList();

        if (validLines.Count == 0) return true;

        if (validLines.Count <= 2)
        {
            var combined = string.Join(" ", validLines.Select(l => l.Text));
            foreach (var kw in s_placeholderKeywords)
            {
                if (combined.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 判断当前歌词列表中是否包含有效翻译。
    /// </summary>
    /// <param name="lyrics">待检查的歌词行序列。</param>
    /// <returns>若存在有效翻译返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool HasTranslation(IEnumerable<LyricLine>? lyrics)
    {
        if (lyrics == null) return false;
        foreach (var l in lyrics)
        {
            if (!string.IsNullOrWhiteSpace(l.Trans)) return true;
        }
        return false;
    }

    /// <summary>
    /// 判断歌词是否主要为外文（日韩文或英文等），确实需要中文辅助翻译。
    /// </summary>
    /// <param name="lyrics">待检查的歌词行序列。</param>
    /// <returns>若外文字符显著多于中文字符返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool NeedsTranslation(IEnumerable<LyricLine>? lyrics)
    {
        if (lyrics == null) return false;
        int hanziCount = 0;
        int foreignCharCount = 0;

        foreach (var line in lyrics)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            if (IsMetaInfoLine(line.Timestamp, line.Text)) continue;

            foreach (var ch in line.Text)
            {
                // 日文假名 (平假名 \u3040-\u309F, 片假名 \u30A0-\u30FF)
                if (ch >= 0x3040 && ch <= 0x30FF)
                {
                    return true;
                }
                // 韩文音节 (\uAC00-\uD7AF)
                if (ch >= 0xAC00 && ch <= 0xD7AF)
                {
                    return true;
                }
                // 汉字
                if (ch >= 0x4E00 && ch <= 0x9FA5)
                {
                    hanziCount++;
                }
                // 拉丁字母
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                {
                    foreignCharCount++;
                }
            }
        }

        // 若通篇拉丁字母数量远超汉字（如英文歌曲），判定为外文歌需要翻译
        return foreignCharCount > 20 && foreignCharCount > hanziCount * 2;
    }

    /// <summary>
    /// 判断当前歌词列表中是否包含词级逐字时间戳同步数据。
    /// </summary>
    /// <param name="lyrics">待检查的歌词行序列。</param>
    /// <returns>若存在至少一行有效 Words 逐字信息返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool HasWordLevelSync(IEnumerable<LyricLine>? lyrics)
    {
        if (lyrics == null) return false;
        foreach (var l in lyrics)
        {
            if (l.Words != null && l.Words.Count > 0) return true;
        }
        return false;
    }

    public static bool IsQrcContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<QrcInfos", StringComparison.OrdinalIgnoreCase) ||
               QrcMsRegex().IsMatch(trimmed);
    }

    public static string ExtractQrcLyricContent(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "";
        var trimmed = rawText.Trim();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("<QrcInfos", StringComparison.OrdinalIgnoreCase))
        {
            var match = QrcLyricContentRegex().Match(trimmed);
            if (match.Success)
            {
                return match.Groups[1].Value
                    .Replace("&apos;", "'")
                    .Replace("&quot;", "\"")
                    .Replace("&gt;", ">")
                    .Replace("&lt;", "<")
                    .Replace("&amp;", "&");
            }
        }
        return rawText;
    }

    public static List<LyricLine> ParseQrcLyrics(string? qrcText)
    {
        var content = ExtractQrcLyricContent(qrcText);
        if (string.IsNullOrWhiteSpace(content)) return [];

        List<LyricLine> result = [];
        foreach (var line in content.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;
            var parsed = ParseQrcLine(trimmed.ToString());
            if (parsed != null)
            {
                result.Add(parsed);
            }
        }
        return result;
    }

    public static LyricLine? ParseQrcLine(string rawLine)
    {
        var msMatch = QrcMsRegex().Match(rawLine);
        long lineTimestampMs;
        string afterTimestamp;

        if (msMatch.Success && msMatch.Index == 0)
        {
            lineTimestampMs = long.TryParse(msMatch.Groups[1].Value, out var msVal) ? msVal : 0L;
            afterTimestamp = rawLine[(msMatch.Index + msMatch.Length)..];
        }
        else
        {
            var match = TimestampRegex().Match(rawLine);
            if (!match.Success) return null;
            int min = int.TryParse(match.Groups[1].Value, out var mVal) ? mVal : 0;
            int sec = int.TryParse(match.Groups[2].Value, out var sVal) ? sVal : 0;
            var msStr = match.Groups[3].Success ? match.Groups[3].Value.PadRight(3, '0')[..3] : "000";
            int ms = int.TryParse(msStr, out var msV) ? msV : 0;
            lineTimestampMs = min * 60_000L + sec * 1_000L + ms;
            afterTimestamp = rawLine[(match.Index + match.Length)..];
        }

        bool isLeadingStyle = afterTimestamp.StartsWith('(');
        if (isLeadingStyle)
        {
            var matches = QrcLeadingRegex().Matches(afterTimestamp);
            if (matches.Count == 0)
            {
                var text = afterTimestamp.Trim();
                return string.IsNullOrEmpty(text) ? null : new LyricLine(TimeSpan.FromMilliseconds(lineTimestampMs), text);
            }

            var words = new List<WordSpan>(matches.Count);
            var sb = new StringBuilder();
            foreach (Match wm in matches)
            {
                long offsetOrAbs = long.TryParse(wm.Groups[1].Value, out var oVal) ? oVal : 0L;
                long dur = long.TryParse(wm.Groups[2].Value, out var dVal) ? dVal : 0L;
                string word = wm.Groups[3].Value;
                long relOffset = offsetOrAbs >= lineTimestampMs ? offsetOrAbs - lineTimestampMs : offsetOrAbs;
                words.Add(new WordSpan(word, relOffset, dur));
                sb.Append(word);
            }

            return new LyricLine(TimeSpan.FromMilliseconds(lineTimestampMs), sb.ToString(), "", words);
        }
        else
        {
            var tagMatches = QrcTagRegex().Matches(afterTimestamp);
            if (tagMatches.Count == 0)
            {
                var text = afterTimestamp.Trim();
                return string.IsNullOrEmpty(text) ? null : new LyricLine(TimeSpan.FromMilliseconds(lineTimestampMs), text);
            }

            var words = new List<WordSpan>(tagMatches.Count);
            var sb = new StringBuilder();
            int lastEnd = 0;
            foreach (Match tm in tagMatches)
            {
                string word = afterTimestamp[lastEnd..tm.Index];
                long offsetOrAbs = long.TryParse(tm.Groups[1].Value, out var oVal) ? oVal : 0L;
                long dur = long.TryParse(tm.Groups[2].Value, out var dVal) ? dVal : 0L;
                long relOffset = offsetOrAbs >= lineTimestampMs ? offsetOrAbs - lineTimestampMs : offsetOrAbs;
                if (!string.IsNullOrEmpty(word))
                {
                    words.Add(new WordSpan(word, relOffset, dur));
                    sb.Append(word);
                }
                lastEnd = tm.Index + tm.Length;
            }

            return new LyricLine(TimeSpan.FromMilliseconds(lineTimestampMs), sb.ToString(), "", words);
        }
    }

    [GeneratedRegex(@"\[(\d{1,2}):(\d{1,2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\[(\d+),(\d+)\]")]
    private static partial Regex QrcMsRegex();

    [GeneratedRegex(@"\((\d+),(\d+)\)([^(]*)")]
    private static partial Regex QrcLeadingRegex();

    [GeneratedRegex(@"\((\d+),(\d+)\)")]
    private static partial Regex QrcTagRegex();

    [GeneratedRegex(@"LyricContent=""(.*)""(?:\s*/>|\s*>)", RegexOptions.Singleline)]
    private static partial Regex QrcLyricContentRegex();

    [GeneratedRegex(@"<(\d{1,2}):(\d{1,2})(?:[\.:](\d{1,3}))?>")]
    private static partial Regex EnhancedTimestampRegex();
}

