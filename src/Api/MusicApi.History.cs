using System.Text;
using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

/// <summary>
/// 最近播放数据上报的资产类型码，对应 QQ 音乐 RPC 底层类型
/// </summary>
internal enum RecentHistoryType
{
    Song = 2,
    Album = 3,
    Playlist = 4,
}

public sealed partial class MusicApi
{
    // ── RPC Payload 构建 ────────────────────────────────────────────────────

    private static string BuildGetRecentHistoryPayload(int type, long updateTime)
    {
        var uin = UserSession.Current.Uin;
        var authst = UserSession.Current.MusicKey;
        int loginType = authst.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;

        return $$"""
            {
              "comm": {
                "ct": 11,
                "cv": 14090008,
                "v": 14090008,
                "tmeAppID": "qqmusic",
                "tmeLoginType": {{loginType}},
                "format": "json",
                "qq": "{{uin}}",
                "authst": "{{authst}}"
              },
              "req_recent": {
                "module": "music.musicasset.PlayRecentlyRead",
                "method": "GetPlayRecentlyInfo",
                "param": {
                  "type": {{type}},
                  "updateTime": {{updateTime}}
                }
              }
            }
            """;
    }

    private static string BuildReportRecentHistoryPayload(string id, int type, long lastTime, int listenCnt)
    {
        var uin = UserSession.Current.Uin;
        var authst = UserSession.Current.MusicKey;
        int loginType = authst.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;

        return $$"""
            {
              "comm": {
                "ct": 11,
                "cv": 14090008,
                "v": 14090008,
                "tmeAppID": "qqmusic",
                "tmeLoginType": {{loginType}},
                "format": "json",
                "qq": "{{uin}}",
                "authst": "{{authst}}"
              },
              "report_recent": {
                "module": "music.musicasset.PlayRecentlyWrite",
                "method": "ReportPlayRecentlyInfo",
                "param": {
                  "data": [
                    {
                      "id": "{{id}}",
                      "type": {{type}},
                      "lastTime": {{lastTime}},
                      "listenCnt": {{listenCnt}},
                      "auxillaryID": "",
                      "auxillaryDict": {}
                    }
                  ]
                }
              }
            }
            """;
    }

    // ── 网关辅助 ─────────────────────────────────────────────────────────────

