using System.Diagnostics;
using Terminal.Gui.App;
using QmTui.Api;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 一起听会话与跨端播放状态同步管理器
/// </summary>
public sealed class ListenTogetherManager : IDisposable
{
    private static readonly Lazy<ListenTogetherManager> s_lazy = new(() => new ListenTogetherManager());
    public static ListenTogetherManager Instance => s_lazy.Value;

    private readonly Lock _lock = new();
    private readonly HashSet<string> _selfCreatedRoomIds = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    private long _lastLocalActionTick;
    private int _expectedPlayStatus = -1;
    private long _expectedSongId;
    private long _lastObservedSongId;
    private long _lastObservedMediaVersion;
    private IReadOnlyList<long> _lastSyncedPlaylistSongIds = [];

    private string _lastFetchedStatsRoomId = "";
    private long _lastStatsFetchTick;
    private long _accumulatedPlayMs;
    private long _lastRecordedStatsSongId;

    public ListenTogetherRoomInfo? CurrentRoom { get; private set; }
    public bool IsInRoom => CurrentRoom != null;
    public bool IsJoining { get; private set; }
    public bool IsWaitingForGuestAutoPlay { get; private set; }
    public ListenTogetherSpaceStats? SpaceStats { get; private set; }

    /// <summary>
    /// 标记当前正在应用由远端心跳/指令下发的播放状态，防止反向二次上报。
    /// </summary>
    public bool IsSyncingFromRemote { get; private set; }

    public event Action<ListenTogetherRoomInfo?>? RoomChanged;
    public event Action<ListenTogetherSpaceStats?>? SpaceStatsUpdated;
    public event Action<string>? StatusMessageRequested;

    // 依赖外部注入的本地播放器操作代理
    public Func<Song, double, Task>? PlaySongHandler { get; set; }
    public Func<Task>? PlayHandler { get; set; }
    public Func<Task>? PauseHandler { get; set; }
    public Func<double, Task>? SeekHandler { get; set; }
    public Func<bool>? IsPlayingGetter { get; set; }
    public Func<double>? CurrentPositionSecondsGetter { get; set; }
    public Func<Song?>? CurrentSongGetter { get; set; }

    private ListenTogetherManager() { }

    /// <summary>
    /// 判断指定歌曲是否为本地或 WebDAV 私有源。
    /// </summary>
    /// <param name="song">待检测的歌曲对象。</param>
    /// <returns>若为本地文件或私有云流则返回 true。</returns>
    public static bool IsLocalOrWebDavSong(Song? song)
    {
        if (song == null) return false;
        return !string.IsNullOrEmpty(song.LocalFilePath) || !string.IsNullOrEmpty(song.WebDavHref);
    }

