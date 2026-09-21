using System.Collections.ObjectModel;
using System.Text;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using QmTui.Models;

namespace QmTui.UI;

public sealed partial class SongListView
{
    private bool OnMarqueeTick()
    {
        if (!Visible || _isMarqueePaused || string.IsNullOrEmpty(_currentFullTitle)) return true;

        int curWidth = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
        int maxW = Math.Max(12, curWidth - 4);
        int textW = GetDisplayWidth(_currentFullTitle);

        if (textW <= maxW)
        {
            if (_lastDispatchedTitle != _currentFullTitle)
            {
                _lastDispatchedTitle = _currentFullTitle;
                _marqueeOffset = 0;
                DisplayTitleChanged?.Invoke(_currentFullTitle);
            }
            return true;
        }

        if (_marqueeRunes.Length > 0)
        {
            _marqueeOffset = (_marqueeOffset + 1) % _marqueeRunes.Length;
        }
        var slice = GetMarqueeSlice(_marqueeRunes, _marqueeOffset, maxW);
        if (_lastDispatchedTitle != slice)
        {
            _lastDispatchedTitle = slice;
            DisplayTitleChanged?.Invoke(slice);
        }

        return true;
    }

    private void SetMarqueeTitle(string fullText)
    {
        if (_currentFullTitle == fullText) return;

        _currentFullTitle = fullText;
        _marqueeOffset = 0;
        _marqueeRunes = (fullText + "        ").ToRunes();

        int curWidth = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
        int maxW = Math.Max(12, curWidth - 4);
        int textW = GetDisplayWidth(fullText);

        if (textW <= maxW)
        {
            _lastDispatchedTitle = fullText;
            DisplayTitleChanged?.Invoke(fullText);
        }
        else
        {
            var slice = GetMarqueeSlice(_marqueeRunes, 0, maxW);
            _lastDispatchedTitle = slice;
            DisplayTitleChanged?.Invoke(slice);
        }
    }

    private int GetCurrentItemCount() => _displayMode switch
    {
        SongListDisplayMode.Playlists => _playlists.Count,
        SongListDisplayMode.Albums => _albums.Count,
        SongListDisplayMode.CustomText => _customItems.Count,
        _ => _songs.Count
    };

    private void CheckTriggerLoadMore()
    {
        if (_isRadioMode) return;

        int totalCount = GetCurrentItemCount();
        if (totalCount == 0) return;

        int current = _listView.SelectedItem ?? 0;
        int viewBottom = _listView.Viewport.Y + _listView.Viewport.Height;

        if (current >= totalCount - 6 || viewBottom >= totalCount - 4)
        {
            LoadMoreRequested?.Invoke();
        }
    }

    private void RefreshRadioDisplay()
    {
        if (!_isRadioMode || _currentRadioSong == null) return;

        int totalWidth = _listView.Viewport.Width;
        if (totalWidth <= 0)
        {
            int curW = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
            totalWidth = curW > 0 ? curW - 2 : 68;
        }

        int cardWidth = Math.Clamp(totalWidth - 4, 38, 64);
        int indent = Math.Max(0, (totalWidth - cardWidth) / 2);
        string pad = new string(' ', indent);
        string borderLine = pad + "+" + new string('-', cardWidth - 2) + "+";
        int innerW = cardWidth - 4;

        var song = _currentRadioSong;
        var albumStr = string.IsNullOrWhiteSpace(song.Album) ? "单曲" : song.Album;

        string FormatRow(string content)
        {
            int w = GetDisplayWidth(content);
            if (w < innerW)
            {
                return pad + "| " + content + new string(' ', innerW - w) + " |";
            }
            if (w > innerW)
            {
                return pad + "| " + TruncateAndPadWide(content, innerW) + " |";
            }
            return pad + "| " + content + " |";
        }

        string FormatCenteredRow(string content)
        {
            int w = GetDisplayWidth(content);
            if (w >= innerW)
            {
                return FormatRow(content);
            }
            int leftPad = (innerW - w) / 2;
            int rightPad = innerW - w - leftPad;
            return pad + "| " + new string(' ', leftPad) + content + new string(' ', rightPad) + " |";
        }

        var lines = new List<string>(12)
        {
            "",
            borderLine,
            FormatCenteredRow("[ 个性电台 · 猜你喜欢 ]"),
            borderLine,
            FormatRow(""),
            FormatRow($"  曲名: 《{song.Title}》"),
            FormatRow($"  歌手: {song.Artist}"),
            FormatRow($"  专辑: {albumStr}"),
            FormatRow($"  音质: {_currentRadioQuality}          收听计数: 第 {_currentRadioPlayedCount:D2} 首"),
            FormatRow(""),
            borderLine
        };

        _radioDisplayLines.Clear();
        _radioDisplayLines.AddRange(lines);
        _displayRows.Clear();
        foreach (var l in _radioDisplayLines) _displayRows.Add(l);
    }

