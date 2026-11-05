using System;
using QmTui.Models;
using QmTui.UI;
using Terminal.Gui.Drawing;
using Xunit;

namespace QmTui.Tests;

/// <summary>
/// 歌词逐字过渡的纯函数与行级兜底时间轴（不启动 Terminal.Gui）
/// </summary>
public class LyricHighlightTests
{
    private static (int R, int G, int B) Rgb(Color c) => (c.R, c.G, c.B);

    // ── FadeProgress ──

    [Theory]
    [InlineData(900, 1000, 0.0)]
    [InlineData(1000, 1000, 0.0)]
    [InlineData(1100, 1000, 0.5)]
    [InlineData(1150, 1000, 0.75)]
    [InlineData(1200, 1000, 1.0)]
    [InlineData(9999, 1000, 1.0)]
    public void FadeProgress_IsLinearAndClamped(long elapsedMs, long startMs, double expected)
    {
        Assert.Equal(expected, LyricListDataSource.FadeProgress(elapsedMs, startMs, 200), 3);
    }

    [Fact]
    public void FadeProgress_ZeroFadeCompletesImmediately()
    {
        Assert.Equal(1.0, LyricListDataSource.FadeProgress(0, 0, 0));
        Assert.Equal(1.0, LyricListDataSource.FadeProgress(-500, 0, 0));
    }

    // ── LerpColor ──

    [Fact]
    public void LerpColor_EndpointsMidpointAndClamp()
    {
        var from = new Color(10, 20, 30);
        var to = new Color(210, 120, 80);

        Assert.Equal((10, 20, 30), Rgb(LyricListDataSource.LerpColor(from, to, 0)));
        Assert.Equal((210, 120, 80), Rgb(LyricListDataSource.LerpColor(from, to, 1)));
        Assert.Equal((110, 70, 55), Rgb(LyricListDataSource.LerpColor(from, to, 0.5)));
        Assert.Equal((10, 20, 30), Rgb(LyricListDataSource.LerpColor(from, to, -0.5)));
        Assert.Equal((210, 120, 80), Rgb(LyricListDataSource.LerpColor(from, to, 2)));
    }

    // ── 行级兜底：无词级时间轴时按行时长均分 ──

    [Fact]
    public void BuildLineCharTimes_WithoutWords_SplitsLineDurationAcrossChars()
    {
        var line = new LyricLine(TimeSpan.FromSeconds(1), "abcdef");
        var (times, available, synthesized) = LyricListDataSource.BuildLineCharTimes(line, 3000);

        Assert.True(available);
        Assert.True(synthesized);
        Assert.NotNull(times);
        Assert.Equal(6, times!.Length);
        Assert.Equal(0, times[0].StartMs);
        Assert.Equal(2000, times[^1].EndMs);   // 相对行首：下一行 3000 - 本行 1000

        for (int i = 1; i < times.Length; i++)
        {
            Assert.Equal(times[i - 1].EndMs, times[i].StartMs);
        }
    }

    [Fact]
    public void BuildLineCharTimes_LastLine_ClampsToMinimumDuration()
    {
        var line = new LyricLine(TimeSpan.FromSeconds(1), "abc");
        var (times, _, synthesized) = LyricListDataSource.BuildLineCharTimes(line, 0);

        Assert.True(synthesized);
        Assert.Equal(800, times![^1].EndMs);
    }

    // ── 回归：词级时间存在时不走兜底 ──

    [Fact]
    public void BuildLineCharTimes_WithWords_KeepsWordLevelOffsets()
    {
        var line = new LyricLine(TimeSpan.FromSeconds(1), "你好世界", "", [
            new WordSpan("你好", 0, 400),
            new WordSpan("世界", 400, 500)
        ]);
        var (times, available, synthesized) = LyricListDataSource.BuildLineCharTimes(line, 5000);

        Assert.True(available);
        Assert.False(synthesized);
        Assert.Equal((400L, 650L), times![2]);
    }

    [Fact]
    public void ActiveLineIndex_TransitionTracking_RecordsPreviousLineAndTimestamp()
    {
        var lyrics = new[]
        {
            new LyricLine(TimeSpan.FromSeconds(1), "第一行"),
            new LyricLine(TimeSpan.FromSeconds(3), "第二行")
        };
        var lineMap = new System.Collections.Generic.Dictionary<int, int>();
        var itemMap = new System.Collections.Generic.List<int>();
        var ds = new LyricListDataSource(lyrics, false, 40, 2, lineMap, itemMap)
        {
            CurrentPlaybackMs = 1500,
            ActiveLineIndex = 0
        };

        Assert.Equal(0, ds.ActiveLineIndex);
        Assert.Equal(-1, ds.PreviousActiveLineIndex);

        // 切换到第 1 行
        ds.CurrentPlaybackMs = 3000;
        ds.ActiveLineIndex = 1;

        Assert.Equal(1, ds.ActiveLineIndex);
        Assert.Equal(0, ds.PreviousActiveLineIndex);
        Assert.Equal(3000, ds.LineTransitionMs);
    }
}
