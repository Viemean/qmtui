using QmTui.Utils;
using Xunit;

namespace QmTui.Tests;

public class LyricParserEdgeCaseTests
{
    // ── 空输入 ───────────────────────────────────────────────────

    [Fact]
    public void ParseLrc_NullOrEmpty_ReturnsEmptyList()
    {
        Assert.Empty(LyricParser.ParseLrc(null!));
        Assert.Empty(LyricParser.ParseLrc(""));
        Assert.Empty(LyricParser.ParseLrc("   "));
    }

    [Fact]
    public void ParseLrc_OnlyMetaLines_ReturnsEmptyList()
    {
        var lrc = "[ti:Test]\n[ar:Artist]\n[al:Album]\n[by:Editor]";
        Assert.Empty(LyricParser.ParseLrc(lrc));
    }

    // ── 无时间戳行应被跳过 ────────────────────────────────────────

    [Fact]
    public void ParseLrc_LinesWithoutTimestamp_AreSkipped()
    {
        var lrc = "这行没有时间戳\n[00:05.00]这行有时间戳\n也没有时间戳";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("这行有时间戳", result[0].Text);
    }

    // ── 空文本时间戳应被跳过 ──────────────────────────────────────

    [Fact]
    public void ParseLrc_EmptyTextAfterTimestamp_IsSkipped()
    {
        var lrc = "[00:05.00]\n[00:10.00]有内容";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("有内容", result[0].Text);
    }

    // ── 超大时间戳 ────────────────────────────────────────────────

    [Fact]
    public void ParseLrc_LargeTimestamp_ParsesCorrectly()
    {
        var lrc = "[99:59.999]结束歌词";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal(99, (int)result[0].Timestamp.TotalMinutes);
        Assert.Equal(59, result[0].Timestamp.Seconds);
    }

    // ── MergeLyrics：翻译合并 ─────────────────────────────────────

    [Fact]
    public void MergeLyrics_MatchingTimestamps_AttachesTranslation()
    {
        var orig  = "[00:05.00]Hello world\n[00:10.00]Goodbye";
        var trans = "[00:05.00]你好世界\n[00:10.00]再见";

        var merged = LyricParser.MergeLyrics(orig, trans);

        Assert.Equal(2, merged.Count);
        Assert.Equal("你好世界", merged[0].Trans);
        Assert.Equal("再见", merged[1].Trans);
    }

    [Fact]
    public void MergeLyrics_EmptyTranslation_LeavesOriginalIntact()
    {
        var orig   = "[00:05.00]Hello";
        var merged = LyricParser.MergeLyrics(orig, "");

        Assert.Single(merged);
        Assert.True(string.IsNullOrEmpty(merged[0].Trans));
    }

    [Fact]
    public void MergeLyrics_NullTranslation_DoesNotThrow()
    {
        var orig   = "[00:05.00]Hello";
        var merged = LyricParser.MergeLyrics(orig, null!);
        Assert.Single(merged);
    }

    // ── HTML 实体替换 ─────────────────────────────────────────────

    [Fact]
    public void ParseLrc_HtmlQuote_AposIsReplaced()
    {
        var lrc    = "[00:01.00]It&apos;s fine";
        var result = LyricParser.ParseLrc(lrc);
        Assert.Single(result);
        Assert.Equal("It\u2019s fine", result[0].Text);
    }

    // ── 纯中文与外文翻译判定 ─────────────────────────────────────

    [Fact]
    public void NeedsTranslation_PureChineseLyrics_ReturnsFalse()
    {
        var lyrics = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.Zero, "说了再见 - 周杰伦 (Jay Chou)"),
            new(TimeSpan.FromSeconds(1), "词：古小力/黄凌嘉"),
            new(TimeSpan.FromSeconds(2), "曲：周杰伦"),
            new(TimeSpan.FromSeconds(3), "编曲：钟兴民"),
            new(TimeSpan.FromSeconds(5), "天凉了 雨下了 你走了"),
            new(TimeSpan.FromSeconds(10), "清楚了 我爱的 遗失了"),
            new(TimeSpan.FromSeconds(15), "落叶飘在湖面上睡着了")
        };

        Assert.False(LyricParser.NeedsTranslation(lyrics));
        Assert.False(LyricParser.HasTranslation(lyrics));
    }

    [Fact]
    public void NeedsTranslation_EnglishLyrics_ReturnsTrue()
    {
        var lyrics = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.Zero, "Shake It Off - Taylor Swift"),
            new(TimeSpan.FromSeconds(5), "I stay out too late, got nothing in my brain"),
            new(TimeSpan.FromSeconds(10), "That's what people say, that's what people say"),
            new(TimeSpan.FromSeconds(15), "I go on too many dates, but I can't make them stay")
        };

        Assert.True(LyricParser.NeedsTranslation(lyrics));
    }

    [Fact]
    public void NeedsTranslation_JapaneseLyrics_ReturnsTrue()
    {
        var lyrics = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.Zero, "Rain - 秦基博"),
            new(TimeSpan.FromSeconds(5), "言葉にできず凍えるままで"),
            new(TimeSpan.FromSeconds(10), "人前ではやさしく生きていた")
        };

        Assert.True(LyricParser.NeedsTranslation(lyrics));
    }
}
