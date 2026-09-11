using System;
using System.Collections.Generic;

namespace QmTui.Models;

/// <summary>
/// 每日30首按日缓存快照
/// </summary>
public sealed class DailyRecommendCache
{
    public string Date { get; set; } = "";
    public string Uin { get; set; } = "";
    public List<Song> Songs { get; set; } = [];
}

/// <summary>
/// “我的喜欢”全量歌曲快照缓存
/// </summary>
public sealed class FavoriteCache
{
    public string Uin { get; set; } = "";
    public int TotalCount { get; set; }
    public string FirstSongMid { get; set; } = "";
    public DateTime LastSyncTime { get; set; }
    public List<Song> Songs { get; set; } = [];
}
