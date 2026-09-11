using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;

namespace QmTui.UI;

/// <summary>
/// 添加到歌单对话框：参考播放队列抽屉风格，支持回车添至自建歌单及按 N 快捷新建歌单
/// </summary>
public sealed class AddToPlaylistDialog : Dialog
{
    private readonly ListView _playlistListView;
    private readonly List<Playlist> _writablePlaylists = [];
    private readonly Action<Playlist> _onSelected;
    private readonly Song _song;
    private readonly Label _currentSongLabel;

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.None)
    };

    public AddToPlaylistDialog(Song song, List<Playlist> playlists, Action<Playlist> onSelected)
    {
        _song = song;
        _onSelected = onSelected;

        Title = "添加到歌单";
        Width = 76;
        Height = 18;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        // 仅保留非“我喜欢”的自建歌单
        _writablePlaylists.AddRange(playlists.Where(p => p.IsCreated && !p.IsMyFavorite));

        _currentSongLabel = new Label
        {
            Text = $"曲目: {song.Title} - {song.Artist}",
            X = 2,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 1
        };
        _currentSongLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(_currentSongLabel);

        _playlistListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        _playlistListView.SetScheme(TransparentDialogScheme);
        _playlistListView.KeyBindings.Remove(Key.Space);
        Add(_playlistListView);

        var buttonScheme = new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None),
            HotFocus = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark)
        };

        var confirmBtn = new Button
        {
            Text = "[Enter/双击] 添加到歌单",
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = 23,
            NoDecorations = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        confirmBtn.SetScheme(buttonScheme);
        confirmBtn.Accepting += (s, e) => ConfirmSelection();
        confirmBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                ConfirmSelection();
                m.Handled = true;
            }
        };

        var createBtn = new Button
        {
            Text = "[N] 新建歌单",
            X = Pos.Right(confirmBtn) + 1,
            Y = Pos.AnchorEnd(1),
            Width = 13,
            NoDecorations = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        createBtn.SetScheme(buttonScheme);
        createBtn.Accepting += async (s, e) => await OpenCreatePlaylistDialogAsync();
        createBtn.MouseEvent += async (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                await OpenCreatePlaylistDialogAsync();
                m.Handled = true;
            }
        };

        var deleteBtn = new Button
        {
            Text = "[D] 删除歌单",
            X = Pos.Right(createBtn) + 1,
            Y = Pos.AnchorEnd(1),
            Width = 13,
            NoDecorations = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        deleteBtn.SetScheme(buttonScheme);
        deleteBtn.Accepting += async (s, e) => await HandleDeleteSelectedPlaylistAsync();
        deleteBtn.MouseEvent += async (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                await HandleDeleteSelectedPlaylistAsync();
                m.Handled = true;
            }
        };

        var cancelBtn = new Button
        {
            Text = "[Esc/A] 取消",
            X = Pos.Right(deleteBtn) + 1,
            Y = Pos.AnchorEnd(1),
            Width = 12,
            NoDecorations = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        cancelBtn.SetScheme(buttonScheme);
        cancelBtn.KeyBindings.Remove(Key.Space);
        cancelBtn.KeyBindings.Remove(Key.Esc);
        cancelBtn.Accepting += (s, e) => Application.RequestStop(this);
        cancelBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                Application.RequestStop(this);
                m.Handled = true;
            }
        };

        // 左右方向键在底部按钮之间自由选择流转，向上键跳回列表
        confirmBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorRight) { createBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _playlistListView.SetFocus(); k.Handled = true; }
        };
        createBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorLeft) { confirmBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorRight) { deleteBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _playlistListView.SetFocus(); k.Handled = true; }
        };
        deleteBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorLeft) { createBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorRight) { cancelBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _playlistListView.SetFocus(); k.Handled = true; }
        };
        cancelBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorLeft) { deleteBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _playlistListView.SetFocus(); k.Handled = true; }
        };

        _playlistListView.KeyDown += async (s, k) =>
        {
            bool isClose = k == Key.Esc || k == Key.Q || k == Key.A || k == Key.A.WithShift ||
                           k.AsRune.Value == 'a' || k.AsRune.Value == 'A' ||
                           k.AsRune.Value == 'q' || k.AsRune.Value == 'Q';
            if (isClose)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            bool isN = k == Key.N || k == Key.N.WithShift ||
                       k.AsRune.Value == 'n' || k.AsRune.Value == 'N';
            if (isN)
            {
                k.Handled = true;
                await OpenCreatePlaylistDialogAsync();
                return;
            }

            bool isD = k == Key.D || k == Key.D.WithShift ||
                       k.AsRune.Value == 'd' || k.AsRune.Value == 'D';
            if (isD)
            {
                k.Handled = true;
                await HandleDeleteSelectedPlaylistAsync();
                return;
            }

            if (k == Key.CursorDown && _playlistListView.SelectedItem == _writablePlaylists.Count - 1)
            {
                confirmBtn.SetFocus();
                k.Handled = true;
            }
        };

        Add(confirmBtn, createBtn, deleteBtn, cancelBtn);

        RefreshPlaylistItems();

        _playlistListView.Accepting += (s, e) =>
        {
            e.Handled = true;
            ConfirmSelection();
        };
        _playlistListView.Accepted += (s, e) => ConfirmSelection();

        // 鼠标双击直接确认
        _playlistListView.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked))
            {
                ConfirmSelection();
                m.Handled = true;
            }
        };

        KeyDown += async (s, k) =>
        {
            if (k == Key.Enter)
            {
                var focused = Application.Navigation?.GetFocused();
                if (focused == createBtn)
                {
                    k.Handled = true;
                    await OpenCreatePlaylistDialogAsync();
                    return;
                }
                if (focused == deleteBtn)
                {
                    k.Handled = true;
                    await HandleDeleteSelectedPlaylistAsync();
                    return;
                }
                if (focused == cancelBtn)
                {
                    k.Handled = true;
                    Application.RequestStop(this);
                    return;
                }

                k.Handled = true;
                ConfirmSelection();
                return;
            }

            bool isClose = k == Key.Esc || k == Key.Q || k == Key.A || k == Key.A.WithShift ||
                           k.AsRune.Value == 'a' || k.AsRune.Value == 'A' ||
                           k.ToString().Equals("a", StringComparison.OrdinalIgnoreCase) ||
                           k.ToString().Equals("Key.A", StringComparison.OrdinalIgnoreCase);

            if (isClose)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            bool isN = k == Key.N || k == Key.N.WithShift ||
                       k.AsRune.Value == 'n' || k.AsRune.Value == 'N' ||
                       k.ToString().Equals("n", StringComparison.OrdinalIgnoreCase) ||
                       k.ToString().Equals("Key.N", StringComparison.OrdinalIgnoreCase);

            if (isN)
            {
                k.Handled = true;
                await OpenCreatePlaylistDialogAsync();
                return;
            }

            bool isD = k == Key.D || k == Key.D.WithShift ||
                       k.AsRune.Value == 'd' || k.AsRune.Value == 'D' ||
                       k.ToString().Equals("d", StringComparison.OrdinalIgnoreCase) ||
                       k.ToString().Equals("Key.D", StringComparison.OrdinalIgnoreCase);

            if (isD)
            {
                k.Handled = true;
                await HandleDeleteSelectedPlaylistAsync();
                return;
            }
        };

        _playlistListView.SetFocus();
        Application.Invoke(() => _playlistListView.SetFocus());
    }

    private void RefreshPlaylistItems()
    {
        var displayItems = new List<string>(_writablePlaylists.Count);
        for (int i = 0; i < _writablePlaylists.Count; i++)
        {
            var p = _writablePlaylists[i];
            displayItems.Add($"{(i + 1):D2}  [自建]  {p.Title}  (共 {p.SongNum} 首)");
        }

        if (displayItems.Count == 0)
        {
            displayItems.Add("暂无自建歌单 (请按 N 新建歌单)");
        }

        _playlistListView.SetSource(new ObservableCollection<string>(displayItems));
    }

    private async Task OpenCreatePlaylistDialogAsync()
    {
        var createDlg = new CreatePlaylistDialog(async (name) =>
        {
            var result = await MusicApi.CreatePlaylistAsync(name);
            Application.Invoke(() =>
            {
                if (result.Success)
                {
                    var newDissId = result.DissId;
                    var newPlaylist = new Playlist(newDissId, name, 0, newDissId, IsFav: false);
                    _writablePlaylists.Insert(0, newPlaylist);
                    RefreshPlaylistItems();
                    _playlistListView.SelectedItem = 0;
                    _currentSongLabel.Text = $"曲目: {_song.Title} - {_song.Artist}  (新建歌单「{name}」成功)";
                }
                else
                {
                    _currentSongLabel.Text = $"曲目: {_song.Title} - {_song.Artist}  (创建失败: {result.Message})";
                }
            });
        });

        Application.Run(createDlg);
        _playlistListView.SetFocus();
    }

    private async Task HandleDeleteSelectedPlaylistAsync()
    {
        var idx = _playlistListView.SelectedItem ?? -1;
        if (idx < 0 || idx >= _writablePlaylists.Count)
        {
            return;
        }

        var playlist = _writablePlaylists[idx];
        bool confirmed = false;
        var dlg = new Dialog
        {
            Title = "删除歌单确认",
            Width = 48,
            Height = 8,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = $"确定要删除歌单「{playlist.Title}」吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        dlg.Add(msg);

        var yesBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1)
        };
        yesBtn.SetScheme(new Scheme { Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None) });
        yesBtn.KeyBindings.Remove(Key.Space);
        yesBtn.Accepting += (s, e) =>
        {
            confirmed = true;
            Application.RequestStop(dlg);
        };
        dlg.Add(yesBtn);

        var cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 4,
            Y = Pos.AnchorEnd(1)
        };
        cancelBtn.SetScheme(new Scheme { Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None) });
        cancelBtn.KeyBindings.Remove(Key.Space);
        cancelBtn.Accepting += (s, e) =>
        {
            confirmed = false;
            Application.RequestStop(dlg);
        };
        dlg.Add(cancelBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        Application.Run(dlg);
        _playlistListView.SetFocus();

        if (confirmed)
        {
            _currentSongLabel.Text = $"正在删除歌单「{playlist.Title}」...";
            var ok = await MusicApi.DeletePlaylistAsync(playlist);
            Application.Invoke(() =>
            {
                if (ok)
                {
                    _writablePlaylists.RemoveAt(idx);
                    RefreshPlaylistItems();
                    if (_playlistListView.SelectedItem >= _writablePlaylists.Count)
                    {
                        _playlistListView.SelectedItem = Math.Max(0, _writablePlaylists.Count - 1);
                    }
                    _currentSongLabel.Text = $"曲目: {_song.Title} - {_song.Artist}  (已删除歌单「{playlist.Title}」)";
                }
                else
                {
                    _currentSongLabel.Text = $"曲目: {_song.Title} - {_song.Artist}  (删除歌单失败，请重试)";
                }
            });
        }
    }

    private void ConfirmSelection()
    {
        var idx = _playlistListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _writablePlaylists.Count)
        {
            Application.RequestStop(this);
            _onSelected.Invoke(_writablePlaylists[idx]);
        }
    }
}
