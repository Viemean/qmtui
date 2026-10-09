using QmTui.Models;

namespace QmTui.Player;

/// <summary>
/// 播放器通用抽象接口（解耦原生 GStreamer 管道与轻量 Web 协同播放器）
/// </summary>
public interface IPlayer : IDisposable
{
    bool IsPlaying { get; }
    double CurrentPositionSeconds { get; }
    double TotalDurationSeconds { get; }
    int Volume { get; }

    event Action<double>? PositionUpdated;
    event Action? PlaybackFinished;

    /// <summary>
    /// 初始化播放器底层音频管线与驱动资源。
    /// </summary>
    void Initialize();

    /// <summary>
    /// 异步加载并播放指定音频 URL 流或本地文件路径。
    /// </summary>
    /// <param name="url">音频文件的绝对路径或流式网络 URL。</param>
    /// <param name="duration">音频预估或元数据总时长（秒）。</param>
    /// <param name="startPosition">起播起始时间戳偏移（秒），默认从 0 开始。</param>
    Task PlayAsync(string url, double duration, double startPosition = 0);

    /// <summary>
    /// 切换播放器暂停与继续播放状态。
    /// </summary>
    Task TogglePauseAsync();

    /// <summary>
    /// 停止当前播放并释放当前管线媒体缓冲。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 跳转至指定的播放时间点。
    /// </summary>
    /// <param name="seconds">绝对目标时间位置（秒）。</param>
    Task SeekAsync(double seconds);

    /// <summary>
    /// 设置音频输出音量百分比。
    /// </summary>
    /// <param name="vol">音量值（0 到 100）。</param>
    void SetVolume(int vol);

    /// <summary>
    /// 更新当前曲目元数据（供 Web 播放器向客户端同步展示封面与标题等信息）
    /// </summary>
    void UpdateCurrentSong(Song? song) { }

    /// <summary>
    /// 更新当前曲目歌词（供 Web 播放器向客户端同步动态歌词）
    /// </summary>
    void UpdateCurrentLyrics(List<LyricLine>? lyrics) { }
}
