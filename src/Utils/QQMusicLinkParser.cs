using System.Text.RegularExpressions;

namespace QmTui.Utils;

/// <summary>搜索框输入类型。</summary>
public enum SearchInputKind
{
    None,
    Nickname,
    PlaylistLink,
    NumericId,
    ShortShareLink
}

/// <summary>纯数字输入实际可用的目标。</summary>
public enum NumericTargetKind
{
    None,
    UserPlaylists,
    PublicPlaylist,
    Both
}

/// <summary>
/// 判断搜索框输入是昵称、歌单链接、数字 ID 还是暂不支持的短链。
/// </summary>
public static partial class QQMusicLinkParser
{
    // 匹配 .../playlist/<id>、.../taoge/<id>、taoge.html?id=<id>、?disstid=<id>、#disstid=<id>
    [GeneratedRegex(@"(?:/(?:playlist|taoge)/|taoge\.html\?id=|(?:[?&#]|^)disstid=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"c6\.y\.qq\.com/base/fcgi-bin/u", RegexOptions.IgnoreCase)]
    private static partial Regex ShortLinkRegex();

    /// <summary>分类输入；<paramref name="value"/> 仅在链接或纯数字时大于 0。</summary>
    public static SearchInputKind Classify(string? raw, out long value)
    {
        value = 0;
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return SearchInputKind.None;
        if (ShortLinkRegex().IsMatch(text)) return SearchInputKind.ShortShareLink;

        var match = PlaylistIdRegex().Match(text);
        if (match.Success && long.TryParse(match.Groups[1].Value, out value) && value > 0)
        {
            return SearchInputKind.PlaylistLink;
        }
        value = 0;

        if (text.All(char.IsAsciiDigit) && long.TryParse(text, out value) && value > 0)
        {
            return SearchInputKind.NumericId;
        }

        value = 0;
        return SearchInputKind.Nickname;
    }

    /// <summary>根据 QQ 号歌单数与公开歌单探测结果决定数字输入的目标。</summary>
    public static NumericTargetKind ChooseNumericTarget(int userPlaylistCount, bool hasPublicPlaylist) =>
        (userPlaylistCount > 0, hasPublicPlaylist) switch
        {
            (true, true) => NumericTargetKind.Both,
            (true, false) => NumericTargetKind.UserPlaylists,
            (false, true) => NumericTargetKind.PublicPlaylist,
            _ => NumericTargetKind.None
        };
}
