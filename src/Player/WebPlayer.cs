using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// 轻量 Web 协同播放器实现（TUI 负责曲库/解析/会话调度，手机浏览器负责 HTML5 硬件音频输出或纯遥控）
/// </summary>
public sealed class WebPlayer : IPlayer
{
    private readonly WebPlaybackServer _server = new();
    private readonly int _preferredPort;
    private readonly bool _initialAudioEnabled;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _virtualTickerCts;
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public double CurrentPositionSeconds { get; private set; }
    public double TotalDurationSeconds { get; private set; }
    public int Volume { get; private set; } = 80;
    public bool AudioOutputEnabled => _server.AudioOutputEnabled;
    public WebPlaybackServer Server => _server;
    public string Url => _server.LocalUrl;
    public int Port => _server.Port;

    public event Action<double>? PositionUpdated;
    public event Action? PlaybackFinished;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? TogglePlayRequested;

    /// <summary>
    /// 初始化轻量 Web 协同播放器实例。
    /// </summary>
    /// <param name="preferredPort">期望绑定的服务监听端口，默认 9999。</param>
    /// <param name="initialAudioEnabled">是否默认开启浏览器端 HTML5 音频输出。</param>
    public WebPlayer(int preferredPort = 9999, bool initialAudioEnabled = true)
    {
        _preferredPort = preferredPort;
        _initialAudioEnabled = initialAudioEnabled;
    }

    /// <summary>
    /// 注册内置 Web 播放服务器事件并启动 HTTP/SSE 监听服务。
    /// </summary>
    public void Initialize()
    {
        _server.NextRequested += () => NextRequested?.Invoke();
        _server.PreviousRequested += () => PreviousRequested?.Invoke();
        _server.TogglePlayRequested += () => TogglePlayRequested?.Invoke();
        _server.PlaybackEnded += () =>
        {
            StopVirtualTicker();
            PlaybackFinished?.Invoke();
        };
        _server.SeekRequested += sec => _ = SeekAsync(sec);
        _server.VolumeRequested += vol => SetVolume(vol);
        _server.AllClientsDisconnected += () =>
        {
            lock (_lock)
            {
                if (IsPlaying)
                {
                    AppLogger.Info("WebPlayer", "All web clients disconnected. Automatically pausing WebPlayer.");
                    _ = TogglePauseAsync();
                }
            }
        };
        _server.ProgressReported += (pos, dur) =>
        {
            CurrentPositionSeconds = pos;
            if (dur > 0) TotalDurationSeconds = dur;
            PositionUpdated?.Invoke(pos);
        };
        _server.AudioOutputToggled += enabled =>
        {
            AppLogger.Info("WebPlayer", $"Web audio output toggled: {(enabled ? "Enabled" : "Disabled")}");
            lock (_lock)
            {
                if (IsPlaying)
                {
                    if (!enabled)
                    {
                        StartVirtualTicker();
                    }
                    else
                    {
                        StopVirtualTicker();
                    }
                }
            }
        };

        var ok = _server.Start(_preferredPort, _initialAudioEnabled);
        if (ok)
        {
            AppLogger.Info("WebPlayer", $"WebPlayer initialized successfully at {_server.LocalUrl}");
        }
        else
        {
            AppLogger.Error("WebPlayer", "Failed to start underlying WebPlaybackServer");
        }
    }

    /// <summary>
    /// 广播新曲目播放状态并触发 Web 客户端加载音频流。
    /// </summary>
    /// <param name="url">音频直链 URL。</param>
    /// <param name="duration">曲目总时长（秒）。</param>
    /// <param name="startPosition">起播起始时间戳偏移（秒）。</param>
    public Task PlayAsync(string url, double duration, double startPosition = 0)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            TotalDurationSeconds = duration;
            CurrentPositionSeconds = startPosition;
            IsPlaying = true;

            _server.CurrentPlayUrl = url;
            _server.TotalDurationSeconds = duration;
            _server.CurrentPositionSeconds = startPosition;
            _server.IsPlaying = true;
            _server.BroadcastState("play");