    private void RefreshDisplayList()
    {
        if (_isUpdatingDisplay) return;

        int totalCount = GetCurrentItemCount();
        if (totalCount == 0)
        {
            _displayRows.Clear();
            _listView.SelectedItem = null;
            _scrollBar.UpdateMetrics(0, _listView.Viewport.Height, 0);
            return;
        }

        try
        {
            _isUpdatingDisplay = true;

            int totalWidth = _listView.Viewport.Width;
            if (totalWidth <= 0)
            {
                int curW = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
                totalWidth = curW > 0 ? curW - 2 : 80;
            }

            int idxWidth = GetIndexWidth();
            int indexArea = idxWidth + 2;

            if (_displayMode == SongListDisplayMode.Playlists)
            {
                int countColW = 10;
                int newTitleW = Math.Max(20, totalWidth - indexArea - countColW - 6);
                if (newTitleW != _playlistTitleColWidth || _indexColWidth != idxWidth)
                {
                    _playlistTitleColWidth = newTitleW;
                    _indexColWidth = idxWidth;
                    _cachedNormalRows.Clear();
                }
            }
            else if (_displayMode == SongListDisplayMode.Albums)
            {
                int countColW = 10;
                int remain = Math.Max(30, totalWidth - indexArea - countColW - 8);
                int newAlbumW = Math.Max(16, (int)Math.Round(remain * 0.60));
                int newArtistW = Math.Max(10, remain - newAlbumW);
                if (newAlbumW != _albumTitleColWidth || newArtistW != _albumArtistColWidth || _indexColWidth != idxWidth)
                {
                    _albumTitleColWidth = newAlbumW;
                    _albumArtistColWidth = newArtistW;
                    _indexColWidth = idxWidth;
                    _cachedNormalRows.Clear();
                }
            }
            else
            {
                int remain = Math.Max(30, totalWidth - indexArea - 9);
                int newTitleW = Math.Max(16, (int)Math.Round(remain * 0.46));
                int newArtistW = Math.Max(10, (int)Math.Round(remain * 0.24));
                int newAlbumW = Math.Max(12, remain - newTitleW - newArtistW);

                if (newTitleW != _titleColWidth || newArtistW != _artistColWidth || newAlbumW != _albumColWidth || _indexColWidth != idxWidth)
                {
                    _titleColWidth = newTitleW;
                    _artistColWidth = newArtistW;
                    _albumColWidth = newAlbumW;
                    _indexColWidth = idxWidth;
                    _cachedNormalRows.Clear();
                }
            }

            var prevSelected = _listView.SelectedItem;
            int selectedIdx = (prevSelected.HasValue && prevSelected.Value >= 0 && prevSelected.Value < totalCount)
                ? prevSelected.Value
                : 0;

            if (_cachedNormalRows.Count != totalCount)
            {
                _cachedNormalRows.Clear();
                for (int i = 0; i < totalCount; i++)
                {
                    _cachedNormalRows.Add(FormatCurrentRow(i, isSelected: false));
                }
            }

            var displayList = new List<string>(totalCount);
            for (int i = 0; i < totalCount; i++)
            {
                displayList.Add(i == selectedIdx ? FormatCurrentRow(i, isSelected: true) : _cachedNormalRows[i]);
            }
            _lastHighlightRow = selectedIdx;

            var prevViewportY = _listView.Viewport.Y;
            _displayRows.Clear();
            foreach (var item in displayList)
            {
                _displayRows.Add(item);
            }
            if (prevSelected.HasValue && prevSelected.Value >= 0 && prevSelected.Value < totalCount)
            {
                _listView.SelectedItem = prevSelected.Value;
            }
            else if (totalCount > 0)
            {
                _listView.SelectedItem = 0;
            }
            else
            {
                _listView.SelectedItem = null;
            }
            if (prevViewportY > 0)
            {
                _listView.Viewport = new Rectangle(_listView.Viewport.X, prevViewportY, _listView.Viewport.Width, _listView.Viewport.Height);
            }
            _scrollBar.UpdateMetrics(totalCount, _listView.Viewport.Height, _listView.Viewport.Y);
        }
        finally
        {
            _isUpdatingDisplay = false;
        }
    }

