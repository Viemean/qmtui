using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;
using QmTui.Utils;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace QmTui.UI;

/// <summary>
/// 歌曲评论区视图：支持精彩热评（Top 3 折叠/展开）、最新评论流、自适应折行、滚动加载与终端图片按需预览
/// </summary>
public sealed class SongCommentView : View
{
    private static readonly char[] s_lineSeparators = ['\r', '\n'];

    private enum ItemType
    {
        Header,
        HotToggle,
        CommentMeta,
        CommentContent,
        CommentPic,
        EmptyOrStatus
    }

    private sealed record DisplayItem(
        ItemType Type,
        string Text,
        SongComment? Comment = null,
        bool IsHot = false
    );

    private readonly ListView _listView;
    private readonly ThinScrollBarView _scrollBar;
    private readonly Label _scrollTopBtn;

    private Song? _currentSong;
    private readonly List<SongComment> _hotComments = new();
    private readonly List<SongComment> _normalComments = new();
    private readonly HashSet<string> _seenCommentIds = new();
    private readonly List<DisplayItem> _displayItems = new();

    private int _totalCommentCount;
    private int _currentPage;
    private string _lastSeqNo = "";
    private bool _hasMore;
    private bool _isLoading;
    private bool _isError;
    private bool _isHotExpanded;
    private int _lastViewportWidth;
    private CancellationTokenSource? _loadCts;
    private object? _resizeTimerToken;

    public event Action? CloseRequested;
    public event Action<bool>? TabNavigationRequested;

    public bool IsLoading => _isLoading;

    public SongCommentView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;

        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        _listView.KeyBindings.Remove(Key.Space);
        _listView.SetScheme(MikuTheme.Lyric);

        _listView.RowRender += (s, e) =>
        {
            if (e.Row < 0 || e.Row >= _displayItems.Count) return;
            var item = _displayItems[e.Row];

            switch (item.Type)
            {
                case ItemType.Header:
                    e.RowAttribute = new Attribute(MikuTheme.QqTextLyricDim, Color.None);
                    break;
                case ItemType.HotToggle:
                    e.RowAttribute = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    break;
                case ItemType.CommentMeta:
                    e.RowAttribute = item.IsHot
                        ? new Attribute(MikuTheme.QqGreenPrimary, Color.None)
                        : new Attribute(Color.White, Color.None);
                    break;
                case ItemType.CommentContent:
                    e.RowAttribute = new Attribute(MikuTheme.QqTextLyricDim, Color.None);
                    break;
                case ItemType.CommentPic:
                    e.RowAttribute = new Attribute(MikuTheme.QqGreenPrimary, Color.None);
                    break;
                default:
                    e.RowAttribute = new Attribute(MikuTheme.QqTextLyricDim, Color.None);
                    break;
            }
        };

        _listView.ValueChanged += (s, e) =>
        {
            _scrollBar?.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            CheckTriggerLoadMore();
        };

        _listView.Accepting += (s, e) =>
        {
            int idx = _listView.SelectedItem ?? -1;
            if (idx >= 0 && idx < _displayItems.Count)
            {
                var item = _displayItems[idx];
                if (item.Type == ItemType.HotToggle)
                {
                    _isHotExpanded = !_isHotExpanded;
                    RebuildDisplayItems();
                    e.Handled = true;
                    return;
                }
            }
        };

        _listView.KeyDown += (s, k) =>
        {
            if (k == Key.CursorDown || k == Key.PageDown || k == Key.End)
            {
                _scrollBar?.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
            }
            else if (k == Key.CursorUp || k == Key.PageUp || k == Key.Home)
            {
                _scrollBar?.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            }
        };