    /// <summary>
    /// 校验指定房间号或加密 UIN 是否属于自身创建。
    /// </summary>
    /// <param name="showId">待检测房间号。</param>
    /// <param name="encUin">待检测加密 UIN。</param>
    /// <returns>若为自身创建返回 true。</returns>
    public bool IsSelfCreatedRoom(string showId, string encUin = "")
    {
        lock (_lock)
        {
            var trimmedShowId = showId.Trim();
            if (!string.IsNullOrEmpty(trimmedShowId))
            {
                if (CurrentRoom != null && string.Equals(CurrentRoom.ShowId, trimmedShowId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (_selfCreatedRoomIds.Contains(trimmedShowId))
                {
                    return true;
                }
            }

            var myEncUin = UserSession.Current.EncryptedUin.Trim();
            var trimmedEncUin = encUin.Trim();
            if (!string.IsNullOrEmpty(trimmedEncUin) && !string.IsNullOrEmpty(myEncUin) &&
                string.Equals(trimmedEncUin, myEncUin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
    }

    internal void SetRoomForTesting(ListenTogetherRoomInfo? room)
    {
        lock (_lock)
        {
            CurrentRoom = room;
        }
    }

    /// <summary>
    /// 发起创建一起听房间并启动心跳同步。
    /// </summary>
    /// <param name="song">当前曲目。</param>
    /// <param name="playlist">当前播放列表。</param>
    /// <param name="offsetMs">当前播放进度偏移量（毫秒）。</param>
    /// <returns>创建成功的房间元数据，失败返回 null。</returns>
    public async Task<ListenTogetherRoomInfo?> StartRoomAsync(Song song, IReadOnlyList<Song>? playlist, long offsetMs)
    {
        if (IsLocalOrWebDavSong(song))
        {
            StatusMessageRequested?.Invoke("本地与私有云音频不支持发起一起听");
            return null;
        }

        try
        {
            if (PauseHandler != null)
            {
                await PauseHandler().ConfigureAwait(false);
            }

            IsWaitingForGuestAutoPlay = true;
            _lastLocalActionTick = Environment.TickCount64;
            _expectedPlayStatus = 0;
            _expectedSongId = song.Id;

            var room = await MusicApi.CreateListenTogetherRoomAsync(song, playlist, offsetMs, playStatus: 2).ConfigureAwait(false);
            if (room != null)
            {
                lock (_lock)
                {
                    _selfCreatedRoomIds.Add(room.ShowId);
                    CurrentRoom = room;
                }

                _lastObservedSongId = song.Id;
                _lastSyncedPlaylistSongIds = playlist?.Select(s => s.Id).ToList() ?? [song.Id];

                int targetIndex = 0;
                if (playlist != null && playlist.Count > 0)
                {
                    for (int i = 0; i < playlist.Count; i++)
                    {
                        if (playlist[i].Id == song.Id)
                        {
                            targetIndex = i;
                            break;
                        }
                    }
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (offsetMs > 0)
                        {
                            await MusicApi.SendListenTogetherSongOperAsync(
                                room.ShowId, cmd: 7, songId: song.Id, songTitle: song.Title,
                                songIndex: targetIndex, offsetMs: offsetMs).ConfigureAwait(false);
                        }
                        await MusicApi.SendListenTogetherSongOperAsync(
                            room.ShowId, cmd: 3, songId: song.Id, songTitle: song.Title,
                            songIndex: targetIndex, offsetMs: offsetMs).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("ListenTogetherManager", $"初始化暂停上报失败: {ex.Message}");
                    }
                });

                StartHeartbeatSync(room.ShowId);
                RoomChanged?.Invoke(room);
                return room;
            }

            IsWaitingForGuestAutoPlay = false;
            _expectedPlayStatus = -1;
            StatusMessageRequested?.Invoke("创建一起听房间失败");
            return null;
        }
        catch (Exception ex)
        {
            IsWaitingForGuestAutoPlay = false;
            _expectedPlayStatus = -1;
            AppLogger.Warn("ListenTogetherManager", $"创建房间异常: {ex.Message}");
            StatusMessageRequested?.Invoke($"创建失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析输入链接或房间号并加入一起听房间。
    /// </summary>
    /// <param name="shareText">邀请链接、文本或房间号。</param>
    /// <returns>加入是否成功。</returns>
    public async Task<bool> JoinRoomAsync(string shareText)
    {
        if (string.IsNullOrWhiteSpace(shareText))
        {
            StatusMessageRequested?.Invoke("请输入有效的一起听链接或房间号");
            return false;
        }

        IsJoining = true;
        try
        {
            var (showId, encUin, songId) = await MusicApi.ResolveListenTogetherShareTargetAsync(shareText).ConfigureAwait(false);
            if (string.IsNullOrEmpty(showId))
            {
                StatusMessageRequested?.Invoke("未能识别有效的一起听房间号");
                return false;
            }

            if (IsSelfCreatedRoom(showId, encUin))
            {
                StatusMessageRequested?.Invoke("无法加入自己创建的一起听房间");
                return false;
            }

            bool success = await MusicApi.JoinListenTogetherRoomAsync(showId, encUin).ConfigureAwait(false);
            if (!success)
            {
                StatusMessageRequested?.Invoke("加入房间失败，房间可能已解散或关闭");
                return false;
            }

            var room = new ListenTogetherRoomInfo
            {
                ShowId = showId,
                CreatorUin = encUin,
                ShareUrl = $"https://i.y.qq.com/n2/m/share/listen_together/index.html?showid={showId}&songid={songId}",
                CurrentSongId = songId,
                UsersCount = 2,
                IsValid = true
            };

            lock (_lock)
            {
                CurrentRoom = room;
            }

            _lastObservedSongId = songId;

            if (!string.IsNullOrEmpty(encUin))
            {
                FetchSpaceStats(showId, encUin, force: true);
            }

            // 进房后拉取房主完整播放队列并同步起播
            _ = Task.Run(async () =>
            {
                try
                {
                    var state = await MusicApi.HeartbeatListenTogetherRoomAsync(showId).ConfigureAwait(false);
                    long initialOffsetMs = state?.SongOffsetMs ?? 0;
                    bool isHostPlaying = state?.PlayStatus == 1;

                    var songIds = await MusicApi.GetListenTogetherSongListAsync(showId).ConfigureAwait(false);
                    if (songIds.Count > 0)
                    {
                        var fullSongs = await MusicApi.GetSongsBySongIdsAsync(songIds).ConfigureAwait(false);
                        if (fullSongs.Count > 0)
                        {
                            int targetIdx = 0;
                            for (int i = 0; i < fullSongs.Count; i++)
                            {
                                if (fullSongs[i].Id == songId)
                                {
                                    targetIdx = i;
                                    break;
                                }
                            }

                            Application.Invoke(async () =>
                            {
                                IsSyncingFromRemote = true;
                                try
                                {
                                    PlaybackQueueService.Instance.SetQueue(fullSongs, targetIdx);
                                    if (PlaySongHandler != null)
                                    {
                                        await PlaySongHandler(fullSongs[targetIdx], initialOffsetMs / 1000.0).ConfigureAwait(false);
                                    }
                                    if (!isHostPlaying && PauseHandler != null)
                                    {
                                        await PauseHandler().ConfigureAwait(false);
                                    }
                                    _lastSyncedPlaylistSongIds = fullSongs.Select(s => s.Id).ToList();
                                    _lastObservedMediaVersion = state?.MediaVersion ?? 0;
                                }
                                finally
                                {
                                    IsSyncingFromRemote = false;
                                }
                            });
                            return;
                        }
                    }

                    // 回退策略：拉取单曲起播
                    if (songId > 0)
                    {
                        var songs = await MusicApi.GetSongsBySongIdsAsync([songId]).ConfigureAwait(false);
                        var single = songs.Count > 0 ? songs[0] : null;
                        if (single != null)
                        {
                            Application.Invoke(async () =>
                            {
                                IsSyncingFromRemote = true;
                                try
                                {
                                    if (PlaySongHandler != null)
                                    {
                                        await PlaySongHandler(single, initialOffsetMs / 1000.0).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    IsSyncingFromRemote = false;
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ListenTogetherManager", $"进房拉取队列异常: {ex.Message}");
                }
            });

            StartHeartbeatSync(showId);
            RoomChanged?.Invoke(room);
            StatusMessageRequested?.Invoke($"成功加入房间 {showId}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ListenTogetherManager", $"加入房间失败: {ex.Message}");
            StatusMessageRequested?.Invoke($"加入失败: {ex.Message}");
            return false;
        }
        finally
        {
            IsJoining = false;
        }
    }

    /// <summary>
    /// 主动退出当前一起听房间。
    /// </summary>
    public void LeaveRoom()
    {
        ListenTogetherRoomInfo? room;
        lock (_lock)
        {
            room = CurrentRoom;
            CurrentRoom = null;
        }
        if (room == null) return;

        IsWaitingForGuestAutoPlay = false;
        StopHeartbeatSync();
        ResetSpaceStats();

        _lastObservedSongId = 0;
        _lastLocalActionTick = 0;
        _expectedPlayStatus = -1;
        _expectedSongId = 0;
        _lastSyncedPlaylistSongIds = [];
        _lastObservedMediaVersion = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                await MusicApi.QuitListenTogetherRoomAsync(room.ShowId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ListenTogetherManager", $"退出房间请求异常: {ex.Message}");
            }
        });

        RoomChanged?.Invoke(null);
        StatusMessageRequested?.Invoke("已退出一起听");
    }

    /// <summary>
    /// 将本地播放控制行为上报至一起听服务端。
    /// </summary>
    /// <param name="cmd">指令码（1 队列重置, 2 播放, 3 暂停, 4 切歌, 7 跳转）。</param>
    /// <param name="songId">目标曲目 ID。</param>
    /// <param name="songTitle">目标曲目标题。</param>
    /// <param name="songIndex">目标索引。</param>
    /// <param name="offsetMs">目标偏移时间（毫秒）。</param>
    /// <param name="playlist">目标队列（仅 cmd=1）。</param>
    public void HandleLocalAction(
        int cmd,
        long songId = 0,
        string songTitle = "",
        int songIndex = -1,
        long offsetMs = -1,
        IReadOnlyList<Song>? playlist = null)
    {
        var room = CurrentRoom;
        if (room == null || IsSyncingFromRemote) return;

        if (cmd == 2)
        {
            IsWaitingForGuestAutoPlay = false;
        }

        var targetSong = CurrentSongGetter?.Invoke();
        long targetSongId = songId > 0 ? songId : (targetSong?.Id ?? 0);
        string targetTitle = !string.IsNullOrEmpty(songTitle) ? songTitle : (targetSong?.Title ?? "");
        long targetOffset = offsetMs >= 0 ? offsetMs : (long)((CurrentPositionSecondsGetter?.Invoke() ?? 0) * 1000);
        int targetIndex = songIndex >= 0 ? songIndex : PlaybackQueueService.Instance.CurrentIndex;
        if (targetIndex < 0) targetIndex = 0;

        _lastLocalActionTick = Environment.TickCount64;
        switch (cmd)
        {
            case 2:
                _expectedPlayStatus = 1;
                break;
            case 3:
                _expectedPlayStatus = 0;
                break;
            case 4:
                _expectedSongId = targetSongId;
                _expectedPlayStatus = 1;
                break;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (cmd == 2 && targetOffset > 0)
                {
                    await MusicApi.SendListenTogetherSongOperAsync(
                        room.ShowId, cmd: 7, songId: targetSongId, songTitle: targetTitle,
                        songIndex: targetIndex, offsetMs: targetOffset).ConfigureAwait(false);
                }
                await MusicApi.SendListenTogetherSongOperAsync(
                    room.ShowId, cmd: cmd, songId: targetSongId, songTitle: targetTitle,
                    songIndex: targetIndex, offsetMs: targetOffset, playlist: playlist).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ListenTogetherManager", $"上报控制指令失败 (cmd={cmd}): {ex.Message}");
            }
        });
    }

    private void StartHeartbeatSync(string showId)
    {
        StopHeartbeatSync();
        _heartbeatCts = new CancellationTokenSource();
        var ct = _heartbeatCts.Token;

        _heartbeatTask = Task.Run(async () =>
        {
            int consecutiveFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var state = await MusicApi.HeartbeatListenTogetherRoomAsync(showId, ct).ConfigureAwait(false);
                    if (state == null)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= 5)
                        {
                            AppLogger.Warn("ListenTogetherManager", "心跳连续失败达到上限，退出房间");
                            Application.Invoke(LeaveRoom);
                            break;
                        }
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                        continue;
                    }
                    consecutiveFailures = 0;

                    // 房间已由远端解散或关闭
                    if (!state.IsRoomValid)
                    {
                        AppLogger.Info("ListenTogetherManager", "房间已失效或对方已解散");
                        Application.Invoke(() =>
                        {
                            LeaveRoom();
                            StatusMessageRequested?.Invoke("一起听房间已解散");
                        });
                        break;
                    }

                    // 成员数更新与房客进入自动起播
                    var current = CurrentRoom;
                    if (current != null)
                    {
                        int oldUsers = current.UsersCount;
                        if (oldUsers != state.UsersCount)
                        {
                            current.UsersCount = state.UsersCount;
                            if (state.UsersCount < 2)
                            {
                                ResetSpaceStats();
                                SpaceStatsUpdated?.Invoke(null);
                            }
                            RoomChanged?.Invoke(current);
                        }

                        if (IsWaitingForGuestAutoPlay && oldUsers < 2 && state.UsersCount >= 2)
                        {
                            IsWaitingForGuestAutoPlay = false;
                            _lastLocalActionTick = Environment.TickCount64;
                            _expectedPlayStatus = 1;
                            Application.Invoke(async () =>
                            {
                                if (PlayHandler != null)
                                {
                                    await PlayHandler().ConfigureAwait(false);
                                }
                            });
                        }
                    }

                    // 好友空间数据定期同步（两分钟周期）
                    if (state.UsersCount >= 2)
                    {
                        var myEnc = UserSession.Current.EncryptedUin;
                        var partnerEnc = state.UserEncUins.FirstOrDefault(u => !string.Equals(u, myEnc, StringComparison.OrdinalIgnoreCase))
                            ?? current?.CreatorUin
                            ?? (state.UserEncUins.Count > 0 ? state.UserEncUins[0] : "")
                            ?? "";
                        if (!string.IsNullOrEmpty(partnerEnc))
                        {
                            long nowTick = Environment.TickCount64;
                            if (SpaceStats == null)
                            {
                                FetchSpaceStats(state.ShowId, partnerEnc, force: true);
                                _lastRecordedStatsSongId = state.CurrentSongId;
                            }
                            else if (nowTick - _lastStatsFetchTick >= 120_000)
                            {
                                FetchSpaceStats(state.ShowId, partnerEnc, force: true);
                            }
                        }
                    }

                    // 在播期间本地乐观累加听歌时长（每 60 秒累加 1 分钟）
                    if (state.UsersCount >= 2 && state.PlayStatus == 1 && SpaceStats != null)
                    {
                        _accumulatedPlayMs += 1500;
                        if (_accumulatedPlayMs >= 60_000)
                        {
                            _accumulatedPlayMs -= 60_000;
                            SpaceStats.ListenMinutes++;
                            SpaceStatsUpdated?.Invoke(SpaceStats);
                        }
                    }

                    long currentTick = Environment.TickCount64;
                    bool isWithinActionWindow = (currentTick - _lastLocalActionTick) < 3000;

                    // 切歌与歌单版本对齐
                    if (state.CurrentSongId > 0)
                    {
                        bool isMediaVersionChanged = state.MediaVersion > 0 && _lastObservedMediaVersion > 0 && state.MediaVersion != _lastObservedMediaVersion;
                        bool isSongChanged = state.CurrentSongId != _lastObservedSongId;

                        if (isWithinActionWindow && _expectedSongId > 0 && state.CurrentSongId != _expectedSongId)
                        {
                            // 本地操作保护窗口内，忽略服务端尚未生效的旧歌曲状态
                        }
                        else
                        {
                            if (state.CurrentSongId == _expectedSongId)
                            {
                                _expectedSongId = 0;
                            }

                            if (isSongChanged || isMediaVersionChanged)
                            {
                                if (isSongChanged && state.UsersCount >= 2 && SpaceStats != null && _lastRecordedStatsSongId > 0 && state.CurrentSongId != _lastRecordedStatsSongId)
                                {
                                    SpaceStats.ListenSongsCount++;
                                    SpaceStatsUpdated?.Invoke(SpaceStats);
                                }
                                _lastRecordedStatsSongId = state.CurrentSongId;
                                _lastObservedSongId = state.CurrentSongId;
                                if (state.MediaVersion > 0) _lastObservedMediaVersion = state.MediaVersion;

                                var currentList = PlaybackQueueService.Instance.ActiveSongs;
                                var existingSong = currentList.FirstOrDefault(s => s.Id == state.CurrentSongId);

                                if (existingSong != null && !isMediaVersionChanged)
                                {
                                    var playingSong = CurrentSongGetter?.Invoke();
                                    if (playingSong?.Id != state.CurrentSongId)
                                    {
                                        Application.Invoke(async () =>
                                        {
                                            IsSyncingFromRemote = true;
                                            try
                                            {
                                                if (PlaySongHandler != null)
                                                {
                                                    await PlaySongHandler(existingSong, state.SongOffsetMs / 1000.0).ConfigureAwait(false);
                                                }
                                            }
                                            finally
                                            {
                                                IsSyncingFromRemote = false;
                                            }
                                        });
                                    }
                                }
                                else
                                {
                                    // 歌曲不在本地当前播放队列中，或者服务端歌单版本变更：拉取完整队列
                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var songIds = await MusicApi.GetListenTogetherSongListAsync(showId, ct).ConfigureAwait(false);
                                            if (songIds.Count > 0)
                                            {
                                                var fullSongs = await MusicApi.GetSongsBySongIdsAsync(songIds, ct).ConfigureAwait(false);
                                                if (fullSongs.Count > 0)
                                                {
                                                    int targetIdx = 0;
                                                    for (int i = 0; i < fullSongs.Count; i++)
                                                    {
                                                        if (fullSongs[i].Id == state.CurrentSongId)
                                                        {
                                                            targetIdx = i;
                                                            break;
                                                        }
                                                    }

                                                    Application.Invoke(async () =>
                                                    {
                                                        IsSyncingFromRemote = true;
                                                        try
                                                        {
                                                            PlaybackQueueService.Instance.SetQueue(fullSongs, targetIdx);
                                                            if (PlaySongHandler != null)
                                                            {
                                                                await PlaySongHandler(fullSongs[targetIdx], state.SongOffsetMs / 1000.0).ConfigureAwait(false);
                                                            }
                                                            if (state.PlayStatus != 1 && PauseHandler != null)
                                                            {
                                                                await PauseHandler().ConfigureAwait(false);
                                                            }
                                                            _lastSyncedPlaylistSongIds = fullSongs.Select(s => s.Id).ToList();
                                                        }
                                                        finally
                                                        {
                                                            IsSyncingFromRemote = false;
                                                        }
                                                    });
                                                    return;
                                                }
                                            }

                                            // 回退策略：拉取目标曲目直接起播
                                            var singleList = await MusicApi.GetSongsBySongIdsAsync([state.CurrentSongId], ct).ConfigureAwait(false);
                                            var targetSong = singleList.Count > 0 ? singleList[0] : null;
                                            if (targetSong != null)
                                            {
                                                Application.Invoke(async () =>
                                                {
                                                    IsSyncingFromRemote = true;
                                                    try
                                                    {
                                                        if (PlaySongHandler != null)
                                                        {
                                                            await PlaySongHandler(targetSong, state.SongOffsetMs / 1000.0).ConfigureAwait(false);
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        IsSyncingFromRemote = false;
                                                    }
                                                });
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            AppLogger.Warn("ListenTogetherManager", $"同步全量歌单异常: {ex.Message}");
                                        }
                                    }, ct);
                                }
                            }
                        }
                    }

                    // 对齐播放与暂停状态
                    if (IsWaitingForGuestAutoPlay)
                    {
                        if (IsPlayingGetter?.Invoke() == true)
                        {
                            Application.Invoke(async () =>
                            {
                                IsSyncingFromRemote = true;
                                try
                                {
                                    if (PauseHandler != null) await PauseHandler().ConfigureAwait(false);
                                }
                                finally
                                {
                                    IsSyncingFromRemote = false;
                                }
                            });
                        }
                    }
                    else if (isWithinActionWindow && _expectedPlayStatus != -1)
                    {
                        bool matches = (_expectedPlayStatus == 1 && state.PlayStatus == 1) ||
                            ((_expectedPlayStatus == 0 || _expectedPlayStatus == 2) && (state.PlayStatus == 0 || state.PlayStatus == 2));
                        if (matches)
                        {
                            _expectedPlayStatus = -1;
                        }
                    }
                    else
                    {
                        bool currentlyPlaying = IsPlayingGetter?.Invoke() == true;
                        if (state.PlayStatus == 1 && !currentlyPlaying)
                        {
                            Application.Invoke(async () =>
                            {
                                IsSyncingFromRemote = true;
                                try
                                {
                                    if (PlayHandler != null) await PlayHandler().ConfigureAwait(false);
                                }
                                finally
                                {
                                    IsSyncingFromRemote = false;
                                }
                            });
                        }
                        else if ((state.PlayStatus == 2 || state.PlayStatus == 0) && currentlyPlaying)
                        {
                            Application.Invoke(async () =>
                            {
                                IsSyncingFromRemote = true;
                                try
                                {
                                    if (PauseHandler != null) await PauseHandler().ConfigureAwait(false);
                                }
                                finally
                                {
                                    IsSyncingFromRemote = false;
                                }
                            });
                        }
                    }

                    // 播放进度偏移容限校准（阈值 3000ms）
                    if (state.UsersCount >= 2 && !IsWaitingForGuestAutoPlay && !isWithinActionWindow)
                    {
                        var localSong = CurrentSongGetter?.Invoke();
                        if (localSong != null && localSong.Id == state.CurrentSongId && state.PlayStatus == 1 && state.SongOffsetMs > 0)
                        {
                            double localSec = CurrentPositionSecondsGetter?.Invoke() ?? 0;
                            double remoteSec = state.SongOffsetMs / 1000.0;
                            if (Math.Abs(localSec - remoteSec) > 3.0)
                            {
                                Application.Invoke(async () =>
                                {
                                    IsSyncingFromRemote = true;
                                    try
                                    {
                                        if (SeekHandler != null) await SeekHandler(remoteSec).ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        IsSyncingFromRemote = false;
                                    }
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("ListenTogetherManager", $"心跳周期异常: {ex.Message}");
                }

                try
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, ct);
    }

    private void StopHeartbeatSync()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
        _heartbeatTask = null;
    }

    private void FetchSpaceStats(string showId, string partnerEncUin, bool force = false)
    {
        if (string.IsNullOrEmpty(showId) || string.IsNullOrEmpty(partnerEncUin)) return;
        var room = CurrentRoom;
        if (room == null || room.UsersCount < 2) return;

        long now = Environment.TickCount64;
        if (!force && string.Equals(_lastFetchedStatsRoomId, showId, StringComparison.OrdinalIgnoreCase) &&
            SpaceStats != null && (now - _lastStatsFetchTick) < 120_000)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var myEnc = UserSession.Current.EncryptedUin;
                var stats = await MusicApi.GetListenTogetherSpaceStatsAsync(showId, partnerEncUin, myEnc).ConfigureAwait(false);
                if (stats != null)
                {
                    _lastFetchedStatsRoomId = showId;
                    _lastStatsFetchTick = Environment.TickCount64;
                    SpaceStats = stats;
                    SpaceStatsUpdated?.Invoke(stats);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ListenTogetherManager", $"获取好友空间统计异常: {ex.Message}");
            }
        });
    }

    private void ResetSpaceStats()
    {
        SpaceStats = null;
        _lastFetchedStatsRoomId = "";
        _lastStatsFetchTick = 0;
        _accumulatedPlayMs = 0;
        _lastRecordedStatsSongId = 0;
        SpaceStatsUpdated?.Invoke(null);
    }

    public void Dispose()
    {
        StopHeartbeatSync();
    }
}