    private string FormatCurrentRow(int index, bool isSelected) => _displayMode switch
    {
        SongListDisplayMode.Playlists => FormatPlaylistRow(index, isSelected),
        SongListDisplayMode.Albums => FormatAlbumRow(index, isSelected),
        _ => FormatSongRow(index, isSelected)
    };

    private string FormatPlaylistRow(int index, bool isSelected)
    {
        if (index < 0 || index >= _playlists.Count) return "";
        var p = _playlists[index];
        string countStr = $"{p.SongNum} 首";

        var titleCol = FormatCell(p.Title, _playlistTitleColWidth, isSelected);
        var countCol = FormatCell(countStr, 10, false);

        int idxWidth = _indexColWidth > 0 ? _indexColWidth : GetIndexWidth();
        string idxStr = (index + 1).ToString().PadLeft(idxWidth, '0');
        return $"{idxStr}  {titleCol}  {countCol}";
    }

    private string FormatAlbumRow(int index, bool isSelected)
    {
        if (index < 0 || index >= _albums.Count) return "";
        var a = _albums[index];
        string countStr = a.SongCount > 0 ? $"{a.SongCount} 首" : "";
        string artist = string.IsNullOrWhiteSpace(a.Artist) ? "群星" : a.Artist;

        var albumCol = FormatCell(a.Title, _albumTitleColWidth, isSelected);
        var artistCol = FormatCell(artist, _albumArtistColWidth, false);
        var countCol = FormatCell(countStr, 10, false);

        int idxWidth = _indexColWidth > 0 ? _indexColWidth : GetIndexWidth();
        string idxStr = (index + 1).ToString().PadLeft(idxWidth, '0');
        return $"{idxStr}  {albumCol}  {artistCol}  {countCol}";
    }

    private string FormatSongRow(int index, bool isSelected)
    {
        if (index < 0 || index >= _songs.Count) return "";
        var s = _songs[index];
        var albumStr = string.IsNullOrWhiteSpace(s.Album) ? "单曲" : s.Album;

        bool isTitleFocused = isSelected && _focusedSubColumn == SongSubColumn.Title;
        bool isArtistFocused = isSelected && _focusedSubColumn == SongSubColumn.Artist;
        bool isAlbumFocused = isSelected && _focusedSubColumn == SongSubColumn.Album;

        var titleCol = FormatCell(s.Title, _titleColWidth, isTitleFocused);
        var artistCol = FormatCell(s.Artist, _artistColWidth, isArtistFocused);
        var albumCol = FormatCell(albumStr, _albumColWidth, isAlbumFocused);

        int idxWidth = _indexColWidth > 0 ? _indexColWidth : GetIndexWidth();
        string idxStr = (index + 1).ToString().PadLeft(idxWidth, '0');
        return $"{idxStr}  {titleCol}  {artistCol}  {albumCol}";
    }

