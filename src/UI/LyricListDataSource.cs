using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;
using Attribute = Terminal.Gui.Drawing.Attribute;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.UI;

/// <summary>
/// 单个显示行元数据与字符时间戳映射
/// </summary>
public sealed record LyricDisplayItem(
    int LineIndex,
    bool IsTranslation,
    string Text,
    int LeadingPad,
    long LineStartMs,
    (long StartMs, long EndMs)[]? CharTimes,
    bool Synthesized = false
);

/// <summary>
/// Terminal.Gui ListView 专用逐字歌词高保真数据源。
/// 实现了单行内字符级时间轴高亮（已唱字亮绿、正在唱字耀眼高亮、未唱字暗灰）。
/// </summary>
public sealed class LyricListDataSource : IListDataSource
{
    private readonly List<LyricDisplayItem> _items = [];
    private readonly List<string> _displayStrings = [];
    private int _maxItemLength;

    private int _activeLineIndex = -1;
    public int ActiveLineIndex
    {
        get => _activeLineIndex;
        set
        {
            if (_activeLineIndex != value)
            {
                if (_activeLineIndex >= 0 && value >= 0)
                {
                    PreviousActiveLineIndex = _activeLineIndex;
                    LineTransitionMs = CurrentPlaybackMs;
                }
                else
                {
                    PreviousActiveLineIndex = -1;
                }
                _activeLineIndex = value;
            }
        }
    }

    public int PreviousActiveLineIndex { get; private set; } = -1;
    public long LineTransitionMs { get; private set; }
    public long CurrentPlaybackMs { get; set; }

    /// <summary>歌词逐字过渡高亮开关（Z 键切换，两个歌词视图共享）</summary>
    public static bool SmoothHighlightEnabled { get; set; } = true;

