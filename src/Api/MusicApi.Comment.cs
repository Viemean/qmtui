using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Models;
using QmTui.UI;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    /// <summary>
    /// 获取歌曲评论区分页列表（优先走现代网关，降级使用传统 H5 接口）
    /// </summary>
    public static async Task<CommentPage?> GetSongCommentsAsync(
        long songId,
        string songMid,
        int pageNum = 0,
        int pageSize = 25,
        string lastCommentSeqNo = "",
        CancellationToken ct = default)
    {
        var actualId = songId;
        if (actualId <= 0 && !string.IsNullOrEmpty(songMid))
        {
            actualId = await ResolveSongIdAsync(songMid, ct).ConfigureAwait(false);
        }
        if (actualId <= 0) return null;

        var page = Math.Max(0, pageNum);
        var size = Math.Clamp(pageSize, 1, 50);

        // 仅在终端支持图形协议时向网关开启图片配图，无图终端传 0 彻底杜绝图片下发与内存解码
        var picEnable = TerminalImageHelper.IsImageSupported ? 1 : 0;

        var modern = await FetchModernCommentsAsync(actualId, page, size, lastCommentSeqNo, picEnable, ct).ConfigureAwait(false);
        if (modern != null) return modern;

        return await FetchLegacyCommentsAsync(actualId, page, size, ct).ConfigureAwait(false);
    }

    private static async Task<CommentPage?> FetchModernCommentsAsync(
        long songId,
        int page,
        int size,
        string lastCommentSeqNo,
        int picEnable,
        CancellationToken ct)
    {
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        var withHot = page == 0 ? 1 : 0;
        var seqEscaped = JsonEncodedText.Encode(lastCommentSeqNo ?? "").ToString();
        var payload = $$"""
        {
            "comm": {
                "cv": 4747474,
                "ct": 24,
                "format": "json",
                "inCharset": "utf-8",
                "outCharset": "utf-8",
                "notice": 0,
                "platform": "yqq.json",
                "needNewCode": 1,
                "uin": 0
            },
            "req": {
                "module": "music.globalComment.CommentRead",
                "method": "GetNewCommentList",
                "param": {
                    "BizType": 1,
                    "BizId": "{{songId}}",
                    "LastCommentSeqNo": "{{seqEscaped}}",
                    "PageSize": {{size}},
                    "PageNum": {{page}},
                    "FromCommentId": "",
                    "WithHot": {{withHot}},
                    "PicEnable": {{picEnable}}
                }
            }
        }
        """;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("req", out var reqObj)) return null;
            if (!reqObj.TryGetProperty("code", out var codeProp) || codeProp.GetInt32() != 0) return null;
            if (!reqObj.TryGetProperty("data", out var dataObj)) return null;

            int totalCount = 0;
            if (dataObj.TryGetProperty("TotalCmNum", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
            {
                totalCount = totalProp.GetInt32();
            }
            else if (dataObj.TryGetProperty("CommentList", out var cl) && cl.TryGetProperty("Total", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            {
                totalCount = tProp.GetInt32();
            }

            var hotList = new List<SongComment>();
            if (page == 0)
            {
                var seenHotIds = new HashSet<string>();
                string[] hotKeys = ["CommentList3", "CommentList2"];
                foreach (var key in hotKeys)
                {
                    if (dataObj.TryGetProperty(key, out var hListObj) && hListObj.TryGetProperty("Comments", out var hArr) && hArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in hArr.EnumerateArray())
                        {
                            var c = ParseModernCommentElement(elem, isHot: true);
                            if (c != null && seenHotIds.Add(c.CommentId))
                            {
                                hotList.Add(c);
                            }
                        }
                    }
                }
            }

            var normalList = new List<SongComment>();
            bool serverHasMore = false;
            string lastSeqNo = "";
            if (dataObj.TryGetProperty("CommentList", out var commentListObj))
            {
                if (commentListObj.TryGetProperty("HasMore", out var hmProp) && hmProp.ValueKind == JsonValueKind.Number)
                {
                    serverHasMore = hmProp.GetInt32() == 1;
                }
                if (commentListObj.TryGetProperty("Comments", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in cArr.EnumerateArray())
                    {
                        var c = ParseModernCommentElement(elem, isHot: false);
                        if (c != null)
                        {
                            normalList.Add(c);
                            if (!string.IsNullOrEmpty(c.SeqNo))
                            {
                                lastSeqNo = c.SeqNo;
                            }
                        }
                    }
                }
            }

            bool hasMore = serverHasMore && normalList.Count > 0;
            return new CommentPage(totalCount, hotList, normalList, hasMore, lastSeqNo);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"FetchModernCommentsAsync failed for songId={songId}", ex);
            return null;
        }
    }

    private static SongComment? ParseModernCommentElement(JsonElement obj, bool isHot)
    {
        try
        {
            var seqNo = obj.TryGetProperty("SeqNo", out var sq) ? sq.GetString() ?? "" : "";
            var rawCmId = obj.TryGetProperty("CmId", out var ci) ? ci.GetString() ?? "" : "";
            var commentId = !string.IsNullOrEmpty(rawCmId) ? rawCmId : seqNo;
            if (string.IsNullOrEmpty(commentId)) return null;

            var nick = obj.TryGetProperty("Nick", out var nk) ? nk.GetString() ?? "" : "";
            if (nick.StartsWith('@')) nick = nick[1..];
            if (string.IsNullOrWhiteSpace(nick)) nick = "匿名用户";

            var avatarUrl = obj.TryGetProperty("Avatar", out var av) ? av.GetString() ?? "" : "";
            var rawContent = obj.TryGetProperty("Content", out var ct) ? ct.GetString() ?? "" : "";
            var content = DecodeHtmlEntities(rawContent);
            if (string.IsNullOrWhiteSpace(content)) return null;

            long timeSec = 0;
            if (obj.TryGetProperty("PubTime", out var pt) && pt.ValueKind == JsonValueKind.Number)
            {
                timeSec = pt.GetInt64();
            }

            int praiseNum = 0;
            if (obj.TryGetProperty("PraiseNum", out var pn) && pn.ValueKind == JsonValueKind.Number)
            {
                praiseNum = pn.GetInt32();
            }

            // 无图终端彻底屏蔽图片 URL，避免任何图片请求可能
            var picUrl = TerminalImageHelper.IsImageSupported && obj.TryGetProperty("Pic", out var p) ? p.GetString() ?? "" : "";
            var picSize = obj.TryGetProperty("PicSize", out var ps) ? ps.GetString() ?? "" : "";
            var location = obj.TryGetProperty("Location", out var loc) ? loc.GetString() ?? "" : "";

            return new SongComment(
                CommentId: commentId,
                Nick: nick,
                AvatarUrl: avatarUrl,
                Content: content,
                TimeSec: timeSec,
                PraiseNum: praiseNum,
                IsHot: isHot,
                PicUrl: picUrl,
                PicSize: picSize,
                Location: location,
                SeqNo: seqNo
            );
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CommentPage?> FetchLegacyCommentsAsync(
        long songId,
        int page,
        int size,
        CancellationToken ct)
    {
        var url = $"https://c.y.qq.com/base/fcgi-bin/fcg_global_comment_h5.fcg?biztype=1&topid={songId}&cmd=8&pagenum={page}&pagesize={size}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var cp) || cp.GetInt32() != 0) return null;

            int totalCount = 0;
            if (root.TryGetProperty("comment", out var co) && co.TryGetProperty("commenttotal", out var ctProp) && ctProp.ValueKind == JsonValueKind.Number)
            {
                totalCount = ctProp.GetInt32();
            }
            else if (root.TryGetProperty("commenttotal", out var rct) && rct.ValueKind == JsonValueKind.Number)
            {
                totalCount = rct.GetInt32();
            }

            int moreComment = root.TryGetProperty("morecomment", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0;

            var hotList = new List<SongComment>();
            if (root.TryGetProperty("hot_comment", out var hc) && hc.TryGetProperty("commentlist", out var hArr) && hArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in hArr.EnumerateArray())
                {
                    var c = ParseLegacyCommentElement(elem, isHot: true);
                    if (c != null) hotList.Add(c);
                }
            }

            var normalList = new List<SongComment>();
            if (root.TryGetProperty("comment", out var cm) && cm.TryGetProperty("commentlist", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in cArr.EnumerateArray())
                {
                    var c = ParseLegacyCommentElement(elem, isHot: false);
                    if (c != null) normalList.Add(c);
                }
            }

            bool hasMore = moreComment == 1 && normalList.Count >= size;
            return new CommentPage(totalCount, hotList, normalList, hasMore);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"FetchLegacyCommentsAsync failed for songId={songId}", ex);
            return null;
        }
    }

    private static SongComment? ParseLegacyCommentElement(JsonElement obj, bool isHot)
    {
        try
        {
            var commentId = obj.TryGetProperty("commentid", out var ci) ? ci.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(commentId) && obj.TryGetProperty("rootcommentid", out var rci))
            {
                commentId = rci.GetString() ?? "";
            }
            if (string.IsNullOrEmpty(commentId)) return null;

            var nick = obj.TryGetProperty("nick", out var nk) ? nk.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(nick) && obj.TryGetProperty("rootcommentnick", out var rnk))
            {
                nick = rnk.GetString() ?? "";
            }
            if (nick.StartsWith('@')) nick = nick[1..];
            if (string.IsNullOrWhiteSpace(nick)) nick = "匿名用户";

            var avatarUrl = obj.TryGetProperty("avatarurl", out var av) ? av.GetString() ?? "" : "";
            var content = obj.TryGetProperty("rootcommentcontent", out var rc) ? rc.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(content) && obj.TryGetProperty("commentcontent", out var cc))
            {
                content = cc.GetString() ?? "";
            }
            if (string.IsNullOrEmpty(content) && obj.TryGetProperty("content", out var c))
            {
                content = c.GetString() ?? "";
            }
            content = DecodeHtmlEntities(content);
            if (string.IsNullOrWhiteSpace(content)) return null;

            long timeSec = obj.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            int praiseNum = obj.TryGetProperty("praisenum", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 0;

            return new SongComment(
                CommentId: commentId,
                Nick: nick,
                AvatarUrl: avatarUrl,
                Content: content,
                TimeSec: timeSec,
                PraiseNum: praiseNum,
                IsHot: isHot
            );
        }
        catch
        {
            return null;
        }
    }

    public static string DecodeHtmlEntities(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text
            .Replace("&nbsp;", " ")
            .Replace("&#13;", "\n")
            .Replace("&#10;", "\n")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Trim();
    }
}
