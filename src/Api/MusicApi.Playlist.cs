using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    /// <summary>
    /// 检索歌曲曲库。
    /// </summary>
    /// <param name="query">搜索关键词。</param>
    /// <param name="page">分页页码，默认 1。</param>
    /// <param name="pageSize">单页条数，默认 25。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>匹配的曲目列表；若未检索到结果或请求失败返回空列表。</returns>
    public static async Task<List<Song>> SearchAsync(string query, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var escapedQuery = JsonEncodedText.Encode(query).ToString();
            var payload = $$"""
            {
              "music.search.SearchCgiService": {
                "module": "music.search.SearchCgiService",
                "method": "DoSearchForQQMusicDesktop",
                "param": {
                  "query": "{{escapedQuery}}",
                  "page_num": {{page}},
                  "num_per_page": {{pageSize}},
                  "search_type": 0
                }
              }
            }
            """;

            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("music.search.SearchCgiService", out var svc) &&
                svc.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("song", out var songObj) &&
                songObj.TryGetProperty("list", out var songList) &&
                songList.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Song>(songList.GetArrayLength());
                foreach (var item in songList.EnumerateArray())
                {
                    var song = ParseSongFromElement(item);
                    if (song != null)
                    {
                        list.Add(song);
                    }
                }
                return list;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"SearchAsync error for '{query}'", ex);
            return [];
        }
    }

    /// <summary>
    /// 检索歌单列表。
    /// </summary>
    /// <param name="query">搜索关键词。</param>
    /// <param name="page">分页页码，默认 1。</param>
    /// <param name="pageSize">单页条数，默认 30。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>匹配的歌单列表；若未检索到结果或请求失败返回空列表。</returns>
    public static async Task<List<Playlist>> SearchPlaylistsAsync(string query, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var escapedQuery = JsonEncodedText.Encode(query).ToString();
            var payload = $$"""
            {
              "music.search.SearchCgiService": {
                "module": "music.search.SearchCgiService",
                "method": "DoSearchForQQMusicDesktop",
                "param": {
                  "query": "{{escapedQuery}}",
                  "page_num": {{page}},
                  "num_per_page": {{pageSize}},
                  "search_type": 3
                }
              }
            }
            """;

            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("music.search.SearchCgiService", out var svc) &&
                svc.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("songlist", out var songlistObj) &&
                songlistObj.TryGetProperty("list", out var songlistArray) &&
                songlistArray.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Playlist>(songlistArray.GetArrayLength());
                foreach (var item in songlistArray.EnumerateArray())
                {
                    long dirId = 0;
                    if (item.TryGetProperty("dissid", out var dip))
                    {
                        if (dip.ValueKind == JsonValueKind.Number) dirId = dip.GetInt64();
                        else if (dip.ValueKind == JsonValueKind.String && long.TryParse(dip.GetString(), out var parsedId)) dirId = parsedId;
                    }
                    if (dirId <= 0) continue;

                    string name = item.TryGetProperty("dissname", out var dnp) ? dnp.GetString() ?? "" : "";
                    string pic = item.TryGetProperty("imgurl", out var imp) ? imp.GetString() ?? "" : "";
                    int songCount = item.TryGetProperty("song_count", out var scp) && scp.TryGetInt32(out var sn) ? sn : 0;

                    list.Add(new Playlist(
                        DirId: dirId,
                        Name: string.IsNullOrWhiteSpace(name) ? "未命名歌单" : name,
                        SongCount: songCount,
                        Tid: dirId,
                        IsFav: true,
                        PicUrl: pic
                    ));
                }
                return list;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"SearchPlaylistsAsync error for '{query}'", ex);
            return [];
        }
    }

    /// <summary>
    /// 收藏歌曲列表查询结果。
    /// </summary>
    /// <param name="Songs">当前页返回的歌曲列表。</param>
    /// <param name="Total">收藏歌曲总数。</param>
    /// <param name="HasMore">是否还有更多未拉取的分页数据。</param>
    public sealed record FavoriteSongsResult(List<Song> Songs, int Total, bool HasMore);

    /// <summary>
    /// 分页获取当前登录用户的收藏歌曲列表。
    /// </summary>
    /// <param name="page">分页页码，默认 1。</param>
    /// <param name="pageSize">单页数据量，默认 100。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>收藏歌曲查询结果；若未登录或请求失败返回空结果集。</returns>
    public static async Task<FavoriteSongsResult> GetFavoriteSongsAsync(int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return new FavoriteSongsResult([], 0, false);

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"req_fav\":{{\"module\":\"music.musicasset.PlaylistDetailRead\",\"method\":\"GetUniformSongDetailInfo\"," +
            $"\"param\":{{\"uin\":\"{uin}\",\"dirid\":201,\"bPaged\":true,\"offset\":{(page - 1) * pageSize},\"size\":{pageSize}}}}}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var list = new List<Song>(pageSize > 0 ? pageSize : 30);
            int total = 0;
            bool hasMore = false;

            if (root.TryGetProperty("req_fav", out var favObj) &&
                favObj.TryGetProperty("data", out var dataObj))
            {
                if (dataObj.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                {
                    total = totalProp.GetInt32();
                }
                else if (dataObj.TryGetProperty("total_song_num", out var tsnProp) && tsnProp.ValueKind == JsonValueKind.Number)
                {
                    total = tsnProp.GetInt32();
                }

                if (dataObj.TryGetProperty("hasmore", out var hmProp))
                {
                    if (hmProp.ValueKind == JsonValueKind.Number) hasMore = hmProp.GetInt32() == 1;
                    else if (hmProp.ValueKind == JsonValueKind.True) hasMore = true;
                    else if (hmProp.ValueKind == JsonValueKind.False) hasMore = false;
                }

                int rawCount = 0;
                if (dataObj.TryGetProperty("list", out var songArray) && songArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in songArray.EnumerateArray())
                    {
                        rawCount++;
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                }

                // 容错判定：若服务端未直接返回 hasmore=true，但只要未达到 total 或原始批次等于 pageSize，均继续保持分页可拉取
                if (!hasMore && total > 0 && ((page - 1) * pageSize + rawCount < total))
                {
                    hasMore = true;
                }
                else if (!hasMore && total == 0 && rawCount >= pageSize)
                {
                    hasMore = true;
                }
            }

            return new FavoriteSongsResult(list, total, hasMore);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", "GetFavoriteSongsAsync error", ex);
            return new FavoriteSongsResult([], 0, false);
        }
    }

    /// <summary>
    /// 获取当前用户自建与收藏的所有歌单列表。
    /// </summary>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>歌单列表；若未登录或请求失败返回空列表。</returns>
    public static async Task<List<Playlist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn) return [];

        await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

        var uin = UserSession.Current.Uin;
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";

        var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
            $"\"self_playlists\":{{\"module\":\"music.musicasset.PlaylistBaseRead\",\"method\":\"GetPlaylistByUin\",\"param\":{{\"uin\":\"{uin}\"}}}}," +
            $"\"fav_playlists\":{{\"module\":\"music.musicasset.PlaylistFavRead\",\"method\":\"GetPlaylistFavInfo\",\"param\":{{\"uin\":\"{uin}\"}}}}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var playlists = new List<Playlist>(32);

            if (root.TryGetProperty("self_playlists", out var selfObj) &&
                selfObj.TryGetProperty("data", out var selfData) &&
                selfData.TryGetProperty("v_playlist", out var selfArr) &&
                selfArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in selfArr.EnumerateArray())
                {
                    long dirId = item.TryGetProperty("dirId", out var d) ? d.GetInt64() : 0;
                    string name = item.TryGetProperty("dirName", out var n) ? n.GetString() ?? "" : "";
                    int count = item.TryGetProperty("songNum", out var c) ? c.GetInt32() : 0;
                    long tid = item.TryGetProperty("tid", out var t) ? t.GetInt64() : 0;
                    string pic = item.TryGetProperty("picUrl", out var p) ? p.GetString() ?? "" : "";

                    if (dirId > 0 && !string.IsNullOrEmpty(name))
                    {
                        playlists.Add(new Playlist(dirId, name, count, tid, IsFav: false, pic));
                    }
                }
            }

            if (root.TryGetProperty("fav_playlists", out var favObj) &&
                favObj.TryGetProperty("data", out var favData) &&
                favData.TryGetProperty("v_list", out var favArr) &&
                favArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in favArr.EnumerateArray())
                {
                    long tid = item.TryGetProperty("tid", out var t) ? t.GetInt64() : 0;
                    long dirId = item.TryGetProperty("dirId", out var d) ? d.GetInt64() : 0;
                    string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    int count = item.TryGetProperty("songnum", out var c) ? c.GetInt32() : 0;
                    string logo = item.TryGetProperty("logo", out var l) ? l.GetString() ?? "" : "";

                    if (tid > 0 && !string.IsNullOrEmpty(name))
                    {
                        playlists.Add(new Playlist(dirId, name, count, tid, IsFav: true, logo));
                    }
                }
            }

            return playlists;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", "GetPlaylistsAsync error", ex);
            return [];
        }
    }

    /// <summary>
    /// 获取指定歌单内的所有歌曲（自建歌单与外部收藏歌单均支持）。
    /// </summary>
    /// <param name="playlist">目标歌单实例。</param>
    /// <param name="page">分页页码，默认 1。</param>
    /// <param name="pageSize">单页条数，默认 100。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>解析后的曲目列表；若未登录或请求失败返回空列表。</returns>
    public static async Task<List<Song>> GetPlaylistSongsAsync(Playlist playlist, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        if (!playlist.IsFav)
        {
            if (!UserSession.Current.IsLoggedIn) return [];
            await LoginService.EnsureMusicKeyAsync(ct).ConfigureAwait(false);

            var uin = UserSession.Current.Uin;
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"req_pl\":{{\"module\":\"music.musicasset.PlaylistDetailRead\",\"method\":\"GetUniformSongDetailInfo\"," +
                $"\"param\":{{\"uin\":\"{uin}\",\"dirid\":{playlist.DirId},\"bPaged\":true,\"offset\":{(page - 1) * pageSize},\"size\":{pageSize}}}}}}}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("req_pl", out var plObj) &&
                    plObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("list", out var songArray) &&
                    songArray.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<Song>(songArray.GetArrayLength());
                    foreach (var item in songArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                    return list;
                }
                return [];
            }
            catch (Exception ex)
            {
                AppLogger.Error("MusicApi", $"GetPlaylistSongsAsync (dirId: {playlist.DirId}) error", ex);
                return [];
            }
        }
        else
        {
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var uin = UserSession.Current.IsLoggedIn ? UserSession.Current.Uin : "0";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"\"}}," +
                $"\"req_diss\":{{\"module\":\"music.srfDissInfo.aiDissInfo\",\"method\":\"uniform_get_Dissinfo\"," +
                $"\"param\":{{\"disstid\":{playlist.Tid},\"userinfo\":1,\"tag\":1}}}}}}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                var cookieHeader = UserSession.Current.GetCookieHeader();
                if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.Add("Cookie", cookieHeader);

                using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("req_diss", out var dissObj) &&
                    dissObj.TryGetProperty("data", out var dataObj) &&
                    dataObj.TryGetProperty("songlist", out var songArray) &&
                    songArray.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<Song>(songArray.GetArrayLength());
                    foreach (var item in songArray.EnumerateArray())
                    {
                        var song = ParseSongFromElement(item);
                        if (song != null) list.Add(song);
                    }
                    return list;
                }
                return [];
            }
            catch (Exception ex)
            {
                AppLogger.Error("MusicApi", $"GetPlaylistSongsAsync (tid: {playlist.Tid}) error", ex);
                return [];
            }
        }
    }

    /// <summary>
    /// 添加歌曲到指定歌单（dirId: 201 即为“我喜欢”）。
    /// </summary>
    /// <param name="dirId">目标歌单目录标识。</param>
    /// <param name="songId">曲目数值标识。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>操作成功返回 <see langword="true"/>；失败返回 <see langword="false"/>。</returns>
    public static async Task<bool> AddSongToPlaylistAsync(long dirId, long songId, CancellationToken ct = default)
    {
        return await AddSongToPlaylistInternalAsync(dirId, songId, canRetryWithRenew: true, ct).ConfigureAwait(false);
    }

    private static string BuildAppCommJson()
    {
        var escapedUin = JsonEncodedText.Encode(UserSession.Current.Uin).ToString();
        var escapedKey = JsonEncodedText.Encode(UserSession.Current.MusicKey).ToString();
        var loginType = GetCurrentLoginType();
        return $"{{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":{loginType},\"qq\":\"{escapedUin}\",\"authst\":\"{escapedKey}\"}}";
    }

    private static async Task<string> PostAppMusicuAsync(string jsonPayload, string? customCookieHeader = null, CancellationToken ct = default)
    {
        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");

        var cookieHeader = customCookieHeader ?? UserSession.Current.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GBK").GetString(bytes);
        }
    }

    private static async Task<bool> AddSongToPlaylistInternalAsync(long dirId, long songId, bool canRetryWithRenew, CancellationToken ct)
    {
        if (!UserSession.Current.IsLoggedIn || songId <= 0) return false;

        await LoginService.EnsureMusicKeyAsync(false, ct).ConfigureAwait(false);

        var comm = BuildAppCommJson();
        var payload = $"{{\"comm\":{comm}," +
            $"\"addSongsToPlayList\":{{\"module\":\"music.musicasset.PlaylistDetailWrite\",\"method\":\"AddSonglist\"," +
            $"\"param\":{{\"dirId\":{dirId},\"v_songInfo\":[{{\"songId\":{songId},\"songType\":0}}]}}}}}}";

        try
        {
            AppLogger.Info("MusicApi", $"AddSongToPlaylistAsync (App-CGI) requesting: dirId={dirId}, songId={songId}");
            var json = await PostAppMusicuAsync(payload, customCookieHeader: "", ct).ConfigureAwait(false);
            AppLogger.Info("MusicApi", $"AddSongToPlaylistAsync (App-CGI) response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("addSongsToPlayList", out var addObj))
            {
                int code = -1;
                if (addObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    code = codeProp.GetInt32();
                }
                else if (addObj.TryGetProperty("subcode", out var subProp) && subProp.ValueKind == JsonValueKind.Number)
                {
                    code = subProp.GetInt32();
                }

                if (code == 0)
                {
                    if (addObj.TryGetProperty("data", out var dataObj))
                    {
                        if (dataObj.TryGetProperty("succ_song_num", out var succProp) && succProp.GetInt32() == 0 &&
                            dataObj.TryGetProperty("fail_song_num", out var failProp) && failProp.GetInt32() > 0)
                        {
                            AppLogger.Warn("MusicApi", $"AddSongToPlaylistAsync: succ_song_num is 0 and fail_song_num is {failProp.GetInt32()}");
                            return false;
                        }
                    }
                    return true;
                }

                if ((code == 1000 || code == 10000 || code == 80105) && canRetryWithRenew)
                {
                    AppLogger.Info("MusicApi", $"AddSongToPlaylistAsync returned auth/rate error {code}, attempting credential renewal...");
                    var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    if (renewed)
                    {
                        return await AddSongToPlaylistInternalAsync(dirId, songId, canRetryWithRenew: false, ct).ConfigureAwait(false);
                    }
                }

                if (code == 2001 && canRetryWithRenew)
                {
                    AppLogger.Info("MusicApi", $"AddSongToPlaylistAsync returned code 2001 (index sync delay), retrying in 800ms...");
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    return await AddSongToPlaylistInternalAsync(dirId, songId, canRetryWithRenew: false, ct).ConfigureAwait(false);
                }

                AppLogger.Warn("MusicApi", $"AddSongToPlaylistAsync (App-CGI) returned non-zero code: {code}");
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"AddSongToPlaylistAsync (App-CGI) exception for dirId={dirId}, songId={songId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 从指定歌单中移除歌曲（dirId: 201 即为“我喜欢”）。
    /// </summary>
    /// <param name="dirId">目标歌单目录标识。</param>
    /// <param name="songId">曲目数值标识。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>操作成功返回 <see langword="true"/>；失败返回 <see langword="false"/>。</returns>
    public static async Task<bool> RemoveSongFromPlaylistAsync(long dirId, long songId, CancellationToken ct = default)
    {
        return await RemoveSongFromPlaylistInternalAsync(dirId, songId, canRetryWithRenew: true, ct).ConfigureAwait(false);
    }

    private static async Task<bool> RemoveSongFromPlaylistInternalAsync(long dirId, long songId, bool canRetryWithRenew, CancellationToken ct)
    {
        if (!UserSession.Current.IsLoggedIn || songId <= 0) return false;

        await LoginService.EnsureMusicKeyAsync(false, ct).ConfigureAwait(false);

        var comm = BuildAppCommJson();
        var payload = $"{{\"comm\":{comm}," +
            $"\"delSongsFromPlayList\":{{\"module\":\"music.musicasset.PlaylistDetailWrite\",\"method\":\"DelSonglist\"," +
            $"\"param\":{{\"dirId\":{dirId},\"v_songInfo\":[{{\"songId\":{songId},\"songType\":0}}]}}}}}}";

        try
        {
            AppLogger.Info("MusicApi", $"RemoveSongFromPlaylistAsync (App-CGI) requesting: dirId={dirId}, songId={songId}");
            var json = await PostAppMusicuAsync(payload, customCookieHeader: "", ct).ConfigureAwait(false);
            AppLogger.Info("MusicApi", $"RemoveSongFromPlaylistAsync (App-CGI) response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("delSongsFromPlayList", out var delObj))
            {
                int code = -1;
                if (delObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    code = codeProp.GetInt32();
                }
                else if (delObj.TryGetProperty("subcode", out var subProp) && subProp.ValueKind == JsonValueKind.Number)
                {
                    code = subProp.GetInt32();
                }

                if (code == 0)
                {
                    return true;
                }

                if ((code == 1000 || code == 10000 || code == 80105) && canRetryWithRenew)
                {
                    AppLogger.Info("MusicApi", $"RemoveSongFromPlaylistAsync returned auth/rate error {code}, attempting credential renewal...");
                    var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    if (renewed)
                    {
                        return await RemoveSongFromPlaylistInternalAsync(dirId, songId, canRetryWithRenew: false, ct).ConfigureAwait(false);
                    }
                }

                if (code == 2001 && canRetryWithRenew)
                {
                    AppLogger.Info("MusicApi", $"RemoveSongFromPlaylistAsync returned code 2001 (index sync delay), retrying in 800ms...");
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    return await RemoveSongFromPlaylistInternalAsync(dirId, songId, canRetryWithRenew: false, ct).ConfigureAwait(false);
                }

                AppLogger.Warn("MusicApi", $"RemoveSongFromPlaylistAsync (App-CGI) returned non-zero code: {code}");
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"RemoveSongFromPlaylistAsync (App-CGI) exception for dirId={dirId}, songId={songId}", ex);
            return false;
        }
    }

    /// <summary>
    /// 添加歌曲到指定歌单。
    /// </summary>
    /// <param name="playlist">目标歌单模型。</param>
    /// <param name="song">待添加的歌曲模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>操作成功返回 <see langword="true"/>；解析失败或接口报错返回 <see langword="false"/>。</returns>
    public static async Task<bool> AddSongToPlaylistAsync(Playlist playlist, Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0) return false;

        return await AddSongToPlaylistAsync(playlist.DirId, songId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从指定歌单中移除歌曲。
    /// </summary>
    /// <param name="playlist">目标歌单模型。</param>
    /// <param name="song">待移除的歌曲模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>操作成功返回 <see langword="true"/>；解析失败或接口报错返回 <see langword="false"/>。</returns>
    public static async Task<bool> RemoveSongFromPlaylistAsync(Playlist playlist, Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0) return false;

        return await RemoveSongFromPlaylistAsync(playlist.DirId, songId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 添加歌曲到当前用户的“我喜欢”收藏列表。
    /// </summary>
    /// <param name="song">待收藏的歌曲模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>收藏成功返回 <see langword="true"/>；失败返回 <see langword="false"/>。</returns>
    public static async Task<bool> AddSongToFavoriteAsync(Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0)
        {
            AppLogger.Warn("MusicApi", $"AddSongToFavoriteAsync: Unable to resolve songId for mid={song.Mid}, title={song.Title}");
            return false;
        }

        var ok = await AddSongToPlaylistAsync(201, songId, ct).ConfigureAwait(false);
        AppLogger.Info("MusicApi", $"AddSongToFavoriteAsync: mid={song.Mid}, songId={songId}, title={song.Title}, result={ok}");
        return ok;
    }

    /// <summary>
    /// 从当前用户的“我喜欢”收藏列表中移除歌曲。
    /// </summary>
    /// <param name="song">待取消收藏的歌曲模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>取消成功返回 <see langword="true"/>；失败返回 <see langword="false"/>。</returns>
    public static async Task<bool> RemoveSongFromFavoriteAsync(Song song, CancellationToken ct = default)
    {
        long songId = song.Id;
        if (songId <= 0 && !string.IsNullOrEmpty(song.Mid))
        {
            songId = await ResolveSongIdAsync(song.Mid, ct).ConfigureAwait(false);
            if (songId > 0) song.Id = songId;
        }
        if (songId <= 0)
        {
            AppLogger.Warn("MusicApi", $"RemoveSongFromFavoriteAsync: Unable to resolve songId for mid={song.Mid}, title={song.Title}");
            return false;
        }

        var ok = await RemoveSongFromPlaylistAsync(201, songId, ct).ConfigureAwait(false);
        AppLogger.Info("MusicApi", $"RemoveSongFromFavoriteAsync: mid={song.Mid}, songId={songId}, title={song.Title}, result={ok}");
        return ok;
    }



    /// <summary>
    /// 创建自建歌单。
    /// </summary>
    /// <param name="name">歌单名称。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>三元组：Success 表示是否成功，DissId 为新创建歌单标识，Message 为响应说明。</returns>
    public static Task<(bool Success, long DissId, string Message)> CreatePlaylistAsync(string name, CancellationToken ct = default) =>
        CreatePlaylistInternalAsync(name, canRetryWithRenew: true, ct);

    private static async Task<(bool Success, long DissId, string Message)> CreatePlaylistInternalAsync(string name, bool canRetryWithRenew, CancellationToken ct)
    {
        if (!UserSession.Current.IsLoggedIn || string.IsNullOrWhiteSpace(name))
        {
            return (false, 0, "用户未登录或歌单名称为空");
        }

        await LoginService.EnsureMusicKeyAsync(false, ct).ConfigureAwait(false);

        var escapedName = JsonEncodedText.Encode(name.Trim()).ToString();
        var comm = BuildAppCommJson();
        var payload = $$"""
        {
          "comm": {{comm}},
          "createNewPlayList": {
            "module": "music.musicasset.PlaylistBaseWrite",
            "method": "AddPlaylist",
            "param": {
              "dirName": "{{escapedName}}",
              "dirShow": 1,
              "dirDesc": "",
              "dirPicUrl": "",
              "taglist": ""
            }
          }
        }
        """;

        try
        {
            AppLogger.Info("MusicApi", $"CreatePlaylistAsync (App-CGI) requesting: name={name}");
            var json = await PostAppMusicuAsync(payload, customCookieHeader: "", ct).ConfigureAwait(false);
            AppLogger.Info("MusicApi", $"CreatePlaylistAsync (App-CGI) response: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("createNewPlayList", out var addObj))
            {
                int code = -1;
                if (addObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    code = codeProp.GetInt32();
                }

                if (code == 0)
                {
                    long dissId = 0;
                    if (addObj.TryGetProperty("data", out var dataObj) &&
                        dataObj.TryGetProperty("result", out var resObj))
                    {
                        if (resObj.TryGetProperty("dirId", out var dProp) && dProp.ValueKind == JsonValueKind.Number)
                        {
                            dissId = dProp.GetInt64();
                        }
                        else if (resObj.TryGetProperty("tid", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
                        {
                            dissId = tProp.GetInt64();
                        }
                    }
                    return (true, dissId, "创建成功");
                }

                if ((code == 1000 || code == 10000 || code == 80105) && canRetryWithRenew)
                {
                    AppLogger.Info("MusicApi", $"CreatePlaylistAsync returned auth/rate error {code}, attempting credential renewal...");
                    var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    if (renewed)
                    {
                        return await CreatePlaylistInternalAsync(name, canRetryWithRenew: false, ct).ConfigureAwait(false);
                    }
                }

                string msg = "";
                if (addObj.TryGetProperty("data", out var dObj) && dObj.TryGetProperty("msg", out var mProp))
                {
                    msg = mProp.GetString() ?? "";
                }
                return (false, 0, string.IsNullOrEmpty(msg) ? $"创建失败 (code={code})" : msg);
            }

            return (false, 0, "创建歌单无响应");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"CreatePlaylistAsync (App-CGI) exception for name={name}", ex);
            return (false, 0, ex.Message);
        }
    }

    /// <summary>
    /// 删除指定歌单（自建歌单执行物理删除，外部歌单执行取消收藏）。
    /// </summary>
    /// <param name="playlist">待删除的歌单模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>删除或取消收藏成功返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static Task<bool> DeletePlaylistAsync(Playlist playlist, CancellationToken ct = default) =>
        DeletePlaylistInternalAsync(playlist, canRetryWithRenew: true, ct);

    private static async Task<bool> DeletePlaylistInternalAsync(Playlist playlist, bool canRetryWithRenew, CancellationToken ct)
    {
        if (!UserSession.Current.IsLoggedIn || playlist.IsMyFavorite)
        {
            return false;
        }

        await LoginService.EnsureMusicKeyAsync(false, ct).ConfigureAwait(false);

        try
        {
            var comm = BuildAppCommJson();
            if (!playlist.IsFav)
            {
                // 自建歌单删除 (music.musicasset.PlaylistBaseWrite/DelPlaylist)
                long dirId = playlist.DirId;
                var payload = $$"""
                {
                  "comm": {{comm}},
                  "deletePlayList": {
                    "module": "music.musicasset.PlaylistBaseWrite",
                    "method": "DelPlaylist",
                    "param": {
                      "dirId": {{dirId}}
                    }
                  }
                }
                """;

                AppLogger.Info("MusicApi", $"DeletePlaylistAsync (App-CGI/created) requesting: dirId={dirId}");
                var json = await PostAppMusicuAsync(payload, customCookieHeader: "", ct).ConfigureAwait(false);
                AppLogger.Info("MusicApi", $"DeletePlaylistAsync (App-CGI/created) response: {json}");

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("deletePlayList", out var delObj) &&
                    delObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    int code = codeProp.GetInt32();
                    if (code == 0) return true;
                    if ((code == 1000 || code == 10000 || code == 80105) && canRetryWithRenew)
                    {
                        var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                        if (renewed)
                        {
                            return await DeletePlaylistInternalAsync(playlist, canRetryWithRenew: false, ct).ConfigureAwait(false);
                        }
                    }
                    return false;
                }
                return false;
            }
            else
            {
                // 收藏外部歌单：取消收藏 (music.musicasset.PlaylistFavWrite/CancelFavPlaylist)
                long dissId = playlist.Tid > 0 ? playlist.Tid : playlist.DirId;
                var payload = $$"""
                {
                  "comm": {{comm}},
                  "deleteFavPlayList": {
                    "module": "music.musicasset.PlaylistFavWrite",
                    "method": "CancelFavPlaylist",
                    "param": {
                      "dirId": {{dissId}}
                    }
                  }
                }
                """;

                AppLogger.Info("MusicApi", $"DeletePlaylistAsync (App-CGI/fav) requesting: dissId={dissId}");
                var json = await PostAppMusicuAsync(payload, customCookieHeader: "", ct).ConfigureAwait(false);
                AppLogger.Info("MusicApi", $"DeletePlaylistAsync (App-CGI/fav) response: {json}");

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("deleteFavPlayList", out var favObj) &&
                    favObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number)
                {
                    int code = codeProp.GetInt32();
                    if (code == 0) return true;
                    if ((code == 1000 || code == 10000 || code == 80105) && canRetryWithRenew)
                    {
                        var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                        if (renewed)
                        {
                            return await DeletePlaylistInternalAsync(playlist, canRetryWithRenew: false, ct).ConfigureAwait(false);
                        }
                    }
                    return false;
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"DeletePlaylistAsync (App-CGI) exception for playlist {playlist.Title}", ex);
            return false;
        }
    }
}
