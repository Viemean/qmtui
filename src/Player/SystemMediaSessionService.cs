using QmTui.Models;

namespace QmTui.Player;

/// <summary>
/// One application-facing media-session surface backed by MPRIS2, Windows SMTC,
/// or macOS Now Playing according to the current operating system.
/// </summary>
public sealed class SystemMediaSessionService : IDisposable
{
    private readonly MprisService? _linux;
    private readonly WindowsMediaSessionService? _windows;
    private readonly MacMediaSessionService? _mac;

    public SystemMediaSessionService()
    {
        if (OperatingSystem.IsLinux()) _linux = new MprisService();
        else if (OperatingSystem.IsWindows()) _windows = new WindowsMediaSessionService();
        else if (OperatingSystem.IsMacOS()) _mac = new MacMediaSessionService();
    }

    public Func<Task>? PlayPauseHandler
    {
        set
        {
            if (_linux != null) _linux.PlayPauseHandler = value;
            if (_mac != null) _mac.PlayPauseHandler = value;
        }
    }

    public Func<Task>? PlayHandler
    {
        set
        {
            if (_linux != null) _linux.PlayHandler = value;
            if (_windows != null) _windows.PlayHandler = value;
            if (_mac != null) _mac.PlayHandler = value;
        }
    }

    public Func<Task>? PauseHandler
    {
        set
        {
            if (_linux != null) _linux.PauseHandler = value;
            if (_windows != null) _windows.PauseHandler = value;
            if (_mac != null) _mac.PauseHandler = value;
        }
    }

    public Func<Task>? StopHandler
    {
        set
        {
            if (_linux != null) _linux.StopHandler = value;
            if (_windows != null) _windows.StopHandler = value;
            if (_mac != null) _mac.StopHandler = value;
        }
    }

    public Func<Task>? NextHandler
    {
        set
        {
            if (_linux != null) _linux.NextHandler = value;
            if (_windows != null) _windows.NextHandler = value;
            if (_mac != null) _mac.NextHandler = value;
        }
    }

    public Func<Task>? PreviousHandler
    {
        set
        {
            if (_linux != null) _linux.PreviousHandler = value;
            if (_windows != null) _windows.PreviousHandler = value;
            if (_mac != null) _mac.PreviousHandler = value;
        }
    }

    public Func<double, Task>? SeekHandler
    {
        set
        {
            if (_linux != null) _linux.SeekHandler = value;
        }
    }

    public Func<double, Task>? SetPositionHandler
    {
        set
        {
            if (_linux != null) _linux.SetPositionHandler = value;
            if (_windows != null) _windows.SetPositionHandler = value;
            if (_mac != null) _mac.SetPositionHandler = value;
        }
    }

    public Action<double>? VolumeSetHandler { set { if (_linux != null) _linux.VolumeSetHandler = value; } }
    public Action<string>? LoopStatusSetHandler { set { if (_linux != null) _linux.LoopStatusSetHandler = value; } }
    public Action<bool>? ShuffleSetHandler { set { if (_linux != null) _linux.ShuffleSetHandler = value; } }
    public Action? QuitHandler { set { if (_linux != null) _linux.QuitHandler = value; } }

    /// <summary>
    /// 启动适配当前操作系统的多媒体控制会话监听服务。
    /// </summary>
    public Task StartAsync() =>
        _linux?.StartAsync() ?? _windows?.StartAsync() ?? _mac?.StartAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 同步播放状态（播放中/暂停）至系统媒体控制面板。
    /// </summary>
    /// <param name="isPlaying">当前是否处于播放中状态。</param>
    public void UpdatePlaybackStatus(bool isPlaying)
    {
        _linux?.UpdatePlaybackStatus(isPlaying);
        _windows?.UpdatePlaybackStatus(isPlaying);
        _mac?.UpdatePlaybackStatus(isPlaying);
    }

    /// <summary>
    /// 同步当前曲目元数据与本地封面路径至系统媒体中心。
    /// </summary>
    /// <param name="song">当前播放的歌曲实体。</param>
    /// <param name="coverPath">本地封面图像绝对路径。</param>
    public void UpdateSong(Song? song, string? coverPath = null)
    {
        CurrentSong = song;
        _linux?.UpdateSong(song, coverPath);
        _windows?.UpdateSong(song, coverPath);
        _mac?.UpdateSong(song, coverPath);
    }

    /// <summary>
    /// 异步更新曲目封面显示。
    /// </summary>
    /// <param name="coverPath">本地封面文件路径。</param>
    public void UpdateCover(string? coverPath)
    {
        _linux?.UpdateCover(coverPath);
        _windows?.UpdateCover(CurrentSong, coverPath);
        _mac?.UpdateCover(coverPath);
    }

    private Song? CurrentSong { get; set; }

    public void UpdateVolume(int volumePercent) => _linux?.UpdateVolume(volumePercent);
    public void UpdatePlaybackMode(PlaybackMode mode) => _linux?.UpdatePlaybackMode(mode);

    /// <summary>
    /// 同步当前播放时间戳与总时长至系统媒体部件。
    /// </summary>
    /// <param name="seconds">当前已播放秒数。</param>
    /// <param name="durationSeconds">曲目总时长秒数。</param>
    public void UpdatePosition(double seconds, double durationSeconds = 0)
    {
        _linux?.UpdatePosition(seconds);
        _windows?.UpdatePosition(seconds, durationSeconds > 0 ? durationSeconds : CurrentSong?.Duration ?? 0);
        _mac?.UpdatePosition(seconds);
    }

    /// <summary>
    /// 广播手动 Seek 定位事件至系统媒体会话。
    /// </summary>
    /// <param name="seconds">跳转后的目标时间点（秒）。</param>
    public void EmitSeeked(double seconds)
    {
        _linux?.EmitSeeked(seconds);
        _windows?.UpdatePosition(seconds, CurrentSong?.Duration ?? 0);
        _mac?.UpdatePosition(seconds, force: true);
    }

    public void Dispose()
    {
        _linux?.Dispose();
        _windows?.Dispose();
        _mac?.Dispose();
    }
}
