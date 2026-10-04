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
    private readonly View _previewOverlay;
    private readonly FrameView _previewBox;
    private readonly Label _previewMaskLabel;
    private readonly Label _previewLoadingLabel;
    private readonly Label _previewHintLabel;

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
    private object? _previewRenderTimerToken;
    private SongComment? _previewingComment;
    private int _lastClickedItemIndex = -1;
    private long _lastClickTicks;

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
            bool isSelected = _listView.SelectedItem == e.Row;

            if (isSelected)
            {
                var fg = (item.Type == ItemType.CommentPic || item.Type == ItemType.HotToggle)
                    ? MikuTheme.QqGreenLight
                    : Color.White;
                e.RowAttribute = new Attribute(fg, MikuTheme.QqGreenDark);
                return;
            }

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

        _scrollBar = new ThinScrollBarView
        {
            X = Pos.AnchorEnd(1),
            Y = 0,
            Width = 1,
            Height = Dim.Fill()
        };
        _scrollBar.ScrollPositionChanged += targetRow =>
        {
            int count = _displayItems.Count;
            if (count > 0)
            {
                int clamped = Math.Clamp(targetRow, 0, count - 1);
                _listView.SelectedItem = clamped;
                _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, clamped, _listView.Viewport.Width, _listView.Viewport.Height);
                _scrollBar.TriggerActivity();
                _scrollBar.UpdateMetrics(count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
            }
        };

        _listView.ValueChanged += (s, e) =>
        {
            _scrollBar.TriggerActivity();
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
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
                if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
                {
                    ShowCommentImagePreview(item.Comment);
                    e.Handled = true;
                    return;
                }
            }
        };

        _listView.KeyDown += (s, k) =>
        {
            var ch = char.ToUpperInvariant((char)k.AsRune.Value);
            if (ch == 'I' || k == Key.I)
            {
                var target = GetTargetCommentForPreview(out int targetRowIdx);
                if (target != null)
                {
                    if (targetRowIdx >= 0 && targetRowIdx < _displayItems.Count)
                    {
                        _listView.SelectedItem = targetRowIdx;
                    }
                    ShowCommentImagePreview(target);
                    k.Handled = true;
                    return;
                }
            }

            if (k == Key.CursorDown || k == Key.PageDown || k == Key.End)
            {
                _scrollBar.TriggerActivity();
                _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
            }
            else if (k == Key.CursorUp || k == Key.PageUp || k == Key.Home)
            {
                _scrollBar.TriggerActivity();
                _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            }
        };

        _listView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.WheeledDown))
            {
                _scrollBar.TriggerActivity();
                _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
                CheckTriggerLoadMore();
                EnsureSelectedItemInViewport();
            }
            else if (m.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                _scrollBar.TriggerActivity();
                _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
                EnsureSelectedItemInViewport();
            }

            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                int clickedRow = (m.Position?.Y ?? 0) + _listView.Viewport.Y;
                if (clickedRow >= 0 && clickedRow < _displayItems.Count)
                {
                    var item = _displayItems[clickedRow];
                    if (item.Type == ItemType.HotToggle)
                    {
                        _isHotExpanded = !_isHotExpanded;
                        RebuildDisplayItems();
                        m.Handled = true;
                        return;
                    }

                    var now = Environment.TickCount64;
                    bool isSecondClick = (clickedRow == _lastClickedItemIndex && (now - _lastClickTicks < 600 || _listView.SelectedItem == clickedRow));
                    _lastClickedItemIndex = clickedRow;
                    _lastClickTicks = now;
                    _listView.SelectedItem = clickedRow;

                    if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
                    {
                        if (item.Type == ItemType.CommentPic || isSecondClick)
                        {
                            ShowCommentImagePreview(item.Comment);
                            m.Handled = true;
                            return;
                        }
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

        // 大图预览浮层
        _previewOverlay = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false,
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        _previewOverlay.SetScheme(new Scheme
        {
            Normal = new Attribute(Color.White, Color.Black),
            Focus = new Attribute(Color.White, Color.Black)
        });

        _previewBox = new FrameView
        {
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = 42,
            Height = 18,
            Title = "📷 评论配图",
            BorderStyle = LineStyle.Rounded
        };
        _previewBox.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.QqGreenPrimary, Color.Black),
            Focus = new Attribute(MikuTheme.QqGreenPrimary, Color.Black)
        });

        _previewMaskLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "",
            CanFocus = false
        };
        _previewMaskLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(Color.Black, Color.Black)
        });

        _previewLoadingLabel = new Label
        {
            Text = "正在加载配图...",
            X = Pos.Center(),
            Y = Pos.Center(),
            Visible = false
        };
        _previewLoadingLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.QqTextLyricDim, Color.Black)
        });

        _previewHintLabel = new Label
        {
            Text = "[ 点击任意处或按 Esc / I 关闭 ]",
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1),
            Visible = true
        };
        _previewHintLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.QqTextLyricDim, Color.Black)
        });

        _previewBox.Add(_previewMaskLabel, _previewLoadingLabel, _previewHintLabel);
        _previewOverlay.Add(_previewBox);

        _previewOverlay.KeyDown += (s, k) =>
        {
            CloseCommentImagePreview();
            k.Handled = true;
        };
        _previewOverlay.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                CloseCommentImagePreview();
                m.Handled = true;
            }
        };
        Add(_previewOverlay);

        // 快捷键处理
        KeyDown += (s, k) =>
        {
            if (_previewOverlay.Visible)
            {
                CloseCommentImagePreview();
                k.Handled = true;
                return;
            }

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
        _scrollBar.TriggerActivity();
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
        CloseCommentImagePreview();
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
                ? "  [📷 评论配图 (按 I 或点击查看)]"
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
            _scrollBar.TriggerActivity();
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, 0);
            _listView.SetFocus();
            SetNeedsDraw();
        }
    }

    private void EnsureSelectedItemInViewport()
    {
        int viewTop = _listView.Viewport.Y;
        int viewHeight = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : Frame.Height;
        int curSel = _listView.SelectedItem ?? -1;
        if (curSel < viewTop || curSel >= viewTop + viewHeight)
        {
            int center = Math.Clamp(viewTop + viewHeight / 2, 0, Math.Max(0, _displayItems.Count - 1));
            _listView.SelectedItem = center;
        }
    }

    private SongComment? GetTargetCommentForPreview(out int targetRowIdx)
    {
        targetRowIdx = -1;
        if (_displayItems.Count == 0) return null;

        int selIdx = _listView.SelectedItem ?? -1;
        int viewTop = _listView.Viewport.Y;
        int viewHeight = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : Frame.Height;

        // 1. 如果当前光标在视口内且当前行属于带图评论
        if (selIdx >= viewTop && selIdx < viewTop + viewHeight && selIdx >= 0 && selIdx < _displayItems.Count)
        {
            var curItem = _displayItems[selIdx];
            if (curItem.Comment != null && !string.IsNullOrEmpty(curItem.Comment.PicUrl))
            {
                targetRowIdx = FindCommentPicRowIndex(curItem.Comment, selIdx);
                return curItem.Comment;
            }
        }

        // 2. 搜索当前视口可见区域内的所有带图项
        int minIdx = Math.Max(0, viewTop);
        int maxIdx = Math.Min(_displayItems.Count - 1, viewTop + viewHeight - 1);

        // 2.1 若光标在视口内，找视口内离光标最近的带图项
        if (selIdx >= minIdx && selIdx <= maxIdx)
        {
            SongComment? nearest = null;
            int minDistance = int.MaxValue;
            int foundRow = -1;
            for (int i = minIdx; i <= maxIdx; i++)
            {
                var item = _displayItems[i];
                if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
                {
                    int dist = Math.Abs(i - selIdx);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        nearest = item.Comment;
                        foundRow = i;
                    }
                }
            }
            if (nearest != null)
            {
                targetRowIdx = FindCommentPicRowIndex(nearest, foundRow);
                return nearest;
            }
        }

        // 2.2 若光标不在当前视口内，从视口顶部向下取第一张可见配图
        for (int i = minIdx; i <= maxIdx; i++)
        {
            var item = _displayItems[i];
            if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
            {
                targetRowIdx = FindCommentPicRowIndex(item.Comment, i);
                return item.Comment;
            }
        }

        // 2.3 全局保底
        if (selIdx >= 0 && selIdx < _displayItems.Count)
        {
            var item = _displayItems[selIdx];
            if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
            {
                targetRowIdx = FindCommentPicRowIndex(item.Comment, selIdx);
                return item.Comment;
            }
        }

        return null;
    }

    private int FindCommentPicRowIndex(SongComment comment, int fallbackIdx)
    {
        for (int i = Math.Max(0, fallbackIdx - 6); i <= Math.Min(_displayItems.Count - 1, fallbackIdx + 6); i++)
        {
            if (_displayItems[i].Type == ItemType.CommentPic && _displayItems[i].Comment?.CommentId == comment.CommentId)
            {
                return i;
            }
        }
        return fallbackIdx;
    }

    private (int targetCols, int targetRows) CalculateAdaptiveImageDimensions(string? localPath)
    {
        int maxR = Math.Clamp(Frame.Height - 6, 8, 22);
        int maxC = Math.Clamp(Frame.Width - 6, 16, 56);

        var dims = TerminalImageHelper.GetImageDimensions(localPath);
        if (dims != null && dims.Value.width > 0 && dims.Value.height > 0)
        {
            // 物理像素宽高在终端 1:2 字符比例下的自然字符宽高比
            double cellAspect = (double)dims.Value.width / dims.Value.height * 2.0;

            int targetRows = maxR;
            int targetCols = (int)Math.Round(targetRows * cellAspect);

            if (targetCols > maxC)
            {
                targetCols = maxC;
                targetRows = (int)Math.Round(targetCols / cellAspect);
            }

            targetCols = Math.Clamp(targetCols, 16, maxC);
            targetRows = Math.Clamp(targetRows, 6, maxR);
            return (targetCols, targetRows);
        }

        return (Math.Min(maxC, 38), Math.Min(maxR, 14));
    }

    private void UpdatePreviewBoxSizeForImage(string? localPath)
    {
        var (targetCols, targetRows) = CalculateAdaptiveImageDimensions(localPath);

        int boxW = targetCols + 2;
        int boxH = targetRows + 3;

        _previewBox.Width = boxW;
        _previewBox.Height = boxH;

        var sb = new StringBuilder();
        var rowSpaces = new string(' ', targetCols);
        for (int r = 0; r < targetRows + 1; r++)
        {
            sb.AppendLine(rowSpaces);
        }
        _previewMaskLabel.Text = sb.ToString();
    }

    private void ShowCommentImagePreview(SongComment comment)
    {
        if (!TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(comment.PicUrl)) return;

        _previewingComment = comment;
        _previewBox.Title = $"📷 {comment.Nick} 的配图";

        var localPath = TerminalImageHelper.GetCommentImageLocalPath(comment.CommentId);
        UpdatePreviewBoxSizeForImage(localPath);

        _previewOverlay.Visible = true;
        _previewLoadingLabel.Visible = true;
        _previewOverlay.SetFocus();
        SetNeedsDraw();

        if (_previewRenderTimerToken != null)
        {
            Application.RemoveTimeout(_previewRenderTimerToken);
            _previewRenderTimerToken = null;
        }

        _previewRenderTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(80), () =>
        {
            _previewRenderTimerToken = null;
            if (_previewOverlay.Visible && _previewingComment == comment)
            {
                RenderPreviewImage(comment);
            }
            return false;
        });
    }

    private void CloseCommentImagePreview()
    {
        if (_previewRenderTimerToken != null)
        {
            Application.RemoveTimeout(_previewRenderTimerToken);
            _previewRenderTimerToken = null;
        }
        TerminalImageHelper.DeleteKittyImage(TerminalImageHelper.ImageIdCommentPreview);
        _previewOverlay.Visible = false;
        _previewingComment = null;
        _listView.SetFocus();
        SetNeedsDraw();
    }

    private void RenderPreviewImage(SongComment comment)
    {
        if (!_previewOverlay.Visible || string.IsNullOrEmpty(comment.PicUrl))
        {
            TerminalImageHelper.DeleteKittyImage(TerminalImageHelper.ImageIdCommentPreview);
            return;
        }

        try
        {
            var localPath = TerminalImageHelper.GetCommentImageLocalPath(comment.CommentId);
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                _previewLoadingLabel.Visible = false;

                UpdatePreviewBoxSizeForImage(localPath);
                var (targetCols, targetRows) = CalculateAdaptiveImageDimensions(localPath);

                var origin = _previewBox.FrameToScreen();
                int renderCol = origin.X + 2;
                int renderRow = origin.Y + 2;

                TerminalImageHelper.RenderKittyImage(
                    localPath,
                    renderCol,
                    renderRow,
                    cols: targetCols,
                    rows: targetRows,
                    imageId: TerminalImageHelper.ImageIdCommentPreview
                );
            }
            else
            {
                _previewLoadingLabel.Visible = true;
                _ = Task.Run(async () =>
                {
                    var downloaded = await TerminalImageHelper.EnsureCommentImageDownloadedAsync(comment.PicUrl, comment.CommentId).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(downloaded))
                    {
                        Application.Invoke(() =>
                        {
                            if (_previewOverlay.Visible && _previewingComment == comment)
                            {
                                RenderPreviewImage(comment);
                            }
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SongCommentView", $"RenderPreviewImage failed: {ex.Message}");
        }
    }
}
