using System.Collections.Generic;

namespace QmTui.Models;

/// <summary>
/// 歌曲评论实体
/// </summary>
public sealed record SongComment(
    string CommentId,
    string Nick,
    string AvatarUrl,
    string Content,
    long TimeSec,
    int PraiseNum,
    bool IsHot,
    string PicUrl = "",
    string PicSize = "",
    string Location = "",
    string SeqNo = ""
);

/// <summary>
/// 评论区分页结果
/// </summary>
public sealed record CommentPage(
    int TotalCount,
    IReadOnlyList<SongComment> HotComments,
    IReadOnlyList<SongComment> Comments,
    bool HasMore,
    string LastSeqNo = ""
);