    public int Count => _items.Count;
    public int MaxItemLength => _maxItemLength;
    public bool SuspendCollectionChangedEvent { get; set; }
    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add { }
        remove { }
    }

    public LyricListDataSource(
        IReadOnlyList<LyricLine> lyrics,
        bool showTranslation,
        int usableWidth,
        int padLines,
        Dictionary<int, int> lineToFirstItemMap,
        List<int> itemToLineMap)
    {
        lineToFirstItemMap.Clear();
        itemToLineMap.Clear();

        if (lyrics.Count == 0)
        {
            var emptyText = MainWindow.CenterLyricText("暂无歌词", usableWidth);
            _items.Add(new LyricDisplayItem(-1, false, emptyText, 0, 0, null));
            _displayStrings.Add(emptyText);
            _maxItemLength = emptyText.Length;
            return;
        }

        // 视口半高空行留白
        for (int p = 0; p < padLines; p++)
        {
            _items.Add(new LyricDisplayItem(-1, false, "", 0, 0, null));
            _displayStrings.Add("");
            itemToLineMap.Add(-1);
        }

        for (int i = 0; i < lyrics.Count; i++)
        {
            var line = lyrics[i];
            lineToFirstItemMap[i] = _items.Count;

            long lineStartMs = (long)line.Timestamp.TotalMilliseconds;
            long nextLineStartMs = i + 1 < lyrics.Count ? (long)lyrics[i + 1].Timestamp.TotalMilliseconds : 0;
            var (origCharTimes, wordsAvailable, synthesized) = BuildLineCharTimes(line, nextLineStartMs);

            // 原文行居中与自动断行
            var origWrapped = MainWindow.WrapLyricText(line.Text, usableWidth);
            int charCursor = 0;

            foreach (var oLine in origWrapped)
            {
                var (centeredText, leadingPad) = CenterLyricTextWithPad(oLine, usableWidth);
                (long StartMs, long EndMs)[]? subCharTimes = null;

                if (wordsAvailable && origCharTimes != null && oLine.Length > 0)
                {
                    subCharTimes = new (long, long)[oLine.Length];
                    int takeLen = Math.Min(oLine.Length, origCharTimes.Length - charCursor);
                    for (int k = 0; k < takeLen; k++)
                    {
                        subCharTimes[k] = origCharTimes[charCursor + k];
                    }
                    charCursor += takeLen;
                }

                _items.Add(new LyricDisplayItem(i, false, centeredText, leadingPad, lineStartMs, subCharTimes, synthesized));
                _displayStrings.Add(centeredText);
                itemToLineMap.Add(i);
                _maxItemLength = Math.Max(_maxItemLength, centeredText.Length);
            }

            // 翻译行
            if (showTranslation && !string.IsNullOrWhiteSpace(line.Trans))
            {
                var transWrapped = MainWindow.WrapLyricText(line.Trans, usableWidth);
                foreach (var tLine in transWrapped)
                {
                    var (centeredTrans, transPad) = CenterLyricTextWithPad(tLine, usableWidth);
                    _items.Add(new LyricDisplayItem(i, true, centeredTrans, transPad, lineStartMs, null));
                    _displayStrings.Add(centeredTrans);
                    itemToLineMap.Add(i);
                    _maxItemLength = Math.Max(_maxItemLength, centeredTrans.Length);
                }
            }

            // 行间空行
            _items.Add(new LyricDisplayItem(-1, false, "", 0, 0, null));
            _displayStrings.Add("");
            itemToLineMap.Add(-1);
        }

        for (int p = 0; p < padLines; p++)
        {
            _items.Add(new LyricDisplayItem(-1, false, "", 0, 0, null));
            _displayStrings.Add("");
            itemToLineMap.Add(-1);
        }
    }

    /// <summary>
    /// 字符从"未唱"过渡到"已唱"的进度（0..1），t=1 表示过渡完成
    /// </summary>
    internal static double FadeProgress(long elapsedMs, long startMs, int fadeMs)
    {
        if (fadeMs <= 0) return 1;
        long delta = elapsedMs - startMs;
        if (delta <= 0) return 0;
        if (delta >= fadeMs) return 1;
        return (double)delta / fadeMs;
    }

    /// <summary>
    /// 逐通道 RGB 线性插值；终端单元格使用单一颜色，由相邻字符色差构成渐变前沿
    /// </summary>
    internal static Color LerpColor(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        byte r = (byte)Math.Round(from.R + (to.R - from.R) * t);
        byte g = (byte)Math.Round(from.G + (to.G - from.G) * t);
        byte b = (byte)Math.Round(from.B + (to.B - from.B) * t);
        return new Color(r, g, b);
    }

    internal static ((long StartMs, long EndMs)[]? Times, bool Available, bool Synthesized) BuildLineCharTimes(LyricLine line, long nextLineStartMs)
    {
        if (string.IsNullOrEmpty(line.Text))
        {
            return (null, false, false);
        }

        int textLen = line.Text.Length;

        // 无词级时间轴（普通 LRC）：按本行→下一行的时长均分到每个字符，做行级兜底扫过
        if (line.Words == null || line.Words.Count == 0)
        {
            long lineStartMs = (long)line.Timestamp.TotalMilliseconds;
            long dur = nextLineStartMs > lineStartMs ? nextLineStartMs - lineStartMs : 0;
            dur = Math.Clamp(dur, 800, 15000);

            var fallbackTimes = new (long StartMs, long EndMs)[textLen];
            for (int c = 0; c < textLen; c++)
            {
                fallbackTimes[c] = (c * dur / textLen, (c + 1) * dur / textLen);
            }
            return (fallbackTimes, true, true);
        }
        var charTimes = new (long StartMs, long EndMs)[textLen];
        int currIdx = 0;
        long lastEndMs = 0;

        foreach (var w in line.Words)
        {
            if (string.IsNullOrEmpty(w.Word)) continue;
            int found = line.Text.IndexOf(w.Word, currIdx, StringComparison.Ordinal);
            if (found < 0)
            {
                found = line.Text.IndexOf(w.Word.Trim(), currIdx, StringComparison.Ordinal);
            }

            if (found >= 0)
            {
                // 填充单词之间的空格/标点间隙时间
                for (int gap = currIdx; gap < found; gap++)
                {
                    charTimes[gap] = (lastEndMs, w.OffsetMs);
                }

                int wordLen = w.Word.Length;
                double durPerChar = wordLen > 0 ? (double)w.DurationMs / wordLen : 0;

                for (int c = 0; c < wordLen && (found + c) < textLen; c++)
                {
                    long cStart = (long)(w.OffsetMs + c * durPerChar);
                    long cEnd = (long)(w.OffsetMs + (c + 1) * durPerChar);
                    charTimes[found + c] = (cStart, cEnd);
                }

                lastEndMs = w.OffsetMs + w.DurationMs;
                currIdx = Math.Min(textLen, found + wordLen);
            }
        }

        // 填充行尾剩余字符
        for (int rest = currIdx; rest < textLen; rest++)
        {
            charTimes[rest] = (lastEndMs, lastEndMs + 500);
        }

        return (charTimes, true, false);
    }

    private static (string CenteredText, int LeadingPad) CenterLyricTextWithPad(string text, int targetWidth)
    {
        int w = text.GetVisualWidth();
        if (w >= targetWidth) return (text, 0);
        int pad = Math.Max(0, (targetWidth - w) / 2);
        return (new string(' ', pad) + text, pad);
    }

    public void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX = 0)
    {
        listView.Move(Math.Max(col - viewportX, 0), row);
        if (item < 0 || item >= _items.Count)
        {
            for (int i = 0; i < width; i++) listView.AddRune((Rune)' ');
            return;
        }

        var rItem = _items[item];
        string str = rItem.Text;
        if (string.IsNullOrEmpty(str))
        {
            for (int i = 0; i < width; i++) listView.AddRune((Rune)' ');
            return;
        }

        bool isActive = (rItem.LineIndex >= 0 && rItem.LineIndex == ActiveLineIndex);
        bool isFadingOut = false;
        double fadeOutProgress = 0.0;

        if (!isActive && rItem.LineIndex >= 0 && rItem.LineIndex == PreviousActiveLineIndex && SmoothHighlightEnabled)
        {
            long elapsedSinceTransition = CurrentPlaybackMs - LineTransitionMs;
            if (elapsedSinceTransition >= 0 && elapsedSinceTransition < 250)
            {
                isFadingOut = true;
                fadeOutProgress = 1.0 - ((double)elapsedSinceTransition / 250.0);
            }
        }

        // 非活跃行、翻译行、无逐字时间戳行，或关闭过渡时的行级兜底行：走普通整行输出
        if (!isActive || rItem.IsTranslation || rItem.CharTimes == null || rItem.CharTimes.Length == 0 ||
            (!SmoothHighlightEnabled && rItem.Synthesized))
        {
            Attribute attr;
            if (isActive)
            {
                if (SmoothHighlightEnabled)
                {
                    long elapsedActive = CurrentPlaybackMs - LineTransitionMs;
                    if (elapsedActive >= 0 && elapsedActive < 200)
                    {
                        double fadeInProgress = Math.Clamp(elapsedActive / 200.0, 0.0, 1.0);
                        var fadeInColor = LerpColor(MikuTheme.QqTextLyricDim, MikuTheme.QqGreenPrimary, fadeInProgress);
                        attr = new Attribute(fadeInColor, Color.None);
                    }
                    else
                    {
                        attr = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    }
                }
                else
                {
                    attr = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
                }
            }
            else if (isFadingOut)
            {
                var fadedColor = LerpColor(MikuTheme.QqTextLyricDim, MikuTheme.QqGreenPrimary, fadeOutProgress);
                attr = new Attribute(fadedColor, Color.None);
            }
            else
            {
                attr = new Attribute(MikuTheme.QqTextLyricDim, Color.None);
            }

            listView.SetAttribute(attr);
            RenderPlainText(listView, str, width, viewportX);
            return;
        }

        // 活跃行逐字符动态着色渲染
        RenderWordSyncedLine(listView, rItem, width, viewportX);
    }

    private void RenderWordSyncedLine(ListView listView, LyricDisplayItem row, int width, int viewportX)
    {
        var activeAttr = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
        var dimAttr = new Attribute(MikuTheme.QqTextLyricDim, Color.None);

        long nowMs = CurrentPlaybackMs;
        long lineStartMs = row.LineStartMs;
        long elapsedInLine = nowMs >= lineStartMs ? nowMs - lineStartMs : 0;

        int colsDrawn = 0;
        string text = row.Text;
        var charTimes = row.CharTimes;
        Attribute? lastAttr = null;

        for (int i = 0; i < text.Length && colsDrawn < width;)
        {
            var decodeStatus = Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int charsConsumed);
            if (decodeStatus != System.Buffers.OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }

            int chCols = rune.GetVisualWidth();
            if (colsDrawn + chCols > width) break;

            Attribute currentAttr;
            if (i < row.LeadingPad)
            {
                currentAttr = dimAttr;
            }
            else
            {
                int charIdx = i - row.LeadingPad;
                if (charTimes != null && charIdx >= 0 && charIdx < charTimes.Length)
                {
                    var (startMs, endMs) = charTimes[charIdx];
                    if (!SmoothHighlightEnabled)
                    {
                        currentAttr = elapsedInLine >= startMs ? activeAttr : dimAttr;
                    }
                    else
                    {
                        // 过渡时长跟随字时长，柔光带恒定约两个字宽，不受歌曲快慢影响
                        int fadeMs = (int)Math.Clamp((endMs - startMs) * 2, 150, 600);

                        // 三段色带：未唱 → 亮绿前沿 → 已唱主色；中间段才做插值，两端走快路径
                        double t = FadeProgress(elapsedInLine, startMs, fadeMs);
                        currentAttr = t <= 0 ? dimAttr
                                      : t >= 1 ? activeAttr
                                      : t < 0.5
                                          ? new Attribute(LerpColor(MikuTheme.QqTextLyricDim, MikuTheme.QqGreenLight, t * 2), Color.None)
                                          : new Attribute(LerpColor(MikuTheme.QqGreenLight, MikuTheme.QqGreenPrimary, (t - 0.5) * 2), Color.None);
                    }
                }
                else
                {
                    currentAttr = activeAttr;
                }
            }

            // 相同颜色合并成一段，避免逐字切换属性
            if (lastAttr is null || !currentAttr.Equals(lastAttr.Value))
            {
                listView.SetAttribute(currentAttr);
                lastAttr = currentAttr;
            }
            listView.AddRune(rune);
            colsDrawn += chCols;
            i += charsConsumed;
        }

        listView.SetAttribute(dimAttr);
        while (colsDrawn < width)
        {
            listView.AddRune((Rune)' ');
            colsDrawn++;
        }
    }

    private static void RenderPlainText(ListView listView, string str, int width, int viewportX)
    {
        int colsDrawn = 0;
        for (int i = 0; i < str.Length && colsDrawn < width;)
        {
            var decodeStatus = Rune.DecodeFromUtf16(str.AsSpan(i), out var rune, out int charsConsumed);
            if (decodeStatus != System.Buffers.OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                charsConsumed = 1;
            }
            int chCols = rune.GetVisualWidth();
            if (colsDrawn + chCols > width) break;
            listView.AddRune(rune);
            colsDrawn += chCols;
            i += charsConsumed;
        }
        while (colsDrawn < width)
        {
            listView.AddRune((Rune)' ');
            colsDrawn++;
        }
    }

    public bool IsMarked(int item) => false;
    public void SetMark(int item, bool value) { }
    public IList ToList() => _displayStrings;
    public void Dispose() { }
}