        _listView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                _scrollBar?.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
            }
            else if (m.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                _scrollBar?.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            }

            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                int idx = _listView.SelectedItem ?? -1;
                if (idx >= 0 && idx < _displayItems.Count)
                {
                    var item = _displayItems[idx];
                    if (item.Type == ItemType.HotToggle)
                    {
                        _isHotExpanded = !_isHotExpanded;
                        RebuildDisplayItems();
                        m.Handled = true;
                        return;
                    }
                }
            }
        };

        _listView.ViewportChanged += (s, e) =>
        {
            int curW = _listView.Viewport.Width;
            if (curW > 0 && curW != _lastViewportWidth)
            {
                _lastViewportWidth = curW;
                if (_resizeTimerToken != null)
                {
                    Application.RemoveTimeout(_resizeTimerToken);
                    _resizeTimerToken = null;
                }
                _resizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
                {
                    _resizeTimerToken = null;
                    RebuildDisplayItems();
                    return false;
                });
            }
        };

        Add(_listView);

        _scrollBar = new ThinScrollBarView
        {
            X = Pos.AnchorEnd(1),
            Y = 0,
            Height = Dim.Fill(),
            AutoShowOnMetricsChange = false
        };
        _scrollBar.ScrollPositionChanged += targetRow =>
        {
            int count = _displayItems.Count;
            if (count > 0)
            {
                int clamped = Math.Clamp(targetRow, 0, count - 1);
                _listView.SelectedItem = clamped;
                _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, clamped, _listView.Viewport.Width, _listView.Viewport.Height);
                _scrollBar.UpdateMetrics(count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
            }
        };
        Add(_scrollBar);

        // 右下角悬浮回到顶部按钮 [▲]
        _scrollTopBtn = new Label
        {
            Text = "[▲]",
            X = Pos.AnchorEnd(5),
            Y = Pos.AnchorEnd(2),
            Width = 3,
            Height = 1,
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        _scrollTopBtn.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
            HotNormal = new Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        _scrollTopBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) ||
                m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                ScrollToTop();
                m.Handled = true;
            }
        };
        Add(_scrollTopBtn);

        // 快捷键处理
        KeyDown += (s, k) =>
        {
            var ch = char.ToUpperInvariant((char)k.AsRune.Value);
            if (k == Key.Esc || ch == 'C' || k == Key.C)
            {
                CloseRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.Home)
            {
                ScrollToTop();
                k.Handled = true;
                return;
            }

            if (ch == 'R' || k == Key.R)
            {
                _ = ReloadAsync();
                k.Handled = true;
                return;
            }

            if (ch == 'H' || k == Key.H)
            {
                if (_hotComments.Count > 3)
                {
                    _isHotExpanded = !_isHotExpanded;
                    RebuildDisplayItems();
                    k.Handled = true;
                    return;
                }
            }

            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                TabNavigationRequested?.Invoke(!k.IsShift);
                k.Handled = true;
                return;
            }
        };
    }

    public void SetSong(Song? song)
    {
        if (_currentSong?.Mid == song?.Mid && song != null) return;

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch {}
        _loadCts = null;

        _currentSong = song;
        _hotComments.Clear();
        _normalComments.Clear();
        _seenCommentIds.Clear();
        _totalCommentCount = 0;
        _currentPage = 0;
        _lastSeqNo = "";
        _hasMore = false;
        _isHotExpanded = false;
        _isLoading = false;
        _isError = false;

        RebuildDisplayItems();

        if (song != null && Visible)
        {
            _ = LoadCommentsAsync(isInitial: true);
        }
    }

    public async Task ReloadAsync()
    {
        if (_currentSong == null) return;
        _hotComments.Clear();
        _normalComments.Clear();
        _seenCommentIds.Clear();
        _totalCommentCount = 0;
        _currentPage = 0;
        _lastSeqNo = "";
        _hasMore = false;
        _isHotExpanded = false;
        _isError = false;
        await LoadCommentsAsync(isInitial: true).ConfigureAwait(false);
    }

    public void OnActivated()
    {
        Visible = true;
        SetFocus();
        if (_currentSong != null && _hotComments.Count == 0 && _normalComments.Count == 0 && !_isLoading)
        {
            _ = LoadCommentsAsync(isInitial: true);
        }
        else
        {
            RebuildDisplayItems();
        }
    }

    public void OnDeactivated()
    {
        Visible = false;
        try
        {
            _loadCts?.Cancel();
        }
        catch {}
    }

    private async Task LoadCommentsAsync(bool isInitial)
    {
        if (_currentSong == null || _isLoading) return;
        if (!isInitial && !_hasMore) return;

        _isLoading = true;
        _isError = false;

        var song = _currentSong;
        int targetPage = isInitial ? 0 : _currentPage + 1;

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch {}
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        RebuildDisplayItems();

        try
        {
            var pageResult = await MusicApi.GetSongCommentsAsync(
                song.Id,
                song.Mid,
                targetPage,
                pageSize: 25,
                lastCommentSeqNo: isInitial ? "" : _lastSeqNo,
                ct: ct
            ).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            Application.Invoke(() =>
            {
                if (pageResult != null)
                {
                    _totalCommentCount = pageResult.TotalCount;
                    _hasMore = pageResult.HasMore;
                    if (!string.IsNullOrEmpty(pageResult.LastSeqNo))
                    {
                        _lastSeqNo = pageResult.LastSeqNo;
                    }
                    _currentPage = targetPage;

                    if (isInitial)
                    {
                        _hotComments.Clear();
                        _seenCommentIds.Clear();
                        foreach (var hot in pageResult.HotComments)
                        {
                            if (_seenCommentIds.Add(hot.CommentId))
                            {
                                _hotComments.Add(hot);
                            }
                        }

                        _normalComments.Clear();
                        foreach (var c in pageResult.Comments)
                        {
                            if (_seenCommentIds.Add(c.CommentId))
                            {
                                _normalComments.Add(c);
                            }
                        }
                    }
                    else
                    {
                        if (pageResult.Comments.Count == 0)
                        {
                            _hasMore = false;
                        }
                        else
                        {
                            int addedCount = 0;
                            foreach (var c in pageResult.Comments)
                            {
                                if (_seenCommentIds.Add(c.CommentId))
                                {
                                    _normalComments.Add(c);
                                    addedCount++;
                                }
                            }
                            if (addedCount == 0)
                            {
                                _hasMore = false;
                            }
                        }
                    }
                }
                else
                {
                    if (isInitial)
                    {
                        _isError = true;
                    }
                    else
                    {
                        _hasMore = false;
                    }
                }

                _isLoading = false;
                RebuildDisplayItems();
            });
        }
        catch (OperationCanceledException) {}
        catch (Exception ex)
        {
            AppLogger.Error("SongCommentView", $"LoadCommentsAsync failed for {song.Title}", ex);
            Application.Invoke(() =>
            {
                if (isInitial) _isError = true;
                _isLoading = false;
                RebuildDisplayItems();
            });
        }
    }

    private void RebuildDisplayItems()
    {
        _displayItems.Clear();

        int viewW = _listView.Viewport.Width > 0 ? _listView.Viewport.Width : 40;
        int usableWidth = Math.Max(16, viewW - 3);

        if (_currentSong == null)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  [ 当前无正在播放曲目 ]"));
            UpdateListSource();
            return;
        }

        if (_isLoading && _hotComments.Count == 0 && _normalComments.Count == 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  [ 正在获取歌曲评论... ]"));
            UpdateListSource();
            return;
        }

        if (_isError && _hotComments.Count == 0 && _normalComments.Count == 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  [ 评论加载失败，按 R 重试 ]"));
            UpdateListSource();
            return;
        }

        if (!_isLoading && _hotComments.Count == 0 && _normalComments.Count == 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  [ 本曲暂无评论，来留下第一条吧 ]"));
            UpdateListSource();
            return;
        }

        // 1. 精彩热评区（默认只展示前 3 条，超过 3 条支持折叠与展开）
        if (_hotComments.Count > 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.Header, $"── 精彩热评 (共 {_hotComments.Count} 条) ──"));

            var displayedHot = (_isHotExpanded || _hotComments.Count <= 3)
                ? _hotComments
                : _hotComments.Take(3).ToList();

            foreach (var hot in displayedHot)
            {
                AppendCommentItem(hot, usableWidth, isHot: true);
            }

            if (_hotComments.Count > 3)
            {
                var toggleText = _isHotExpanded
                    ? "  [▲ 收起精彩评论]"
                    : $"  [▼ 展开更多精彩评论 (还有 {_hotComments.Count - 3} 条)]";
                _displayItems.Add(new DisplayItem(ItemType.HotToggle, toggleText));
                _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, ""));
            }
        }

        // 2. 最新评论流
        if (_normalComments.Count > 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.Header, $"── 最新评论 (共 {_totalCommentCount} 条) ──"));

            foreach (var c in _normalComments)
            {
                AppendCommentItem(c, usableWidth, isHot: false);
            }
        }

        // 3. 底部状态指示
        if (_isLoading)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  [ 正在加载更多评论... ]"));
        }
        else if (!_hasMore && _normalComments.Count > 0)
        {
            _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "  ── 已加载全部评论 ──"));
        }

        UpdateListSource();
    }

    private void AppendCommentItem(SongComment c, int usableWidth, bool isHot)
    {
        // 头部信息行：[热评] 昵称 [IP属地] · 时间   [👍 赞数]
        var metaBuilder = new StringBuilder();
        if (isHot)
        {
            metaBuilder.Append("[热评] ");
        }
        metaBuilder.Append(c.Nick);
        if (!string.IsNullOrEmpty(c.Location))
        {
            metaBuilder.Append($" [{c.Location}]");
        }
        metaBuilder.Append($" · {FormatTimestamp(c.TimeSec)}");
        if (c.PraiseNum > 0)
        {
            metaBuilder.Append($"  [👍 {FormatCount(c.PraiseNum)}]");
        }

        _displayItems.Add(new DisplayItem(ItemType.CommentMeta, metaBuilder.ToString(), c, isHot));

        // 正文多行折行
        var wrappedLines = WrapText(c.Content, usableWidth);
        foreach (var line in wrappedLines)
        {
            _displayItems.Add(new DisplayItem(ItemType.CommentContent, $"  {line}", c, isHot));
        }

        // 图片标签
        if (!string.IsNullOrEmpty(c.PicUrl))
        {
            var picText = TerminalImageHelper.IsImageSupported
                ? "  [📷 评论配图]"
                : "  [图片]";
            _displayItems.Add(new DisplayItem(ItemType.CommentPic, picText, c, isHot));
        }

        // 评论之间空一行分隔
        _displayItems.Add(new DisplayItem(ItemType.EmptyOrStatus, "", c, isHot));
    }

    private void UpdateListSource()
    {
        var textList = _displayItems.Select(d => d.Text).ToList();
        var prevY = _listView.Viewport.Y;
        var prevSel = _listView.SelectedItem;
        _listView.SetSource(new ObservableCollection<string>(textList));
        if (prevY > 0 && prevY < textList.Count)
        {
            _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, prevY, _listView.Viewport.Width, _listView.Viewport.Height);
        }
        if (prevSel.HasValue && prevSel.Value < textList.Count)
        {
            _listView.SelectedItem = prevSel.Value;
        }
        _scrollBar.UpdateMetrics(textList.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        SetNeedsDraw();
    }

    private void CheckTriggerLoadMore()
    {
        if (!_hasMore || _isLoading || _displayItems.Count == 0) return;

        int current = _listView.SelectedItem ?? 0;
        int viewBottom = _listView.Viewport.Y + _listView.Viewport.Height;

        if (current >= _displayItems.Count - 8 || viewBottom >= _displayItems.Count - 6)
        {
            _ = LoadCommentsAsync(isInitial: false);
        }
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        var rawLines = text.Split(s_lineSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in rawLines)
        {
            var curLine = new StringBuilder();
            int curWidth = 0;
            foreach (var rune in raw.EnumerateRunes())
            {
                int w = rune.Value > 127 ? 2 : 1;
                if (curWidth + w > maxWidth)
                {
                    lines.Add(curLine.ToString());
                    curLine.Clear();
                    curWidth = 0;
                }
                curLine.Append(rune.ToString());
                curWidth += w;
            }
            if (curLine.Length > 0)
            {
                lines.Add(curLine.ToString());
            }
        }
        return lines;
    }

    private static string FormatCount(int count)
    {
        if (count >= 100_000_000) return $"{count / 100_000_000.0:F1}亿";
        if (count >= 10_000) return $"{count / 10_000.0:F1}万";
        return count.ToString();
    }

    private static string FormatTimestamp(long timestampSec)
    {
        if (timestampSec <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestampSec).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "";
        }
    }

    public void ScrollToTop()
    {
        if (_displayItems.Count > 0)
        {
            _listView.SelectedItem = 0;
            _listView.Viewport = new System.Drawing.Rectangle(0, 0, _listView.Viewport.Width, _listView.Viewport.Height);
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, 0);
            _listView.SetFocus();
            SetNeedsDraw();
        }
    }
}
