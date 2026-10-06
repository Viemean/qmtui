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
    private long _lastPreviewCloseTick;

    public event Action? CloseRequested;
    public event Action<bool>? TabNavigationRequested;
    public event Action<int>? TotalCommentCountChanged;

    public int TotalCommentCount => _totalCommentCount;
    public bool IsLoading => _isLoading;
    public bool IsImagePreviewActive => _previewOverlay?.Visible ?? false;
    public long LastPreviewCloseTick => _lastPreviewCloseTick;
    public bool HasActiveFocus => HasFocus || _listView.HasFocus || (_previewOverlay?.HasFocus ?? false);

    public new bool SetFocus()
    {
        Visible = true;
        bool listFocus = _listView.SetFocus();
        _scrollBar.TriggerActivity();
        SetNeedsDraw();
        AppLogger.Force("SongCommentView", $"SetFocus: _listView.SetFocus()={listFocus}, _listView.HasFocus={_listView.HasFocus}, HasActiveFocus={HasActiveFocus}, SuperView.CanFocus={SuperView?.CanFocus}, Visible={Visible}");
        return _listView.HasFocus;
    }

    private static SongCommentSnapshot? _sharedSnapshot;

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
        _listView.KeyBindings.Remove(Key.Tab);
        _listView.KeyBindings.Remove(Key.Tab.WithShift);
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
                var bg = HasActiveFocus ? MikuTheme.QqGreenDark : Color.None;
                e.RowAttribute = new Attribute(fg, bg);
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

        _listView.HasFocusChanged += (s, e) => SetNeedsDraw();
        HasFocusChanged += (s, e) => SetNeedsDraw();

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
            AppLogger.Force("SongCommentView", $"_listView.KeyDown: key={k}, SelectedItem={_listView.SelectedItem}, HasActiveFocus={HasActiveFocus}");

            var ch = char.ToUpperInvariant((char)k.AsRune.Value);
            if (k == Key.Esc || ch == 'C' || k == Key.C)
            {
                if (_previewOverlay?.Visible == true)
                {
                    CloseCommentImagePreview();
                    k.Handled = true;
                    return;
                }

                if (Environment.TickCount64 - _lastPreviewCloseTick < 350)
                {
                    k.Handled = true;
                    return;
                }

                CloseRequested?.Invoke();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorDown)
            {
                NavigateToNextSelectableItem(forward: true);
                k.Handled = true;
                return;
            }

            if (k == Key.CursorUp)
            {
                NavigateToNextSelectableItem(forward: false);
                k.Handled = true;
                return;
            }

            if (k == Key.CursorLeft || k == Key.PageUp)
            {
                PageNavigate(forward: false);
                k.Handled = true;
                return;
            }

            if (k == Key.CursorRight || k == Key.PageDown)
            {
                PageNavigate(forward: true);
                k.Handled = true;
                return;
            }

            if (k == Key.Home)
            {
                ScrollToTop();
                k.Handled = true;
                return;
            }

            if (k == Key.End)
            {
                ScrollToEnd();
                k.Handled = true;
                return;
            }

            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                TabNavigationRequested?.Invoke(!k.IsShift);
                k.Handled = true;
                return;
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
                    _listView.SetFocus();

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
            Width = 38,
            Height = 7,
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
            Text = "[ 点击任意处或按 Esc / Enter 关闭 ]",
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

            if (Environment.TickCount64 - _lastPreviewCloseTick < 350)
            {
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

            if (k == Key.CursorUp)
            {
                MovePrevious();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorDown)
            {
                MoveNext();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorLeft || k == Key.PageUp)
            {
                PagePrevious();
                k.Handled = true;
                return;
            }

            if (k == Key.CursorRight || k == Key.PageDown)
            {
                PageNext();
                k.Handled = true;
                return;
            }

            if (k == Key.End)
            {
                ScrollToEnd();
                k.Handled = true;
                return;
            }

            if (k == Key.Enter)
            {
                ActivateSelected();
                k.Handled = true;
                return;
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
        if (_currentSong?.Mid == song?.Mid && song != null && (_hotComments.Count > 0 || _normalComments.Count > 0 || _isLoading))
        {
            return;
        }

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch { }
        _loadCts = null;

        _currentSong = song;

        if (song != null && _sharedSnapshot != null && _sharedSnapshot.Song?.Mid == song.Mid &&
            (_sharedSnapshot.HotComments.Count > 0 || _sharedSnapshot.NormalComments.Count > 0))
        {
            RestoreSnapshot(_sharedSnapshot);
            return;
        }

        if (_sharedSnapshot != null && _sharedSnapshot.Song?.Mid != song?.Mid)
        {
            _sharedSnapshot = null;
        }

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
        TotalCommentCountChanged?.Invoke(0);

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
        _sharedSnapshot = null;
        TotalCommentCountChanged?.Invoke(0);
        await LoadCommentsAsync(isInitial: true).ConfigureAwait(false);
    }

    public void OnActivated()
    {
        Visible = true;
        SetFocus();
        _scrollBar.TriggerActivity();

        if (_currentSong != null && _sharedSnapshot != null && _sharedSnapshot.Song?.Mid == _currentSong.Mid &&
            (_sharedSnapshot.HotComments.Count > 0 || _sharedSnapshot.NormalComments.Count > 0))
        {
            if (_normalComments.Count == 0 || _sharedSnapshot.NormalComments.Count > _normalComments.Count || _sharedSnapshot.ViewportY != _listView.Viewport.Y)
            {
                RestoreSnapshot(_sharedSnapshot);
                return;
            }
        }

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
        catch { }

        if (_currentSong != null && (_hotComments.Count > 0 || _normalComments.Count > 0))
        {
            _sharedSnapshot = CreateSnapshot();
        }
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
        catch { }
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        RebuildDisplayItems();

        try
        {
            if (song.Id <= 0 && !string.IsNullOrEmpty(song.Mid))
            {
                try
                {
                    var resolvedId = await MusicApi.ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
                    if (resolvedId > 0)
                    {
                        song.Id = resolvedId;
                    }
                }
                catch { }
            }

            CommentPage? pageResult = null;
            int maxAttempts = isInitial ? 3 : 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    pageResult = await MusicApi.GetSongCommentsAsync(
                        song.Id,
                        song.Mid,
                        targetPage,
                        pageSize: 25,
                        lastCommentSeqNo: isInitial ? "" : _lastSeqNo,
                        ct: ct
                    ).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) return;

                    if (pageResult != null)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("SongCommentView", $"LoadCommentsAsync attempt {attempt}/{maxAttempts} error for {song.Title}: {ex.Message}");
                }

                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(attempt * 400, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            if (ct.IsCancellationRequested) return;

            Application.Invoke(() =>
            {
                if (pageResult != null)
                {
                    _totalCommentCount = pageResult.TotalCount;
                    TotalCommentCountChanged?.Invoke(_totalCommentCount);
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
                if (_currentSong != null && (_hotComments.Count > 0 || _normalComments.Count > 0))
                {
                    _sharedSnapshot = CreateSnapshot();
                }
                else if (_sharedSnapshot?.Song?.Mid == _currentSong?.Mid && _hotComments.Count == 0 && _normalComments.Count == 0)
                {
                    _sharedSnapshot = null;
                }
            });
        }
        catch (OperationCanceledException) { }
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
        // 头部信息行：[热评] 昵称 [IP属地] · 时间   [点赞数]
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
                ? "  [📷 评论配图 (按 Enter 或点击查看)]"
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

    public static string FormatCount(int count)
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

    public void ScrollToEnd()
    {
        if (_displayItems.Count > 0)
        {
            int last = _displayItems.Count - 1;
            for (int i = last; i >= 0; i--)
            {
                if (IsSelectableCommentRow(_displayItems[i].Type))
                {
                    last = i;
                    break;
                }
            }
            _listView.SelectedItem = last;
            EnsureRowVisibleInViewport(last);
            _scrollBar.TriggerActivity();
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            CheckTriggerLoadMore();
            SetNeedsDraw();
        }
    }

    private static bool IsSelectableCommentRow(ItemType type) =>
        type == ItemType.CommentMeta || type == ItemType.CommentPic || type == ItemType.HotToggle;

    private void EnsureRowVisibleInViewport(int row)
    {
        int viewTop = _listView.Viewport.Y;
        int viewHeight = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : Frame.Height;

        if (row < viewTop)
        {
            _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, row, _listView.Viewport.Width, _listView.Viewport.Height);
        }
        else if (row >= viewTop + viewHeight)
        {
            int newY = row - viewHeight + 1;
            _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, Math.Max(0, newY), _listView.Viewport.Width, _listView.Viewport.Height);
        }
    }

    private void NavigateToNextSelectableItem(bool forward)
    {
        if (_displayItems.Count == 0) return;

        int cur = _listView.SelectedItem ?? 0;
        int next = cur;

        if (forward)
        {
            for (int i = cur + 1; i < _displayItems.Count; i++)
            {
                if (IsSelectableCommentRow(_displayItems[i].Type))
                {
                    next = i;
                    break;
                }
            }
        }
        else
        {
            for (int i = cur - 1; i >= 0; i--)
            {
                if (IsSelectableCommentRow(_displayItems[i].Type))
                {
                    next = i;
                    break;
                }
            }
        }

        if (next != cur)
        {
            _listView.SelectedItem = next;
            EnsureRowVisibleInViewport(next);
            _scrollBar.TriggerActivity();
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
            if (forward) CheckTriggerLoadMore();
            SetNeedsDraw();
        }
    }

    private void PageNavigate(bool forward)
    {
        if (_displayItems.Count == 0) return;

        int viewHeight = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : Frame.Height;
        int step = Math.Max(2, viewHeight - 2);
        int cur = _listView.SelectedItem ?? 0;
        int target = forward ? Math.Min(_displayItems.Count - 1, cur + step) : Math.Max(0, cur - step);

        int best = target;
        int minDiff = int.MaxValue;
        for (int i = Math.Max(0, target - 6); i <= Math.Min(_displayItems.Count - 1, target + 6); i++)
        {
            if (IsSelectableCommentRow(_displayItems[i].Type))
            {
                int diff = Math.Abs(i - target);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    best = i;
                }
            }
        }

        _listView.SelectedItem = best;
        EnsureRowVisibleInViewport(best);
        _scrollBar.TriggerActivity();
        _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        if (forward) CheckTriggerLoadMore();
        SetNeedsDraw();
    }

    private void EnsureSelectedItemInViewport()
    {
        int viewTop = _listView.Viewport.Y;
        int viewHeight = _listView.Viewport.Height > 0 ? _listView.Viewport.Height : Frame.Height;
        int curSel = _listView.SelectedItem ?? -1;
        if (curSel < viewTop || curSel >= viewTop + viewHeight)
        {
            int center = Math.Clamp(viewTop + viewHeight / 2, 0, Math.Max(0, _displayItems.Count - 1));
            for (int i = center; i < Math.Min(_displayItems.Count, center + 4); i++)
            {
                if (IsSelectableCommentRow(_displayItems[i].Type))
                {
                    center = i;
                    break;
                }
            }
            _listView.SelectedItem = center;
        }
    }

    public void MoveNext() => NavigateToNextSelectableItem(forward: true);
    public void MovePrevious() => NavigateToNextSelectableItem(forward: false);
    public void PageNext() => PageNavigate(forward: true);
    public void PagePrevious() => PageNavigate(forward: false);
    public void ActivateSelected()
    {
        int idx = _listView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _displayItems.Count)
        {
            var item = _displayItems[idx];
            if (item.Type == ItemType.HotToggle)
            {
                _isHotExpanded = !_isHotExpanded;
                RebuildDisplayItems();
                return;
            }
            if (item.Comment != null && !string.IsNullOrEmpty(item.Comment.PicUrl))
            {
                ShowCommentImagePreview(item.Comment);
            }
        }
    }

    private (int targetCols, int targetRows) CalculateAdaptiveImageDimensions(string? localPath)
    {
        // 尽可能占满歌曲界面（右半区）可用空间，同时受限于 Frame.Width 与 Frame.Height，绝不侵占左侧封面
        int maxC = Math.Max(16, Frame.Width - 4);
        int maxR = Math.Max(8, Frame.Height - 4);

        var dims = TerminalImageHelper.GetImageDimensions(localPath);
        if (dims != null && dims.Value.width > 0 && dims.Value.height > 0)
        {
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

        return (Math.Min(maxC, 60), Math.Min(maxR, 26));
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

    private void SetLoadingPreviewBox()
    {
        _previewBox.Width = 38;
        _previewBox.Height = 7;
        _previewMaskLabel.Text = "";
        _previewLoadingLabel.Visible = true;
        _previewOverlay.SetNeedsLayout();
    }

    private void ShowCommentImagePreview(SongComment comment)
    {
        if (!TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(comment.PicUrl)) return;

        _previewingComment = comment;
        _previewBox.Title = $"📷 {comment.Nick} 的配图";

        var localPath = TerminalImageHelper.GetCommentImageLocalPath(comment.CommentId);
        bool hasCached = File.Exists(localPath) && new FileInfo(localPath).Length > 0;

        if (hasCached)
        {
            UpdatePreviewBoxSizeForImage(localPath);
            _previewLoadingLabel.Visible = false;
        }
        else
        {
            SetLoadingPreviewBox();
        }

        _previewOverlay.Visible = true;
        _previewOverlay.SetFocus();
        _previewOverlay.SetNeedsLayout();
        SetNeedsDraw();

        if (_previewRenderTimerToken != null)
        {
            Application.RemoveTimeout(_previewRenderTimerToken);
            _previewRenderTimerToken = null;
        }

        _previewRenderTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(hasCached ? 60 : 80), () =>
        {
            _previewRenderTimerToken = null;
            if (_previewOverlay.Visible && _previewingComment == comment)
            {
                RenderPreviewImage(comment);
            }
            return false;
        });
    }

    public void CloseCommentImagePreview()
    {
        _lastPreviewCloseTick = Environment.TickCount64;
        if (_previewRenderTimerToken != null)
        {
            Application.RemoveTimeout(_previewRenderTimerToken);
            _previewRenderTimerToken = null;
        }
        TerminalImageHelper.DeleteKittyImage(TerminalImageHelper.ImageIdCommentPreview);
        if (_previewOverlay != null)
        {
            _previewOverlay.Visible = false;
        }
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
                SetLoadingPreviewBox();
                _ = Task.Run(async () =>
                {
                    var downloaded = await TerminalImageHelper.EnsureCommentImageDownloadedAsync(comment.PicUrl, comment.CommentId).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(downloaded))
                    {
                        Application.Invoke(() =>
                        {
                            if (_previewOverlay.Visible && _previewingComment == comment)
                            {
                                UpdatePreviewBoxSizeForImage(downloaded);
                                _previewOverlay.SetNeedsLayout();
                                SetNeedsDraw();

                                if (_previewRenderTimerToken != null)
                                {
                                    Application.RemoveTimeout(_previewRenderTimerToken);
                                    _previewRenderTimerToken = null;
                                }

                                _previewRenderTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(70), () =>
                                {
                                    _previewRenderTimerToken = null;
                                    if (_previewOverlay.Visible && _previewingComment == comment)
                                    {
                                        RenderPreviewImage(comment);
                                    }
                                    return false;
                                });
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

    public SongCommentSnapshot CreateSnapshot()
    {
        var snapshot = new SongCommentSnapshot
        {
            Song = _currentSong,
            TotalCommentCount = _totalCommentCount,
            CurrentPage = _currentPage,
            LastSeqNo = _lastSeqNo,
            HasMore = _hasMore,
            IsHotExpanded = _isHotExpanded,
            SelectedItemIndex = _listView.SelectedItem ?? -1,
            ViewportY = _listView.Viewport.Y,
            ScrollRatio = _displayItems.Count > 0 ? (double)_listView.Viewport.Y / _displayItems.Count : 0.0
        };

        snapshot.HotComments.AddRange(_hotComments);
        snapshot.NormalComments.AddRange(_normalComments);
        foreach (var id in _seenCommentIds)
        {
            snapshot.SeenCommentIds.Add(id);
        }

        int sel = _listView.SelectedItem ?? -1;
        if (sel >= 0 && sel < _displayItems.Count)
        {
            snapshot.SelectedCommentId = _displayItems[sel].Comment?.CommentId;
        }

        int top = _listView.Viewport.Y;
        if (top >= 0 && top < _displayItems.Count)
        {
            snapshot.TopCommentId = _displayItems[top].Comment?.CommentId;
        }

        return snapshot;
    }

    public void RestoreSnapshot(SongCommentSnapshot snapshot)
    {
        if (snapshot.Song == null) return;

        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
        catch { }
        _loadCts = null;

        _currentSong = snapshot.Song;
        _hotComments.Clear();
        _hotComments.AddRange(snapshot.HotComments);
        _normalComments.Clear();
        _normalComments.AddRange(snapshot.NormalComments);
        _seenCommentIds.Clear();
        foreach (var id in snapshot.SeenCommentIds)
        {
            _seenCommentIds.Add(id);
        }
        _totalCommentCount = snapshot.TotalCommentCount;
        _currentPage = snapshot.CurrentPage;
        _lastSeqNo = snapshot.LastSeqNo;
        _hasMore = snapshot.HasMore;
        _isHotExpanded = snapshot.IsHotExpanded;
        _isLoading = false;
        _isError = false;

        RebuildDisplayItems();

        if (_displayItems.Count > 0)
        {
            int targetY = -1;
            if (!string.IsNullOrEmpty(snapshot.TopCommentId))
            {
                for (int i = 0; i < _displayItems.Count; i++)
                {
                    if (_displayItems[i].Comment?.CommentId == snapshot.TopCommentId)
                    {
                        targetY = i;
                        break;
                    }
                }
            }
            if (targetY < 0 && snapshot.ScrollRatio > 0)
            {
                targetY = (int)Math.Round(snapshot.ScrollRatio * _displayItems.Count);
            }
            if (targetY < 0)
            {
                targetY = Math.Clamp(snapshot.ViewportY, 0, _displayItems.Count - 1);
            }

            int targetSel = -1;
            if (!string.IsNullOrEmpty(snapshot.SelectedCommentId))
            {
                for (int i = 0; i < _displayItems.Count; i++)
                {
                    if (_displayItems[i].Comment?.CommentId == snapshot.SelectedCommentId &&
                        IsSelectableCommentRow(_displayItems[i].Type))
                    {
                        targetSel = i;
                        break;
                    }
                }
            }
            if (targetSel < 0 && snapshot.SelectedItemIndex >= 0)
            {
                targetSel = Math.Clamp(snapshot.SelectedItemIndex, 0, _displayItems.Count - 1);
            }

            if (targetSel >= 0)
            {
                _listView.SelectedItem = targetSel;
            }
            _listView.Viewport = new System.Drawing.Rectangle(_listView.Viewport.X, targetY, _listView.Viewport.Width, _listView.Viewport.Height);
            _scrollBar.UpdateMetrics(_displayItems.Count, _listView.Viewport.Height, _listView.Viewport.Y);
        }

        TotalCommentCountChanged?.Invoke(_totalCommentCount);
        SetNeedsDraw();
    }

    public void SyncFrom(SongCommentView other)
    {
        if (other == null) return;
        var snapshot = other.CreateSnapshot();
        if (snapshot.Song != null && (snapshot.HotComments.Count > 0 || snapshot.NormalComments.Count > 0))
        {
            _sharedSnapshot = snapshot;
            RestoreSnapshot(snapshot);
        }
    }
}

public sealed class SongCommentSnapshot
{
    public Song? Song { get; set; }
    public List<SongComment> HotComments { get; set; } = new();
    public List<SongComment> NormalComments { get; set; } = new();
    public HashSet<string> SeenCommentIds { get; set; } = new();
    public int TotalCommentCount { get; set; }
    public int CurrentPage { get; set; }
    public string LastSeqNo { get; set; } = "";
    public bool HasMore { get; set; }
    public bool IsHotExpanded { get; set; }

    public string? SelectedCommentId { get; set; }
    public string? TopCommentId { get; set; }
    public int SelectedItemIndex { get; set; } = -1;
    public int ViewportY { get; set; }
    public double ScrollRatio { get; set; }
}