    // musicu.fcg 专用客户端，超时 20s，对应 Kotlin OkHttp 默认行为
    private static readonly System.Net.Http.HttpClient s_musicuClient = new(new System.Net.Http.SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>
    /// 向 musicu.fcg 发送明文 JSON（对应 Kotlin postGateway，用于读类接口）
    /// </summary>
    private static async Task<string> PostMusicuAsync(string jsonPayload, CancellationToken ct = default)
    {
        var sign = ComputeZzcSign(jsonPayload);
        var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?_={sign}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        req.Headers.Referrer = new Uri("https://y.qq.com/");
        req.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var cookieHeader = UserSession.Current.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            req.Headers.Add("Cookie", cookieHeader);
        }

        using var resp = await s_musicuClient.SendAsync(req, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // ── 响应解析辅助 ─────────────────────────────────────────────────────────

    private static (JsonElement outer, JsonElement inner) ExtractRecentDataContainer(JsonElement root)
    {
        var reqRecent = root.TryGetProperty("req_recent", out var rr) ? rr : root;
        var outer = reqRecent.TryGetProperty("data", out var od) ? od : default;
        var inner = outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("data", out var id)
            ? id
            : outer;
        return (outer, inner);
    }

    private static long GetUpdateTime(JsonElement outer, JsonElement inner)
    {
        if (inner.ValueKind == JsonValueKind.Object &&
            inner.TryGetProperty("updateTime", out var ut) &&
            ut.ValueKind == JsonValueKind.Number)
        {
            return ut.GetInt64();
        }
        if (outer.ValueKind == JsonValueKind.Object &&
            outer.TryGetProperty("updateTime", out var ut2) &&
            ut2.ValueKind == JsonValueKind.Number)
        {
            return ut2.GetInt64();
        }
        return 0L;
    }

    // ── 公开 API ────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取云端最近播放单曲列表（增量拉取，updateTime=0 表示全量）
    /// </summary>
    public static async Task<(List<Song> Songs, long UpdateTime)> GetRecentSongsAsync(
        long updateTime = 0L,
        CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            return ([], 0L);
        }

        try
        {
            var payload = BuildGetRecentHistoryPayload((int)RecentHistoryType.Song, updateTime);
            var json = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var (outer, inner) = ExtractRecentDataContainer(doc.RootElement);
            long newUpdateTime = GetUpdateTime(outer, inner);

            if (outer.ValueKind != JsonValueKind.Object)
            {
                return ([], 0L);
            }

            JsonElement songListElem = default;
            if (inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty("songList", out var sl))
            {
                songListElem = sl;
            }
            else if (outer.TryGetProperty("songList", out var sl2))
            {
                songListElem = sl2;
            }

            if (songListElem.ValueKind != JsonValueKind.Array)
            {
                return ([], newUpdateTime);
            }

            var songs = new List<Song>();
            foreach (var itemElem in songListElem.EnumerateArray())
            {
                var trackElem = itemElem.TryGetProperty("track", out var tr) ? tr
                    : itemElem.TryGetProperty("song_info", out var si) ? si
                    : itemElem;
                var song = ParseSongFromElement(trackElem);
                if (song != null)
                {
                    songs.Add(song);
                }
            }
            return (songs, newUpdateTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn("MusicApi.History", $"GetRecentSongsAsync failed: {ex.Message}");
            return ([], 0L);
        }
    }

    /// <summary>
    /// 上报单曲播放记录到云端（优先使用数字 ID，对齐 Kotlin 端规范）
    /// </summary>
    public static async Task ReportRecentSongAsync(Song song, CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            return;
        }

        string reportId = song.Id > 0 ? song.Id.ToString() : song.Mid;
        if (string.IsNullOrEmpty(reportId))
        {
            return;
        }

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var payload = BuildReportRecentHistoryPayload(reportId, (int)RecentHistoryType.Song, nowSec, 1);
            var resp = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);
            int code = -1;
            if (doc.RootElement.TryGetProperty("report_recent", out var rr) &&
                rr.TryGetProperty("code", out var c) &&
                c.ValueKind == JsonValueKind.Number)
            {
                code = c.GetInt32();
            }
            AppLogger.Debug("MusicApi.History", $"Song report code={code} id={reportId} mid={song.Mid} title={song.Title}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn("MusicApi.History", $"Song report failed for id={reportId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 上报歌单播放记录到云端（按歌单对象）
    /// </summary>
    public static async Task ReportRecentPlaylistAsync(Playlist playlist, CancellationToken ct = default)
    {
        string playlistId = playlist.Tid > 0 ? playlist.Tid.ToString() : playlist.DirId.ToString();
        await ReportRecentPlaylistAsync(playlistId, playlist.Title, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 上报歌单播放记录到云端（按歌单 ID 与标题）
    /// </summary>
    public static async Task ReportRecentPlaylistAsync(string playlistId, string title = "", CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn || string.IsNullOrEmpty(playlistId) || playlistId == "0")
        {
            return;
        }

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var payload = BuildReportRecentHistoryPayload(playlistId, (int)RecentHistoryType.Playlist, nowSec, 1);
            var resp = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);
            int code = -1;
            if (doc.RootElement.TryGetProperty("report_recent", out var rr) &&
                rr.TryGetProperty("code", out var c) &&
                c.ValueKind == JsonValueKind.Number)
            {
                code = c.GetInt32();
            }
            AppLogger.Debug("MusicApi.History", $"Playlist report code={code} id={playlistId} title={title}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn("MusicApi.History", $"Playlist report failed for id={playlistId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 上报专辑播放记录到云端（按专辑对象）
    /// </summary>
    public static async Task ReportRecentAlbumAsync(Album album, CancellationToken ct = default)
    {
        string albumId = album.Id > 0 ? album.Id.ToString() : album.Mid;
        await ReportRecentAlbumAsync(albumId, album.Mid, album.Title, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 上报专辑播放记录到云端（按专辑 ID/Mid 与标题）
    /// </summary>
    public static async Task ReportRecentAlbumAsync(string albumReportId, string albumMid = "", string title = "", CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn || string.IsNullOrEmpty(albumReportId))
        {
            return;
        }

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var payload = BuildReportRecentHistoryPayload(albumReportId, (int)RecentHistoryType.Album, nowSec, 1);
            var resp = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(resp);
            int code = -1;
            if (doc.RootElement.TryGetProperty("report_recent", out var rr) &&
                rr.TryGetProperty("code", out var c) &&
                c.ValueKind == JsonValueKind.Number)
            {
                code = c.GetInt32();
            }
            AppLogger.Debug("MusicApi.History", $"Album report code={code} id={albumReportId} mid={albumMid} title={title}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Warn("MusicApi.History", $"Album report failed for id={albumReportId}: {ex.Message}");
        }
    }

    /// <summary>
    /// 上报单曲播放记录到云端，兼容调用接口
    /// </summary>
    public static async Task ReportRecentHistoryAsync(
        Song song,
        Playlist? sourcePlaylist = null,
        Album? sourceAlbum = null,
        CancellationToken ct = default)
    {
        await ReportRecentSongAsync(song, ct).ConfigureAwait(false);

        if (sourcePlaylist != null)
        {
            await ReportRecentPlaylistAsync(sourcePlaylist, ct).ConfigureAwait(false);
        }

        if (sourceAlbum != null)
        {
            await ReportRecentAlbumAsync(sourceAlbum, ct).ConfigureAwait(false);
        }
    }
}
