using System.Collections.ObjectModel;
using System.Globalization;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace QmTui.UI;

/// <summary>
/// 待播播放队列抽屉对话框，用于查看、即时插播与整理后续曲目
/// </summary>
public sealed class QueueDrawerDialog : Dialog
{
    private readonly ListView _queueListView;
    private readonly Label _currentPlayingLabel;
    private List<Song> _cachedSongs = [];

    private long _lastClickTick;
    private int _lastClickRow = -1;

    public Song? SelectedSongToPlay { get; private set; }

    private static Scheme TransparentDialogScheme => MikuTheme.TransparentDialog;

    public QueueDrawerDialog()
    {
        Title = "播放列表";
        Width = 76;
        Height = 18;
        SetScheme(TransparentDialogScheme);

        _currentPlayingLabel = new Label
        {
            X = 2,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 1
        };
        _currentPlayingLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(_currentPlayingLabel);

        _queueListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2)
        };
        _queueListView.SetScheme(TransparentDialogScheme);
        _queueListView.KeyBindings.Remove(Key.Space);
        Add(_queueListView);

        Button CreateActionButton(string text, Pos x, Action action)
        {
            var btn = new Button
            {
                Text = text,
                X = x,
                Y = Pos.AnchorEnd(1),
                NoDecorations = true,
                ShadowStyle = ShadowStyles.None,
                CanFocus = false
            };
            btn.TabStop = TabBehavior.NoStop;
            btn.KeyBindings.Remove(Key.Space);
            btn.SetScheme(MikuTheme.SearchCategoryDim);
            btn.Accepting += (s, e) => action();
            return btn;
        }

        var playBtn = CreateActionButton("[Enter] 播放", 2, () =>
        {
            int idx = _queueListView.SelectedItem ?? -1;
            PlaySongAtIndex(idx);
        });
        var removeBtn = CreateActionButton("[Del] 移除选中", Pos.Right(playBtn) + 2, RemoveCurrentSelectedItem);
        var clearBtn = CreateActionButton("[C] 清空列表", Pos.Right(removeBtn) + 2, ConfirmAndClearUpcoming);
        var closeBtn = CreateActionButton("[Esc] 关闭", Pos.Right(clearBtn) + 2, () => Application.RequestStop(this));

        Add(playBtn, removeBtn, clearBtn, closeBtn);

        ReloadQueueData();

        // 原生激活事件（按回车或终端双击）触发播放
        _queueListView.Accepted += (s, e) =>
        {
            int idx = _queueListView.SelectedItem ?? -1;
            PlaySongAtIndex(idx);
        };

        // 鼠标单击与双击交互：支持原生双击标志与 400ms 快速连击判定（适配 Linux 终端驱动）
        _queueListView.MouseEvent += (s, m) =>
        {
            if (m.Position.HasValue && (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked)))
            {
                var clickedRow = _queueListView.Viewport.Y + m.Position.Value.Y;
                bool isDoubleClick = m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);

                if (!isDoubleClick && m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
                {
                    var now = Environment.TickCount64;
                    if (now - _lastClickTick < 400 && _lastClickRow == clickedRow)
                    {
                        isDoubleClick = true;
                        _lastClickTick = 0;
                    }
                    else
                    {
                        _lastClickTick = now;
                        _lastClickRow = clickedRow;
                    }
                }

                if (isDoubleClick)
                {
                    PlaySongAtIndex(clickedRow);
                }
            }
        };

        // 统一按键分发逻辑，直接在 ListView 与 Dialog 双重拦截，防止 ListView 字符搜索吞噬按键
        void HandleKeyAction(Key k)
        {
            if (!MainWindow.IsTerminalWindowFocused)
            {
                k.Handled = true;
                return;
            }

            bool isClose = k == Key.Esc || k == Key.E || k == Key.E.WithShift ||
                           k.AsRune.Value == 'e' || k.AsRune.Value == 'E';
            if (isClose)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                int idx = _queueListView.SelectedItem ?? -1;
                PlaySongAtIndex(idx);
                return;
            }

            if (k == Key.DeleteChar)
            {
                k.Handled = true;
                RemoveCurrentSelectedItem();
                return;
            }

            bool isC = k == Key.C || k == Key.C.WithShift ||
                       k.AsRune.Value == 'c' || k.AsRune.Value == 'C';
            if (isC)
            {
                k.Handled = true;
                ConfirmAndClearUpcoming();
                return;
            }
        }

        _queueListView.KeyDown += (s, k) => HandleKeyAction(k);
        KeyDown += (s, k) =>
        {
            if (k.Handled) return;
            HandleKeyAction(k);
        };
    }

    private void ConfirmAndClearUpcoming()
    {
        if (_cachedSongs.Count == 0) return;

        var confirmDlg = new Dialog
        {
            Title = "清空确认",
            Width = 46,
            Height = 8,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        confirmDlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = "确定要清空待播歌曲列表吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(TransparentDialogScheme);
        confirmDlg.Add(msg);

        var okBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        okBtn.SetScheme(TransparentDialogScheme);

        var cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);

        okBtn.Accepting += (s, e) =>
        {
            Application.RequestStop(confirmDlg);
            PlaybackQueueService.Instance.ClearUpcoming();
            ReloadQueueData();
        };

        cancelBtn.Accepting += (s, e) =>
        {
            Application.RequestStop(confirmDlg);
        };

        confirmDlg.KeyDown += (s, k) =>
        {
            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                Application.RequestStop(confirmDlg);
                PlaybackQueueService.Instance.ClearUpcoming();
                ReloadQueueData();
            }
            else if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(confirmDlg);
            }
        };

        confirmDlg.Add(okBtn, cancelBtn);
        okBtn.SetFocus();
        Application.Run(confirmDlg);
    }

    private void PlaySongAtIndex(int idx)
    {
        if (idx >= 0 && idx < _cachedSongs.Count)
        {
            SelectedSongToPlay = _cachedSongs[idx];
            PlaybackQueueService.Instance.SetCurrentIndex(idx);
            Application.RequestStop(this);
        }
    }

    private void ReloadQueueData()
    {
        var queue = PlaybackQueueService.Instance;
        _cachedSongs = queue.ActiveSongs.ToList();
        int curIdx = queue.CurrentIndex;
        var curSong = queue.CurrentSong;

        Title = $"播放列表 (共 {_cachedSongs.Count} 首)";

        if (curSong != null)
        {
            _currentPlayingLabel.Text = $"▶ 正在播放: {curSong.Title} - {curSong.Artist}";
        }
        else
        {
            _currentPlayingLabel.Text = "  队列暂无正在播放的歌曲";
        }

        if (_cachedSongs.Count == 0)
        {
            _queueListView.SetSource(new ObservableCollection<string>(["  (播放队列为空，在主列表按回车起播或按 N 插队)"]));
            return;
        }

        var displayItems = new List<string>(_cachedSongs.Count);
        for (int i = 0; i < _cachedSongs.Count; i++)
        {
            var s = _cachedSongs[i];
            string marker = (i == curIdx) ? "▶ " : "  ";
            string titlePart = s.Title.TruncateAndPadWide(34);
            string artistPart = s.Artist.TruncateAndPadWide(24);
            displayItems.Add($"{marker}{i + 1,2}. {titlePart} - {artistPart}");
        }

        _queueListView.SetSource(new ObservableCollection<string>(displayItems));
        if (curIdx >= 0 && curIdx < displayItems.Count)
        {
            _queueListView.SelectedItem = curIdx;
        }
    }

    public void RemoveCurrentSelectedItem()
    {
        int idx = _queueListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _cachedSongs.Count)
        {
            PlaybackQueueService.Instance.RemoveAt(idx);
            ReloadQueueData();
            if (idx < _cachedSongs.Count)
            {
                _queueListView.SelectedItem = idx;
            }
            else if (_cachedSongs.Count > 0)
            {
                _queueListView.SelectedItem = _cachedSongs.Count - 1;
            }
        }
    }

    public void ClearUpcomingSongs()
    {
        PlaybackQueueService.Instance.ClearUpcoming();
        ReloadQueueData();
    }

    public void PlayCurrentSelectedItem()
    {
        int idx = _queueListView.SelectedItem ?? -1;
        PlaySongAtIndex(idx);
    }
}
