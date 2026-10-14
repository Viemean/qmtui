using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

/// <summary>
/// 一起听房间管理与交互对话框
/// </summary>
public sealed class ListenTogetherDialog : Dialog
{
    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
        Focus = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
        HotFocus = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
        Disabled = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Terminal.Gui.Drawing.Color.None),
        Active = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Editable = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, Terminal.Gui.Drawing.Color.None)
    };

    private readonly Func<Song?> _currentSongGetter;
    private readonly Func<IReadOnlyList<Song>> _playlistGetter;
    private readonly Func<long> _currentOffsetMsGetter;
    private readonly Action<string>? _statusCallback;

    private TextField? _joinInput;
    private Label? _statusNoticeLabel;

    public ListenTogetherDialog(
        Func<Song?> currentSongGetter,
        Func<IReadOnlyList<Song>> playlistGetter,
        Func<long> currentOffsetMsGetter,
        Action<string>? statusCallback = null)
    {
        _currentSongGetter = currentSongGetter;
        _playlistGetter = playlistGetter;
        _currentOffsetMsGetter = currentOffsetMsGetter;
        _statusCallback = statusCallback;

        Title = " 一起听 ";
        Width = 56;
        Height = 15;
        X = Pos.Center();
        Y = Pos.Center();

        SetScheme(TransparentDialogScheme);

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);
            if (c == 'K' && (_joinInput == null || !_joinInput.HasFocus))
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        };

        RenderContent();
    }

    private void RenderContent()
    {
        RemoveAll();

        var mgr = ListenTogetherManager.Instance;
        var room = mgr.CurrentRoom;

        if (room == null)
        {
            RenderNotInRoomView();
        }
        else
        {
            RenderInRoomView(room);
        }
    }

    private void RenderNotInRoomView()
    {
        var currentSong = _currentSongGetter();
        string songDesc = currentSong != null
            ? $"{currentSong.Title} - {currentSong.Artist}"
            : "未播放曲目";

        if (songDesc.Length > 44)
        {
            songDesc = string.Concat(songDesc.AsSpan(0, 41), "...");
        }

        var headerLabel = new Label
        {
            Text = "【创建一起听】",
            X = 2,
            Y = 0
        };
        headerLabel.SetScheme(TransparentDialogScheme);
        Add(headerLabel);

        var currentSongLabel = new Label
        {
            Text = $"当前曲目: {songDesc}",
            X = 2,
            Y = 1
        };
        currentSongLabel.SetScheme(TransparentDialogScheme);
        Add(currentSongLabel);

        var createBtn = new Button
        {
            Text = " 发起房间 ",
            X = 2,
            Y = 2
        };
        createBtn.SetScheme(TransparentDialogScheme);
        createBtn.Accepting += async (s, e) =>
        {
            if (currentSong == null)
            {
                SetNotice("请先播放一首在线歌曲再发起");
                return;
            }
            if (ListenTogetherManager.IsLocalOrWebDavSong(currentSong))
            {
                SetNotice("本地与私有云音频不支持一起听");
                return;
            }

            createBtn.Enabled = false;
            SetNotice("正在创建房间...");
            var playlist = _playlistGetter();
            long offset = _currentOffsetMsGetter();

            var newRoom = await ListenTogetherManager.Instance.StartRoomAsync(currentSong, playlist, offset).ConfigureAwait(false);
            Application.Invoke(() =>
            {
                if (newRoom != null)
                {
                    _statusCallback?.Invoke($"已创建一起听房间 {newRoom.ShowId}");
                    RenderContent();
                }
                else
                {
                    createBtn.Enabled = true;
                    SetNotice("创建房间失败，请检查登录态与网络");
                }
            });
        };
        Add(createBtn);

        var divider = new Label
        {
            Text = new string('-', 50),
            X = 2,
            Y = 4
        };
        divider.SetScheme(TransparentDialogScheme);
        Add(divider);

        var joinHeader = new Label
        {
            Text = "【加入好友房间】",
            X = 2,
            Y = 5
        };
        joinHeader.SetScheme(TransparentDialogScheme);
        Add(joinHeader);

        var joinPrompt = new Label
        {
            Text = "输入邀请链接或房间号 (LUGC...):",
            X = 2,
            Y = 6
        };
        joinPrompt.SetScheme(TransparentDialogScheme);
        Add(joinPrompt);

        _joinInput = new TextField
        {
            X = 2,
            Y = 7,
            Width = Dim.Fill(2),
            CanFocus = true
        };
        _joinInput.SetScheme(TransparentDialogScheme);
        _joinInput.EnableMiddleClickPaste();
        _joinInput.KeyDown += (s, k) =>
        {
            if (k.IsCtrl && (k.AsRune.Value == 'v' || k.AsRune.Value == 'V'))
            {
                k.Handled = true;
                _joinInput.PasteFromClipboard();
            }
        };
        Add(_joinInput);

        var joinBtn = new Button
        {
            Text = " 加入房间 ",
            X = 2,
            Y = 9
        };
        joinBtn.SetScheme(TransparentDialogScheme);

        async void ExecuteJoin()
        {
            var text = _joinInput.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                SetNotice("请输入链接或房间号");
                return;
            }

            joinBtn.Enabled = false;
            SetNotice("正在解析并连接房间...");

            bool ok = await ListenTogetherManager.Instance.JoinRoomAsync(text).ConfigureAwait(false);
            Application.Invoke(() =>
            {
                if (ok)
                {
                    _statusCallback?.Invoke("已成功加入一起听房间");
                    RenderContent();
                }
                else
                {
                    joinBtn.Enabled = true;
                    SetNotice("加入房间失败，请确认房间有效性");
                }
            });
        }

        joinBtn.Accepting += (s, e) => ExecuteJoin();
        _joinInput.Accepting += (s, e) => ExecuteJoin();
        Add(joinBtn);

        var closeBtn = new Button
        {
            Text = " 关闭 [K/Esc] ",
            X = Pos.AnchorEnd(18),
            Y = 9
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.Accepting += (s, e) => Application.RequestStop(this);
        Add(closeBtn);

        _statusNoticeLabel = new Label
        {
            Text = "",
            X = 2,
            Y = 11,
            Width = Dim.Fill(2)
        };
        _statusNoticeLabel.SetScheme(TransparentDialogScheme);
        Add(_statusNoticeLabel);
    }

    private void RenderInRoomView(ListenTogetherRoomInfo room)
    {
        var mgr = ListenTogetherManager.Instance;
        var stats = mgr.SpaceStats;

        var statusHeader = new Label
        {
            Text = $"【一起听进行中】 房间: {room.ShowId}",
            X = 2,
            Y = 0
        };
        statusHeader.SetScheme(TransparentDialogScheme);
        Add(statusHeader);

        string usersText = $"当前在线: {room.UsersCount} 人" + (mgr.IsWaitingForGuestAutoPlay ? " (等待好友加入自动起播)" : "");
        var usersLabel = new Label
        {
            Text = usersText,
            X = 2,
            Y = 2
        };
        usersLabel.SetScheme(TransparentDialogScheme);
        Add(usersLabel);

        var currentSong = _currentSongGetter();
        string songDesc = currentSong != null
            ? $"{currentSong.Title} - {currentSong.Artist}"
            : "同步中";
        if (songDesc.Length > 44) songDesc = string.Concat(songDesc.AsSpan(0, 41), "...");

        var songLabel = new Label
        {
            Text = $"当前播放: {songDesc}",
            X = 2,
            Y = 3
        };
        songLabel.SetScheme(TransparentDialogScheme);
        Add(songLabel);

        string statsDesc = stats != null
            ? $"累计共听: {stats.ListenMinutes} 分钟 | 已听: {stats.ListenSongsCount} 首"
            : "累计共听统计正在同步...";
        var statsLabel = new Label
        {
            Text = statsDesc,
            X = 2,
            Y = 5
        };
        statsLabel.SetScheme(TransparentDialogScheme);
        Add(statsLabel);

        var copyBtn = new Button
        {
            Text = " 复制分享链接 ",
            X = 2,
            Y = 7
        };
        copyBtn.SetScheme(TransparentDialogScheme);
        copyBtn.Accepting += (s, e) =>
        {
            string url = !string.IsNullOrEmpty(room.ShareUrl)
                ? room.ShareUrl
                : $"https://i.y.qq.com/n2/m/share/listen_together/index.html?showid={room.ShowId}&songid={room.CurrentSongId}";
            ClipboardService.SetText(url);
            SetNotice("分享链接已复制至系统剪贴板");
            _statusCallback?.Invoke("一起听链接已复制");
        };
        Add(copyBtn);

        var leaveBtn = new Button
        {
            Text = " 退出房间 ",
            X = 20,
            Y = 7
        };
        leaveBtn.SetScheme(TransparentDialogScheme);
        leaveBtn.Accepting += (s, e) =>
        {
            ListenTogetherManager.Instance.LeaveRoom();
            _statusCallback?.Invoke("已退出一起听");
            RenderContent();
        };
        Add(leaveBtn);

        var closeBtn = new Button
        {
            Text = " 关闭 [K/Esc] ",
            X = Pos.AnchorEnd(18),
            Y = 7
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.Accepting += (s, e) => Application.RequestStop(this);
        Add(closeBtn);

        _statusNoticeLabel = new Label
        {
            Text = "",
            X = 2,
            Y = 9,
            Width = Dim.Fill(2)
        };
        _statusNoticeLabel.SetScheme(TransparentDialogScheme);
        Add(_statusNoticeLabel);
    }

    private void SetNotice(string msg)
    {
        if (_statusNoticeLabel != null)
        {
            _statusNoticeLabel.Text = msg;
        }
    }
}