    private static string FormatCell(string text, int targetWidth, bool isFocused)
    {
        if (targetWidth <= 0) return "";
        if (string.IsNullOrEmpty(text))
        {
            return GetPadding(targetWidth);
        }

        if (!isFocused)
        {
            return TruncateAndPadWide(text, targetWidth);
        }

        int textW = GetDisplayWidth(text);
        int bracketOverhead = 4; // "[ " + " ]"
        if (targetWidth <= bracketOverhead)
        {
            return TruncateAndPadWide($"[{text}]", targetWidth);
        }

        int innerLimit = targetWidth - bracketOverhead;
        if (textW <= innerLimit)
        {
            string content = $"[ {text} ]";
            return content + GetPadding(targetWidth - (textW + bracketOverhead));
        }

        // 超长文本截断：保证两侧完整方括号，格式为 [ xxx.. ]
        int maxTruncateWidth = Math.Max(1, targetWidth - 6);
        var sb = new StringBuilder(targetWidth);
        sb.Append("[ ");
        int curW = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int rw = rune.GetColumns();
            if (curW + rw > maxTruncateWidth)
            {
                break;
            }
            sb.Append(rune);
            curW += rw;
        }
        sb.Append(".. ]");
        int totalUsed = 2 + curW + 4;
        if (totalUsed < targetWidth)
        {
            sb.Append(GetPadding(targetWidth - totalUsed));
        }
        return sb.ToString();
    }

    private int GetIndexWidth()
    {
        return _songs.Count >= 1000 ? 4 : (_songs.Count >= 100 ? 3 : 2);
    }

    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.GetColumns();
    }

    private static readonly string s_spaces = new(' ', 256);

    public static string GetPadding(int count)
    {
        if (count <= 0) return "";
        if (count <= s_spaces.Length) return s_spaces[..count];
        return new string(' ', count);
    }

    private static string GetMarqueeSlice(System.Text.Rune[] runes, int offset, int targetWidth)
    {
        int totalLen = runes.Length;
        if (totalLen == 0) return "";
        offset %= totalLen;

        var sb = new StringBuilder(targetWidth);
        int curW = 0;
        for (int i = 0; i < totalLen * 2; i++)
        {
            var rune = runes[(offset + i) % totalLen];
            int runeW = rune.GetColumns();
            if (curW + runeW > targetWidth)
            {
                break;
            }
            sb.Append(rune);
            curW += runeW;
        }

        if (curW < targetWidth)
        {
            sb.Append(GetPadding(targetWidth - curW));
        }
        return sb.ToString();
    }

    public static string TruncateAndPadWide(string text, int targetWidth)
    {
        if (targetWidth <= 0) return "";
        if (string.IsNullOrEmpty(text))
        {
            return GetPadding(targetWidth);
        }

        int totalW = GetDisplayWidth(text);
        if (totalW == targetWidth)
        {
            return text;
        }
        if (totalW < targetWidth)
        {
            return text + GetPadding(targetWidth - totalW);
        }

        int limit = Math.Max(1, targetWidth - 2);
        int currentW = 0;
        var sb = new StringBuilder(targetWidth);

        foreach (var rune in text.EnumerateRunes())
        {
            int runeW = rune.GetColumns();
            if (currentW + runeW > limit)
            {
                break;
            }
            sb.Append(rune);
            currentW += runeW;
        }

        sb.Append("..");
        currentW += 2;

        if (currentW < targetWidth)
        {
            sb.Append(GetPadding(targetWidth - currentW));
        }

        return sb.ToString();
    }
}
