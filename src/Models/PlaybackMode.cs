namespace QmTui.Models;

/// <summary>
/// 播放循环与随机模式（对齐移动端3种模式）
/// </summary>
public enum PlaybackMode
{
    /// <summary>
    /// 列表循环 (首尾循环)
    /// </summary>
    ListLoop = 0,

    /// <summary>
    /// 单曲循环 (结束后重播该曲)
    /// </summary>
    SingleLoop = 1,

    /// <summary>
    /// 随机播放 (列表内洗牌抽取)
    /// </summary>
    Shuffle = 2
}

public static class PlaybackModeHelper
{
    public static string GetBadge(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => "列表",
        PlaybackMode.SingleLoop => "单曲",
        PlaybackMode.Shuffle => "随机",
        _ => "列表"
    };

    public static string GetName(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => "列表循环",
        PlaybackMode.SingleLoop => "单曲循环",
        PlaybackMode.Shuffle => "随机播放",
        _ => "列表循环"
    };

    public static PlaybackMode Next(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => PlaybackMode.SingleLoop,
        PlaybackMode.SingleLoop => PlaybackMode.Shuffle,
        PlaybackMode.Shuffle => PlaybackMode.ListLoop,
        _ => PlaybackMode.ListLoop
    };

    public static (string LoopStatus, bool Shuffle) ToMpris(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => ("Playlist", false),
        PlaybackMode.SingleLoop => ("Track", false),
        PlaybackMode.Shuffle => ("Playlist", true),
        _ => ("Playlist", false)
    };

    public static PlaybackMode FromMpris(string loopStatus, bool shuffle)
    {
        if (shuffle) return PlaybackMode.Shuffle;
        return loopStatus switch
        {
            "Track" => PlaybackMode.SingleLoop,
            _ => PlaybackMode.ListLoop
        };
    }

    public static string ToConnectLoopMode(this PlaybackMode mode) => mode switch
    {
        PlaybackMode.ListLoop => "ListRepeat",
        PlaybackMode.SingleLoop => "SingleRepeat",
        PlaybackMode.Shuffle => "Shuffle",
        _ => "ListRepeat"
    };

    public static PlaybackMode FromConnectLoopMode(string? modeStr)
    {
        if (string.IsNullOrWhiteSpace(modeStr)) return PlaybackMode.ListLoop;
        var s = modeStr.Trim();
        if (s.Contains("Single", StringComparison.OrdinalIgnoreCase) || s.Contains("One", StringComparison.OrdinalIgnoreCase) || s.Contains("Track", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackMode.SingleLoop;
        }
        if (s.Contains("Shuffle", StringComparison.OrdinalIgnoreCase) || s.Contains("Random", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackMode.Shuffle;
        }
        return PlaybackMode.ListLoop;
    }
}
