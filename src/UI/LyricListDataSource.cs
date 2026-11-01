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
    (long StartMs, long EndMs)[]? CharTimes
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

    public int ActiveLineIndex { get; set; } = -1;
    public long CurrentPlaybackMs { get; set; }

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
            var (origCharTimes, wordsAvailable) = BuildLineCharTimes(line);

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

                _items.Add(new LyricDisplayItem(i, false, centeredText, leadingPad, lineStartMs, subCharTimes));
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

    private static ((long StartMs, long EndMs)[]? Times, bool Available) BuildLineCharTimes(LyricLine line)
    {
        if (line.Words == null || line.Words.Count == 0 || string.IsNullOrEmpty(line.Text))
        {
            return (null, false);
        }

        int textLen = line.Text.Length;
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

        return (charTimes, true);
    }

    private static (string CenteredText, int LeadingPad) CenterLyricTextWithPad(string text, int targetWidth)
    {
        int w = MainWindow.GetDisplayWidth(text);
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

        // 非活跃行、翻译行或无逐字时间戳行：走普通整行输出
        if (!isActive || rItem.IsTranslation || rItem.CharTimes == null || rItem.CharTimes.Length == 0)
        {
            var attr = isActive
                ? new Attribute(MikuTheme.QqGreenPrimary, Color.None)
                : new Attribute(MikuTheme.QqTextLyricDim, Color.None);
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

        for (int i = 0; i < text.Length && colsDrawn < width; i++)
        {
            char ch = text[i];
            int chCols = ch > 127 ? 2 : 1;
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
                    var (startMs, _) = charTimes[charIdx];
                    currentAttr = elapsedInLine >= startMs ? activeAttr : dimAttr;
                }
                else
                {
                    currentAttr = activeAttr;
                }
            }

            listView.SetAttribute(currentAttr);
            listView.AddRune((Rune)ch);
            colsDrawn += chCols;
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
        for (int i = 0; i < str.Length && colsDrawn < width; i++)
        {
            char ch = str[i];
            int chCols = ch > 127 ? 2 : 1;
            if (colsDrawn + chCols > width) break;
            listView.AddRune((Rune)ch);
            colsDrawn += chCols;
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
