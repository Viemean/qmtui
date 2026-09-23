using System.Text;
using Terminal.Gui.App;
using QmTui.Connect.Discovery;
using QmTui.Connect.Models;
using QmTui.Connect.Server;
using QmTui.Connect.Storage;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    private ConnectStorage? _connectStorage;
    private TvConnectServer? _connectServer;
    private ConnectMdnsService? _connectMdns;
    private string? _lastRemoteSyncedSongMid;

    private void SetupConnectService()
    {
        try
        {
            _connectStorage = new ConnectStorage();
            _connectServer = new TvConnectServer(_connectStorage, port: 8765);

            _connectServer.DeviceConnected += dev => Application.Invoke(() =>
            {
                BroadcastConnectPlayerState();
                BroadcastConnectQueueState();
                BroadcastConnectLyrics();
            });

            _connectServer.PlayerStateRequested += () => Application.Invoke(BroadcastConnectPlayerState);
            _connectServer.QueueStateRequested += () => Application.Invoke(BroadcastConnectQueueState);

            PlaybackQueueService.Instance.QueueChanged += () => Application.Invoke(BroadcastConnectQueueState);

            _connectServer.PlaySongRequested += cmd =>
            {
                Application.Invoke(async () =>
                {
                    try
                    {
                        var song = cmd.Song.ToDomainSong();
                        if (cmd.Queue != null && cmd.Queue.Count > 0)
                        {
                            var domainQueue = cmd.Queue.Select(q => q.ToDomainSong()).ToList();
                            var validIdx = Math.Clamp(cmd.Index, 0, Math.Max(0, domainQueue.Count - 1));
                            PlaybackQueueService.Instance.SetQueue(domainQueue, validIdx);
                        }

                        if (!string.IsNullOrEmpty(cmd.QualityTier))
                        {
                            _preferredQualityTier = AudioQualityHelper.Parse(cmd.QualityTier);
                        }

                        // 对齐作者移动端流媒体中转与直连策略：
                        // 1. AudioSource STREAM_PROXY
                        // 2. Song.MediaMid 为 http/https 流
                        // 3. Song.LocalFilePath 为 http/https 流或本地有效文件
                        string? overrideUrl = null;
                        if (cmd.AudioSource?.SourceType == AudioSourceType.STREAM_PROXY && !string.IsNullOrEmpty(cmd.AudioSource.StreamUrl))
                        {
                            overrideUrl = cmd.AudioSource.StreamUrl;
                        }
                        else if (!string.IsNullOrEmpty(cmd.Song.MediaMid) &&
                                (cmd.Song.MediaMid.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cmd.Song.MediaMid.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        {
                            overrideUrl = cmd.Song.MediaMid;
                        }
                        else if (!string.IsNullOrEmpty(cmd.Song.LocalFilePath))
                        {
                            if (cmd.Song.LocalFilePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                cmd.Song.LocalFilePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                File.Exists(cmd.Song.LocalFilePath))
                            {
                                overrideUrl = cmd.Song.LocalFilePath;
                            }
                        }

                        // 本地流直通回环绕过 (Loopback Bypass)：
                        // 若 overrideUrl 指向本机的 /stream/local?path=...，直接解析为本地物理文件播放，消除本机 HTTP 代理开销
                        if (!string.IsNullOrEmpty(overrideUrl) && overrideUrl.Contains("/stream/local"))
                        {
                            var qIdx = overrideUrl.IndexOf("path=", StringComparison.OrdinalIgnoreCase);
                            if (qIdx >= 0)
                            {
                                try
                                {
                                    var pathPart = overrideUrl[(qIdx + 5)..].Split('&')[0];
                                    var localPath = Uri.UnescapeDataString(pathPart);
                                    if (File.Exists(localPath))
                                    {
                                        overrideUrl = localPath;
                                        song.LocalFilePath = localPath;
                                    }
                                    else if (overrideUrl.Contains($":{_connectServer.ActualPort}/stream/local"))
                                    {
                                        AppLogger.Warn("MainWindow.Connect", $"Detected loopback stream for missing PC file: {localPath}, clearing overrideUrl to request mobile stream proxy");
                                        overrideUrl = null;
                                        song = song with { MediaMid = song.Mid };
                                    }
                                }
                                catch { }
                            }
                        }

                        await PlaySongAsync(song, startPosition: cmd.StartPositionMs / 1000.0, overridePlayUrl: overrideUrl).ConfigureAwait(false);
                        BroadcastConnectPlayerState();
                        BroadcastConnectQueueState();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("MainWindow.Connect", "PlaySongRequested error", ex);
                    }
                });
            };

            _connectServer.EnqueueNextRequested += cmd =>
            {
                Application.Invoke(() =>
                {
                    var song = cmd.Song.ToDomainSong();
                    PlaybackQueueService.Instance.InsertNext(song);
                    BroadcastConnectQueueState();
                });
            };

            _connectServer.PauseRequested += () => Application.Invoke(async () =>
            {
                var isPlaying = _isTuiAudioDisabled ? _isWebPlaying : _player.IsPlaying;
                if (isPlaying)
                {
                    if (_isTuiAudioDisabled)
                    {
                        _isWebPlaying = false;
                        StopWebVirtualTicker();
                    }
                    else
                    {
                        await _player.TogglePauseAsync().ConfigureAwait(false);
                    }
                    UpdatePlayerStatus();
                    BroadcastConnectPlayerState();
                }
            });

            _connectServer.ResumeRequested += () => Application.Invoke(async () =>
            {
                var isPlaying = _isTuiAudioDisabled ? _isWebPlaying : _player.IsPlaying;
                if (!isPlaying)
                {
                    if (_player.TotalDurationSeconds > 0)
                    {
                        if (_isTuiAudioDisabled)
                        {
                            _isWebPlaying = true;
                            if (_activeSong != null) StartWebVirtualTicker(_activeSong.Duration);
                        }
                        else
                        {
                            await _player.TogglePauseAsync().ConfigureAwait(false);
                        }
                        UpdatePlayerStatus();
                        BroadcastConnectPlayerState();
                    }
                    else
                    {
                        var queueSongs = PlaybackQueueService.Instance.ActiveSongs;
                        var targetSong = _activeSong
                            ?? PlaybackQueueService.Instance.CurrentSong
                            ?? _songListView.GetSelectedSong()
                            ?? (queueSongs.Count > 0 ? queueSongs[0] : null);

                        if (targetSong != null)
                        {
                            await PlaySongAsync(targetSong, UserSession.Current.LastPlaybackPositionSeconds).ConfigureAwait(false);
                            UpdatePlayerStatus();
                            BroadcastConnectPlayerState();
                        }
                        else
                        {
                            AppLogger.Info("MainWindow.Connect", "Ignoring cmd_resume because no song is available to play");
                        }
                    }
                }
            });
            _connectServer.PreviousRequested += () => Application.Invoke(async () =>
            {
                await PlayPrevInCurrentListAsync().ConfigureAwait(false);
                BroadcastConnectPlayerState();
            });
            _connectServer.NextRequested += () => Application.Invoke(async () =>
            {
                if (RadioService.Instance.HasActiveRadio)
                {
                    await PlayNextRadioTrackAsync().ConfigureAwait(false);
                }
                else
                {
                    await PlayNextInCurrentListAsync().ConfigureAwait(false);
                }
                BroadcastConnectPlayerState();
            });
            _connectServer.SeekRequested += ms => Application.Invoke(async () =>
            {
                await _player.SeekAsync(ms / 1000.0).ConfigureAwait(false);
                BroadcastConnectPlayerState();
            });
            _connectServer.SetVolumeRequested += vol => Application.Invoke(() => AdjustVolumeDirect((int)Math.Round(vol * 100)));
            _connectServer.SwitchTierRequested += tierStr =>
            {
                Application.Invoke(async () =>
                {
                    var tier = AudioQualityHelper.Parse(tierStr);
                    await SwitchQualityTierAsync(tier).ConfigureAwait(false);
                    BroadcastConnectPlayerState();
                });
            };
            _connectServer.CycleLoopModeRequested += payload => Application.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var mode = PlaybackModeHelper.FromConnectLoopMode(payload);
                    SetPlaybackMode(mode);
                    _controlBar.UpdateStatus($"[播放模式: {mode.GetName()}]");
                    return;
                }
                TogglePlaybackMode();
            });
            _connectServer.ToggleFavoriteRequested += cmd =>
            {
                Application.Invoke(() =>
                {
                    var target = _activeSong;
                    if (cmd.Song != null && !string.IsNullOrEmpty(cmd.Song.SongMid))
                    {
                        if (_activeSong?.Mid == cmd.Song.SongMid) target = _activeSong;
                        else target = cmd.Song.ToDomainSong();
                    }
                    else if (!string.IsNullOrEmpty(cmd.SongMid))
                    {
                        if (_activeSong?.Mid == cmd.SongMid) target = _activeSong;
                    }

                    if (target != null)
                    {
                        var targetFavorite = cmd.IsFavorite ?? !IsSongFavorite(target);
                        if (IsSongFavorite(target) == targetFavorite)
                        {
                            return;
                        }
                        ApplySongFavoriteState(target, targetFavorite);
                        _controlBar.UpdateStatus(targetFavorite
                            ? $"[喜欢状态已同步] 已添加《{target.Title}》"
                            : $"[喜欢状态已同步] 已移除《{target.Title}》");
                    }
                });
            };
            _connectServer.TriggerAodRequested += () => Application.Invoke(ToggleAodMode);
            _connectServer.OpenPlayerRequested += () => Application.Invoke(() =>
            {
                if (!_isNowPlayingViewActive)
                {
                    ToggleNowPlayingView();
                }
            });
            _connectServer.SyncLyricsScrollRequested += payload =>
            {
                Application.Invoke(() =>
                {
                    ScrollLyricToLine(payload.LineIndex);
                });
            };
            _connectServer.SyncLyricsRequested += payload =>
            {
                Application.Invoke(() =>
                {
                    if (payload?.Lyrics != null && payload.Lyrics.Count > 0)
                    {
                        _lastRemoteSyncedSongMid = payload.SongMid;
                        var domainLyrics = payload.Lyrics
                            .Select(l => new LyricLine(TimeSpan.FromMilliseconds(l.TimestampMs), l.Text, l.TransText))
                            .ToList();
                        _currentLyrics.Clear();
                        _currentLyrics.AddRange(domainLyrics);
                        _player.UpdateCurrentLyrics(_currentLyrics);

                        if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
                        {
                            _standaloneWebServer.CurrentLyrics = domainLyrics;
                            _standaloneWebServer.BroadcastState("lyrics_change");
                        }

                        var hasTrans = LyricParser.HasTranslation(_currentLyrics) && LyricParser.NeedsTranslation(_currentLyrics);
                        _hasTranslation = hasTrans;
                        _showTranslation = hasTrans;
                        if (_lyricTransBtn != null)
                        {
                            _lyricTransBtn.Visible = hasTrans;
                            UpdateTranslationButtonHighlight();
                        }
                        _controlBar?.UpdateTranslationAvailability(hasTrans);

                        RefreshLyricListView();
                        _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
                        _nowPlayingView.SetTranslationState(_showTranslation);

                        var posSec = _isTuiAudioDisabled ? _webVirtualPosition : _player.CurrentPositionSeconds;
                        UpdateLyrics(posSec);

                        AppLogger.Info("MainWindow.Connect", $"Synchronized {domainLyrics.Count} lyric lines for song: {payload.Title} ({payload.SongMid})");
                    }
                });
            };

            _connectServer.CurrentCoverPathProvider = () =>
            {
                if (!string.IsNullOrEmpty(_currentCoverFilePath) && File.Exists(_currentCoverFilePath))
                {
                    return _currentCoverFilePath;
                }
                return null;
            };

            _connectServer.CoverPathByMidProvider = async (mid) =>
            {
                if (string.IsNullOrEmpty(mid)) return null;

                if (_activeSong != null && _activeSong.Mid == mid && !string.IsNullOrEmpty(_currentCoverFilePath) && File.Exists(_currentCoverFilePath))
                {
                    return _currentCoverFilePath;
                }

                var cleanMid = mid.StartsWith("pc_local_", StringComparison.OrdinalIgnoreCase)
                    ? "local_" + mid["pc_local_".Length..]
                    : mid;

                var queueSongs = PlaybackQueueService.Instance.ActiveSongs;
                var song = queueSongs.FirstOrDefault(s => s.Mid == mid || s.Mid == cleanMid);

                if (song == null)
                {
                    var localSongs = LocalMusicService.GetCachedSongs();
                    song = localSongs.FirstOrDefault(s => s.Mid == mid || s.Mid == cleanMid);
                }

                if (song == null && cleanMid.StartsWith("local_", StringComparison.OrdinalIgnoreCase))
                {
                    var hashSuffix = cleanMid["local_".Length..];
                    var localSongs = LocalMusicService.GetCachedSongs();
                    song = localSongs.FirstOrDefault(s => LocalMusicService.ComputeMd5(s.LocalFilePath ?? "").StartsWith(hashSuffix, StringComparison.OrdinalIgnoreCase));
                }

                if (song != null)
                {
                    if (song.IsWebDav || !string.IsNullOrEmpty(song.WebDavHref))
                    {
                        var servers = WebDavService.GetServers();
                        var server = (!string.IsNullOrEmpty(song.WebDavServerId) ? servers.Find(s => s.Id == song.WebDavServerId) : null)
                                     ?? WebDavService.GetActiveServer();
                        if (server != null)
                        {
                            return await WebDavService.EnsureCoverAsync(server, song).ConfigureAwait(false);
                        }
                    }
                    else if (song.IsLocal || !string.IsNullOrEmpty(song.LocalFilePath))
                    {
                        return await LocalMusicService.EnsureCoverAsync(song).ConfigureAwait(false);
                    }
                }

                return null;
            };

                _connectServer.CurrentLyricsTextProvider = () => GetOrBuildFormattedLyricsPayload();

                _connectServer.Start();
                _connectMdns = new ConnectMdnsService(_connectStorage, port: _connectServer.ActualPort);
                _connectMdns.Start();
                AppLogger.Info("MainWindow.Connect", $"Melodist Connect service & mDNS started on port {_connectServer.ActualPort}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainWindow.Connect", "Failed to start Connect service", ex);
            }
        }

        public void BroadcastConnectPlayerState()
        {
            if (_connectServer == null || !_connectServer.IsRunning || _connectServer.ConnectedCount == 0) return;

            try
            {
                var isPlaying = _isTuiAudioDisabled ? _isWebPlaying : _player.IsPlaying;
                var hasActiveSong = _activeSong != null;
                var isActuallyActive = isPlaying || _player.TotalDurationSeconds > 0 || _isWebPlaying || hasActiveSong;
                var posSec = _isTuiAudioDisabled ? _webVirtualPosition : _player.CurrentPositionSeconds;
                var durSec = _player.TotalDurationSeconds > 0 ? _player.TotalDurationSeconds : (_activeSong?.Duration ?? 0);
                var queue = PlaybackQueueService.Instance.ActiveSongs;
                var curIdx = PlaybackQueueService.Instance.CurrentIndex;

                var actualPort = _connectServer.ActualPort;

                ConnectSong? connectSong = null;
                if (_activeSong != null)
                {
                    connectSong = ConnectSong.FromDomainSong(_activeSong, _actualQualityTier, actualPort);
                }

                ConnectSong? prevSong = null;
                ConnectSong? nextSong = null;
                var prevDomain = PlaybackQueueService.Instance.PeekPrevSong();
                if (prevDomain != null)
                {
                    prevSong = ConnectSong.FromDomainSong(prevDomain, AudioQualityTier.SQ, actualPort);
                }
                var nextDomain = PlaybackQueueService.Instance.PeekNextSong();
                if (nextDomain != null)
                {
                    nextSong = ConnectSong.FromDomainSong(nextDomain, AudioQualityTier.SQ, actualPort);
                }

                string? lrcPayload = GetOrBuildFormattedLyricsPayload();

                var availableTiers = new List<string> { "Standard", "HQ", "SQ", "HiRes", "Master" };

                var evt = new PlayerStateEvent(
                    CurrentSong: connectSong,
                    IsPlaying: isPlaying,
                    PositionMs: (long)(posSec * 1000),
                    DurationMs: (long)(durSec * 1000),
                    Volume: _player.Volume / 100.0f,
                    QueueSize: queue.Count,
                    CurrentIndex: curIdx,
                    LoopMode: _currentPlaybackMode.ToConnectLoopMode(),
                    IsAodActive: _isAodMode,
                    PrevSong: prevSong,
                    NextSong: nextSong,
                    CurrentTier: _actualQualityTier.ToString(),
                    AvailableTiers: availableTiers,
                    IsFavorite: isActuallyActive && _activeSong != null && IsSongFavorite(_activeSong),
                    IsRadioMode: RadioService.Instance.HasActiveRadio,
                    LyricOffsetMs: 0,
                    Lyrics: lrcPayload
                );

                _connectServer.BroadcastPlayerState(evt);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Connect", $"BroadcastConnectPlayerState error: {ex.Message}");
        }
    }

    public void BroadcastConnectQueueState()
    {
        if (_connectServer == null || !_connectServer.IsRunning || _connectServer.ConnectedCount == 0) return;

        try
        {
            var queue = PlaybackQueueService.Instance.ActiveSongs;
            var curIdx = PlaybackQueueService.Instance.CurrentIndex;
            var actualPort = _connectServer.ActualPort;
            var connectQueue = queue.Select(s => ConnectSong.FromDomainSong(s, AudioQualityTier.SQ, actualPort)).ToList();

            var evt = new QueueStateEvent(
                Queue: connectQueue,
                CurrentIndex: curIdx
            );

            _connectServer.BroadcastQueueState(evt);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Connect", $"BroadcastConnectQueueState error: {ex.Message}");
        }
    }

    public void BroadcastConnectLyrics()
    {
        if (_connectServer == null || !_connectServer.IsRunning || _connectServer.ConnectedCount == 0) return;
        if (_activeSong == null || _currentLyrics == null || _currentLyrics.Count == 0) return;
        if (!string.IsNullOrEmpty(_lastRemoteSyncedSongMid) && _lastRemoteSyncedSongMid == _activeSong.Mid)
        {
            return;
        }

        try
        {
            var lines = _currentLyrics.Select(l => new ConnectLyricLine(
                TimestampMs: (long)l.Timestamp.TotalMilliseconds,
                Text: l.Text,
                TransText: l.Trans
            )).ToList();

            var payload = new LyricsSyncPayload(
                SongMid: _activeSong.Mid,
                Title: _activeSong.Title,
                Singer: _activeSong.Artist,
                Lyrics: lines,
                SourceDeviceId: _connectStorage?.LocalDeviceId ?? "",
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            );

            _connectServer.BroadcastLyrics(payload);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Connect", $"BroadcastConnectLyrics error: {ex.Message}");
        }
    }

    private void ShowConnectDialog()
    {
        if (_connectServer == null || _connectStorage == null)
        {
            SetupConnectService();
        }

        var dlg = new ConnectDialog(_connectServer!, _connectStorage!, _connectMdns);
        _activeModalDialog = dlg;
        Application.Run(dlg);
        _activeModalDialog = null;
    }

    private void AdjustVolumeDirect(int targetVolume)
    {
        var vol = Math.Clamp(targetVolume, 0, 100);
        _player.SetVolume(vol);
        _controlBar.UpdateVolume(vol, vol == 0);
        _mprisService.UpdateVolume(vol);
        UserSession.Current.Volume = vol;
        BroadcastConnectPlayerState();
    }
}
