namespace QmTui.Models;

/// <summary>
/// 专辑数据模型
/// </summary>
public sealed record Album(
    long Id,
    string Mid,
    string Title,
    string Artist,
    int SongCount,
    string CoverUrl = "",
    long PubTime = 0,
    string PublishDate = ""
);

/// <summary>
/// 专辑详情数据模型
/// </summary>
public sealed record AlbumDetail(
    string Mid,
    string Name,
    string Artist,
    string PublishDate,
    string Company,
    string Description,
    List<Song> Songs,
    long Id = 0
);

/// <summary>
/// 歌手详情数据模型
/// </summary>
public sealed record ArtistDetail(
    string Mid,
    long Id,
    string Name,
    string Brief,
    List<Song> Songs
);

/// <summary>
/// 歌手子模式（单曲或专辑）
/// </summary>
public enum SingerSubMode
{
    Songs,
    Albums
}

/// <summary>
/// 歌单数据模型
/// </summary>
public record Playlist(
    long DirId,
    string Name,
    int SongCount,
    long Tid = 0,
    bool IsFav = false,
    string PicUrl = "",
    long OwnerUin = 0
)
{
    /// <summary>非 0 表示这是他人（或指定 QQ 号）的歌单，读取时必须带上所有者。</summary>
    public bool IsOwnedByOther => OwnerUin > 0;

    public bool IsMyFavorite => DirId == 201 && !IsOwnedByOther;

    public string Title => Name;
    public int SongNum => SongCount;
    public bool IsCreated => !IsFav && !IsOwnedByOther;

    public string DisplayTitle => IsMyFavorite
        ? $"[我喜欢] ({SongCount} 首)"
        : (IsFav ? $"[收藏] {Name} ({SongCount} 首)" : $"{Name} ({SongCount} 首)");
}

/// <summary>
/// QQ 音乐用户搜索结果。
/// </summary>
public sealed record UserSummary(
    string Name,
    string EncryptUin,
    string Uin,
    int FansNum,
    int PlaylistCount,
    bool IsMyself,
    string Identify = ""
);

/// <summary>
/// 公开歌单详情（元数据 + 完整歌曲列表）。
/// </summary>
public sealed record PublicPlaylistDetail(Playlist Playlist, List<Song> Songs);
