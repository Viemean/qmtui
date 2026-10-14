namespace QmTui.Models;

/// <summary>
/// 一起听房间元数据实体
/// </summary>
public sealed class ListenTogetherRoomInfo
{
    public string ShowId { get; init; } = "";
    public string CreatorUin { get; init; } = "";
    public string ShareUrl { get; init; } = "";
    public long CurrentSongId { get; init; }
    public string CurrentSongTitle { get; init; } = "";
    public int UsersCount { get; set; }
    public bool IsValid { get; set; } = true;
}

/// <summary>
/// 一起听心跳同步状态数据
/// </summary>
public sealed class ListenTogetherSyncState
{
    public string ShowId { get; init; } = "";
    public long CurrentSongId { get; init; }
    public string SongTitle { get; init; } = "";
    public int PlayStatus { get; init; } // 1: 播放中, 2/0: 暂停
    public long SongOffsetMs { get; init; }
    public long SongPlayVersion { get; init; }
    public long MediaVersion { get; init; }
    public int UsersCount { get; init; }
    public bool IsRoomValid { get; init; } = true;
    public IReadOnlyList<string> UserEncUins { get; init; } = [];
}

/// <summary>
/// 一起听好友空间统计数据
/// </summary>
public sealed class ListenTogetherSpaceStats
{
    public long ListenMinutes { get; set; }
    public int ListenSongsCount { get; set; }
    public int FavoriteSongsCount { get; set; }
}
