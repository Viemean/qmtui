namespace QmTui.Models;

/// <summary>
/// 播放队列来源上下文（对应 Kotlin PlaybackSourceContext）
/// 用于在播满 15 秒时向云端上报歌单或专辑历史
/// </summary>
public abstract record PlaybackSourceContext
{
    public sealed record Playlist(string Id, string Title) : PlaybackSourceContext;
    public sealed record Album(string Mid, long Id, string Title) : PlaybackSourceContext;
}