            AppLogger.Info("WebPlayer", $"PlayAsync: url='{url}', duration={duration:F1}s, startPos={startPosition:F1}s, audioEnabled={AudioOutputEnabled}");

            if (!AudioOutputEnabled)
            {
                StartVirtualTicker();
            }
            else
            {
                StopVirtualTicker();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 切换播放器暂停与恢复播放状态，并向网页端广播状态同步。
    /// </summary>
    public Task TogglePauseAsync()
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            IsPlaying = !IsPlaying;
            _server.IsPlaying = IsPlaying;
            _server.BroadcastState(IsPlaying ? "resume" : "pause");
            AppLogger.Info("WebPlayer", $"TogglePauseAsync: IsPlaying={IsPlaying}");

            if (IsPlaying)
            {
                if (!AudioOutputEnabled) StartVirtualTicker();
            }
            else
            {
                StopVirtualTicker();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止播放，重置虚拟计时器并向网页端同步 stop 状态。
    /// </summary>
    public Task StopAsync()
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            IsPlaying = false;
            CurrentPositionSeconds = 0;
            _server.IsPlaying = false;
            _server.CurrentPositionSeconds = 0;
            _server.BroadcastState("stop");
            StopVirtualTicker();
            AppLogger.Info("WebPlayer", "Playback stopped");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 调整当前播放位置并向已连接客户端广播 seek 事件。
    /// </summary>
    /// <param name="seconds">跳转目标时间位置（秒）。</param>
    public Task SeekAsync(double seconds)
    {
        lock (_lock)
        {
            if (_disposed) return Task.CompletedTask;

            CurrentPositionSeconds = Math.Clamp(seconds, 0, TotalDurationSeconds > 0 ? TotalDurationSeconds : 3600);
            _server.CurrentPositionSeconds = CurrentPositionSeconds;
            _server.BroadcastState("seek");
            AppLogger.Info("WebPlayer", $"Seek to {CurrentPositionSeconds:F1}s");
        }

        PositionUpdated?.Invoke(CurrentPositionSeconds);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设置并向已连接网页端广播音量变化。
    /// </summary>
    /// <param name="vol">音量百分比（0 到 100）。</param>
    public void SetVolume(int vol)
    {
        Volume = Math.Clamp(vol, 0, 100);
        _server.Volume = Volume;
        _server.BroadcastState("volume");
    }

    /// <summary>
    /// 更新当前曲目元数据并向客户端广播曲目变更事件。
    /// </summary>
    /// <param name="song">当前曲目实体。</param>
    public void UpdateCurrentSong(Song? song)
    {
        _server.CurrentSong = song;
        _server.BroadcastState("song_change");
    }

    /// <summary>
    /// 同步当前动态歌词至网页端展示。
    /// </summary>
    /// <param name="lyrics">解析后的歌词行列表。</param>
    public void UpdateCurrentLyrics(List<LyricLine>? lyrics)
    {
        _server.CurrentLyrics = lyrics;
        _server.BroadcastState("lyrics_change");
    }

    private void StartVirtualTicker()
    {
        StopVirtualTicker();
        _virtualTickerCts = new CancellationTokenSource();
        var ct = _virtualTickerCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool shouldFinish = false;
                double pos = 0;
                lock (_lock)
                {
                    if (!IsPlaying || AudioOutputEnabled) break;
                    CurrentPositionSeconds += 0.5;
                    pos = CurrentPositionSeconds;
                    _server.CurrentPositionSeconds = pos;
                    if (TotalDurationSeconds > 0 && CurrentPositionSeconds >= TotalDurationSeconds - 0.5)
                    {
                        shouldFinish = true;
                    }
                }

                PositionUpdated?.Invoke(pos);

                if (shouldFinish)
                {
                    PlaybackFinished?.Invoke();
                    break;
                }
            }
        }, ct);
    }

    private void StopVirtualTicker()
    {
        try
        {
            _virtualTickerCts?.Cancel();
        }
        catch { }
        _virtualTickerCts = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopVirtualTicker();
            _server.Dispose();
        }
    }
}
