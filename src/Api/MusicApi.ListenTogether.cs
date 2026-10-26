using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    private const string AppUserAgent = "QQMusic 14090008(android 14)";
    private const string WebUserAgent = "Mozilla/5.0 (Linux; Android 14; MelodistTV) AppleWebKit/537.36";

    private static readonly HttpClient s_noRedirectClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly Regex s_urlRegex = new(@"https?://[a-zA-Z0-9\-._~:/?#\[\]@!$&'*+,;=%]+", RegexOptions.Compiled);
    private static readonly Regex s_domainRegex = new(@"(?:[a-zA-Z0-9-]+\.)*y\.qq\.com/[a-zA-Z0-9\-._~:/?#\[\]@!$&'*+,;=%]+", RegexOptions.Compiled);
    private static readonly Regex s_lugcRegex = new(@"LUGC[0-9a-zA-Z]+", RegexOptions.Compiled);
    private static readonly char[] s_urlTrimChars = [',', '.', ';', ')', '，', '。'];

    /// <summary>
    /// 向移动端 App 网关发送 POST 请求并获取响应文本。
    /// </summary>
    /// <param name="jsonPayload">待发送的明文 JSON 载荷。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>网关返回的响应字符串。</returns>
    public static async Task<string> PostAppGatewayAsync(string jsonPayload, CancellationToken ct = default)
    {
        var sign = ComputeZzcSign(jsonPayload);
        var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?_={sign}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);
        req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        req.Headers.Referrer = new Uri("https://y.qq.com/");

        var cookieHeader = UserSession.Current.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            req.Headers.Add("Cookie", cookieHeader);
        }

        using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 发起创建一起听房间请求。
    /// </summary>
    /// <param name="song">初始播放歌曲。</param>
    /// <param name="playlist">当前初始待播队列。</param>
    /// <param name="offsetMs">初始播放进度偏移（毫秒）。</param>
    /// <param name="playStatus">初始播放状态（2 为暂停，1 为播放）。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>创建成功返回房间信息，失败返回 null。</returns>
    public static async Task<ListenTogetherRoomInfo?> CreateListenTogetherRoomAsync(
        Song song,
        IReadOnlyList<Song>? playlist = null,
        long offsetMs = 0,
        int playStatus = 2,
        CancellationToken ct = default)
    {
        var songId = song.Id;
        var songTitle = song.Title ?? "";

        var songsToSync = playlist != null && playlist.Count > 0 ? playlist.Take(1000).ToList() : [song];
        if (songsToSync.Count == 0) songsToSync.Add(song);

        int currIdx = 0;
        for (int i = 0; i < songsToSync.Count; i++)
        {
            if (songsToSync[i].Id == songId)
            {
                currIdx = i;
                break;
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_create");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSvr");
            writer.WriteString("method", "LightUGCRoomCreate");
            writer.WriteStartObject("param");
            writer.WriteNumber("fromTag", 0);
            writer.WriteNumber("playStatus", playStatus);
            writer.WriteNumber("playMode", 0);
            writer.WriteNumber("songOffset", offsetMs);
            writer.WriteNumber("seekTime", offsetMs);
            writer.WriteNumber("sourceType", 0);
            writer.WriteNumber("avatarValid", 0);
            writer.WriteNumber("musicWorldValid", 0);
            writer.WriteNumber("isAITogether", 0);
            writer.WriteNumber("isMatchStranger", 0);

            writer.WriteStartObject("songPlaying");
            writer.WriteNumber("songID", songId);
            writer.WriteNumber("songType", 0);
            writer.WriteNumber("songIndex", currIdx);
            writer.WriteString("title", songTitle);
            writer.WriteEndObject();

            writer.WriteStartArray("songInfos");
            for (int i = 0; i < songsToSync.Count; i++)
            {
                var s = songsToSync[i];
                writer.WriteStartObject();
                writer.WriteNumber("songID", s.Id);
                writer.WriteNumber("songType", 0);
                writer.WriteNumber("songIndex", i);
                writer.WriteString("title", s.Title ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject(); // param
            writer.WriteEndObject(); // req_create
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_create", out var reqCreate)) return null;
            if (!reqCreate.TryGetProperty("code", out var codeElem) || codeElem.GetInt32() != 0) return null;
            if (!reqCreate.TryGetProperty("data", out var dataElem)) return null;
            if (!dataElem.TryGetProperty("entryInfo", out var entryInfo)) return null;
            if (!entryInfo.TryGetProperty("roomInfo", out var roomInfo)) return null;

            var showId = roomInfo.TryGetProperty("showID", out var sIdProp) ? sIdProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(showId)) return null;

            var createUin = roomInfo.TryGetProperty("createUin", out var cUinProp) ? cUinProp.GetString() ?? "" : "";
            var shareUrl = $"https://i.y.qq.com/n2/m/share/listen_together/index.html?showid={showId}&songid={songId}";

            return new ListenTogetherRoomInfo
            {
                ShowId = showId,
                CreatorUin = createUin,
                ShareUrl = shareUrl,
                CurrentSongId = songId,
                CurrentSongTitle = songTitle,
                UsersCount = 1,
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"创建一起听房间失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 加入已存在的一起听房间。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="fromEncUin">邀请者加密 UIN。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>是否成功加入房间。</returns>
    public static async Task<bool> JoinListenTogetherRoomAsync(string showId, string fromEncUin = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_join");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSvr");
            writer.WriteString("method", "LightUGCRoomEntry");
            writer.WriteStartObject("param");
            writer.WriteString("showID", showId);
            writer.WriteString("fromEncryptedUin", fromEncUin);
            writer.WriteNumber("entryType", 0);
            writer.WriteNumber("fromTag", 0);
            writer.WriteNumber("avatarValid", 0);
            writer.WriteNumber("musicWorldValid", 0);
            writer.WriteStartArray("songInfos");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_join", out var reqJoin)) return false;
            return reqJoin.TryGetProperty("code", out var codeElem) && codeElem.GetInt32() == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"加入一起听房间失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 发送一起听心跳请求并返回房间实时状态。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>心跳状态数据对象；若请求失败返回 null。</returns>
    public static async Task<ListenTogetherSyncState?> HeartbeatListenTogetherRoomAsync(string showId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_hb");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSvr");
            writer.WriteString("method", "LightUGCRoomHeartBeat");
            writer.WriteStartObject("param");
            writer.WriteString("showID", showId);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_hb", out var reqHb)) return null;

            int code = reqHb.TryGetProperty("code", out var codeElem) ? codeElem.GetInt32() : -1;
            if (code == 1000 || code == 10004 || code == 11000 || code == 11001)
            {
                return new ListenTogetherSyncState
                {
                    ShowId = showId,
                    IsRoomValid = false
                };
            }
            if (code != 0) return null;

            if (!reqHb.TryGetProperty("data", out var dataElem)) return null;

            long currentSongId = 0;
            string songTitle = "";
            int playStatus = 0;
            long songOffsetMs = 0;
            long songPlayVersion = 0;

            if (dataElem.TryGetProperty("songInfo", out var songInfoElem))
            {
                if (songInfoElem.TryGetProperty("playingSongInfo", out var playingElem))
                {
                    if (playingElem.TryGetProperty("songID", out var sidProp)) currentSongId = sidProp.GetInt64();
                    if (playingElem.TryGetProperty("title", out var titleProp)) songTitle = titleProp.GetString() ?? "";
                }
                if (songInfoElem.TryGetProperty("playStatus", out var psProp)) playStatus = psProp.GetInt32();
                if (songInfoElem.TryGetProperty("songOffset", out var offsetProp)) songOffsetMs = offsetProp.GetInt64();
                if (songInfoElem.TryGetProperty("songPlayVersion", out var spvProp)) songPlayVersion = spvProp.GetInt64();
            }

            long mediaVersion = 0;
            if (dataElem.TryGetProperty("mediaVersion", out var mvProp)) mediaVersion = mvProp.GetInt64();

            int usersCount = 1;
            List<string> encUins = [];
            if (dataElem.TryGetProperty("users", out var usersElem) && usersElem.ValueKind == JsonValueKind.Array)
            {
                usersCount = usersElem.GetArrayLength();
                foreach (var u in usersElem.EnumerateArray())
                {
                    if (u.TryGetProperty("encryptUin", out var euProp))
                    {
                        var s = euProp.GetString();
                        if (!string.IsNullOrEmpty(s)) encUins.Add(s);
                    }
                }
            }

            return new ListenTogetherSyncState
            {
                ShowId = showId,
                CurrentSongId = currentSongId,
                SongTitle = songTitle,
                PlayStatus = playStatus,
                SongOffsetMs = songOffsetMs,
                SongPlayVersion = songPlayVersion,
                MediaVersion = mediaVersion,
                UsersCount = usersCount,
                IsRoomValid = true,
                UserEncUins = encUins
            };
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MusicApi", $"心跳请求异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 退出指定的一起听房间。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>是否成功退出房间。</returns>
    public static async Task<bool> QuitListenTogetherRoomAsync(string showId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return false;

        var uin = UserSession.Current.Uin ?? "";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_quit");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSvr");
            writer.WriteString("method", "LightUGCRoomQuit");
            writer.WriteStartObject("param");
            writer.WriteString("showID", showId);
            writer.WriteStartArray("uinList");
            if (!string.IsNullOrEmpty(uin)) writer.WriteStringValue(uin);
            writer.WriteEndArray();
            writer.WriteString("from", "player");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_quit", out var reqQuit)) return false;
            return reqQuit.TryGetProperty("code", out var codeElem) && codeElem.GetInt32() == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"退出一起听房间异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 上报房间播放控制操作。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="cmd">操作控制码（1: 队列重置, 2: 恢复播放, 3: 暂停, 4: 切歌, 7: 进度跳转）。</param>
    /// <param name="songId">当前/目标曲目数字 ID。</param>
    /// <param name="songTitle">曲目标题。</param>
    /// <param name="songIndex">曲目在队列中的索引。</param>
    /// <param name="offsetMs">播放进度偏移（毫秒）。</param>
    /// <param name="playlist">待重置的完整播放队列（仅 cmd=1 时需要）。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>操作是否成功被服务端确认。</returns>
    public static async Task<bool> SendListenTogetherSongOperAsync(
        string showId,
        int cmd,
        long songId = 0,
        string songTitle = "",
        int songIndex = 0,
        long offsetMs = 0,
        IReadOnlyList<Song>? playlist = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_oper");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSongSvr");
            writer.WriteString("method", "LightUGCRoomSongOper");
            writer.WriteStartObject("param");
            writer.WriteString("showID", showId);
            writer.WriteNumber("cmd", cmd);
            writer.WriteNumber("sourceType", 0);
            writer.WriteNumber("playlistType", 0);

            switch (cmd)
            {
                case 2: // Play
                case 3: // Pause
                case 7: // Seek
                    writer.WriteNumber("seekTime", offsetMs);
                    writer.WriteNumber("songOffset", offsetMs);
                    if (songId > 0)
                    {
                        writer.WriteStartObject("songPlaying");
                        writer.WriteNumber("songID", songId);
                        writer.WriteNumber("songType", 0);
                        writer.WriteNumber("songIndex", songIndex);
                        if (!string.IsNullOrEmpty(songTitle)) writer.WriteString("title", songTitle);
                        writer.WriteEndObject();
                    }
                    break;

                case 4: // Change song
                    writer.WriteNumber("songOffset", 0);
                    writer.WriteStartObject("songPlaying");
                    writer.WriteNumber("songID", songId);
                    writer.WriteNumber("songType", 0);
                    writer.WriteNumber("songIndex", songIndex);
                    if (!string.IsNullOrEmpty(songTitle)) writer.WriteString("title", songTitle);
                    writer.WriteEndObject();
                    break;

                case 1: // Reset playlist
                    writer.WriteNumber("seekTime", offsetMs);
                    writer.WriteNumber("songOffset", offsetMs);
                    if (songId > 0)
                    {
                        writer.WriteStartObject("songPlaying");
                        writer.WriteNumber("songID", songId);
                        writer.WriteNumber("songType", 0);
                        writer.WriteNumber("songIndex", songIndex);
                        if (!string.IsNullOrEmpty(songTitle)) writer.WriteString("title", songTitle);
                        writer.WriteEndObject();
                    }
                    if (playlist != null && playlist.Count > 0)
                    {
                        writer.WriteStartArray("songList");
                        var limited = playlist.Take(1000);
                        int pIdx = 0;
                        foreach (var s in limited)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("songID", s.Id);
                            writer.WriteNumber("songType", 0);
                            writer.WriteNumber("songIndex", pIdx++);
                            writer.WriteString("title", s.Title ?? "");
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    break;
            }

            writer.WriteEndObject(); // param
            writer.WriteEndObject(); // req_oper
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_oper", out var reqOper)) return false;
            return reqOper.TryGetProperty("code", out var codeElem) && codeElem.GetInt32() == 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"上报一起听控制操作失败 (cmd={cmd}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取房间当前全量待播曲目的数字 ID 队列。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>歌曲数字 ID 队列。</returns>
    public static async Task<IReadOnlyList<long>> GetListenTogetherSongListAsync(string showId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return [];

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("comm");
            using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
            {
                commDoc.RootElement.WriteTo(writer);
            }

            writer.WriteStartObject("req_oper");
            writer.WriteString("module", "music.lightUGCRoom.LightUGCRoomSongSvr");
            writer.WriteString("method", "LightUGCRoomSongOper");
            writer.WriteStartObject("param");
            writer.WriteString("showID", showId);
            writer.WriteNumber("cmd", 0);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        try
        {
            var payload = Encoding.UTF8.GetString(stream.ToArray());
            var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respStr);
            if (!doc.RootElement.TryGetProperty("req_oper", out var reqOper)) return [];
            if (!reqOper.TryGetProperty("data", out var dataElem)) return [];
            if (!dataElem.TryGetProperty("songList", out var listElem) || listElem.ValueKind != JsonValueKind.Array) return [];

            List<long> result = [];
            foreach (var item in listElem.EnumerateArray())
            {
                if (item.TryGetProperty("songID", out var idElem))
                {
                    long sid = idElem.GetInt64();
                    if (sid > 0) result.Add(sid);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"拉取一起听房间曲目队列失败: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 批量通过歌曲数字 ID 检索并解析完整曲目实体序列。
    /// </summary>
    /// <param name="songIds">歌曲数字 ID 列表。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>解析成功的曲目列表。</returns>
    public static async Task<IReadOnlyList<Song>> GetSongsBySongIdsAsync(IReadOnlyList<long> songIds, CancellationToken ct = default)
    {
        var validIds = songIds.Where(id => id > 0).Distinct().Take(1000).ToList();
        if (validIds.Count == 0) return [];

        var chunks = validIds.Chunk(100).ToList();
        var fetchTasks = chunks.Select(async chunkIds =>
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("comm");
                using (var commDoc = JsonDocument.Parse(BuildAppCommJson()))
                {
                    commDoc.RootElement.WriteTo(writer);
                }

                writer.WriteStartObject("req_track");
                writer.WriteString("module", "music.trackInfo.UniformRuleCtrl");
                writer.WriteString("method", "CgiGetTrackInfo");
                writer.WriteStartObject("param");

                writer.WriteStartArray("ids");
                foreach (var id in chunkIds) writer.WriteNumberValue(id);
                writer.WriteEndArray();

                writer.WriteStartArray("types");
                foreach (var _ in chunkIds) writer.WriteNumberValue(0);
                writer.WriteEndArray();

                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            try
            {
                var payload = Encoding.UTF8.GetString(stream.ToArray());
                var respStr = await PostAppGatewayAsync(payload, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respStr);
                if (!doc.RootElement.TryGetProperty("req_track", out var reqTrack)) return [];
                if (!reqTrack.TryGetProperty("data", out var dataElem)) return [];
                if (!dataElem.TryGetProperty("tracks", out var tracksElem) || tracksElem.ValueKind != JsonValueKind.Array) return [];

                List<Song> parsedSongs = [];
                foreach (var track in tracksElem.EnumerateArray())
                {
                    var s = ParseSongFromElement(track);
                    if (s != null) parsedSongs.Add(s);
                }
                return (IReadOnlyList<Song>)parsedSongs;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MusicApi", $"分片拉取曲目元数据失败: {ex.Message}");
                return [];
            }
        });

        var results = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        Dictionary<long, Song> songMap = [];
        foreach (var list in results)
        {
            foreach (var s in list)
            {
                songMap[s.Id] = s;
            }
        }

        var orderedSongs = new List<Song>(validIds.Count);
        foreach (var id in validIds)
        {
            if (songMap.TryGetValue(id, out var s))
            {
                orderedSongs.Add(s);
            }
        }
        return orderedSongs;
    }

    /// <summary>
    /// 解析输入分享文本中的一起听房间标识与参数。
    /// </summary>
    /// <param name="rawText">用户输入的文本或链接。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>三元组（showId 房间标识, encUin 邀请者加密 UIN, songId 携带的曲目 ID）。</returns>
    public static async Task<(string ShowId, string EncUin, long SongId)> ResolveListenTogetherShareTargetAsync(string rawText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return ("", "", 0);

        var cleanText = rawText.Trim();

        // 尝试匹配完整 HTTP/HTTPS 链接
        string? matchedUrl = null;
        var urlMatch = s_urlRegex.Match(cleanText);
        if (urlMatch.Success)
        {
            matchedUrl = urlMatch.Value;
        }
        else
        {
            var domainMatch = s_domainRegex.Match(cleanText);
            if (domainMatch.Success)
            {
                matchedUrl = "https://" + domainMatch.Value;
            }
        }

        if (matchedUrl != null)
        {
            var trimmedUrl = matchedUrl.TrimEnd(s_urlTrimChars);
            var parsed = ParseFromListenTogetherHttpUrl(trimmedUrl);
            if (!string.IsNullOrEmpty(parsed.ShowId))
            {
                return parsed;
            }

            // 若未直接取得 showId，发起网络探测获取 302 重定向 Location
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, trimmedUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                using var resp = await s_noRedirectClient.SendAsync(req, ct).ConfigureAwait(false);
                var location = resp.Headers.Location;
                if (location != null)
                {
                    var resolved = ParseFromListenTogetherHttpUrl(location.ToString());
                    if (!string.IsNullOrEmpty(resolved.ShowId))
                    {
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MusicApi", $"短链探测失败: {ex.Message}");
            }
        }

        // 兜底正则匹配房间号 (LUGC 开头)
        var lugcMatch = s_lugcRegex.Match(cleanText);
        if (lugcMatch.Success)
        {
            return (lugcMatch.Value, "", 0);
        }

        return ("", "", 0);
    }

    private static (string ShowId, string EncUin, long SongId) ParseFromListenTogetherHttpUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return ("", "", 0);

        var query = uri.Query.TrimStart('?');
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        string showId = "";
        string encUin = "";
        long songId = 0;

        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = pair[..idx];
            var val = Uri.UnescapeDataString(pair[(idx + 1)..]);

            if (key.Equals("showid", StringComparison.OrdinalIgnoreCase)) showId = val;
            else if (key.Equals("share_encrypt_uin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(encUin)) encUin = val;
            else if (key.Equals("hosteuin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(encUin)) encUin = val;
            else if (key.Equals("songid", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, out var sid)) songId = sid;
        }

        return (showId, encUin, songId);
    }

    /// <summary>
    /// 获取好友空间累计统计数据（累计听歌时长、听过歌曲数及共同收藏歌曲数）。
    /// </summary>
    /// <param name="showId">房间标识 showID。</param>
    /// <param name="targetEncUin">对方加密 UIN。</param>
    /// <param name="selfEncUin">自身加密 UIN。</param>
    /// <param name="ct">异步取消令牌。</param>
    /// <returns>统计实体；获取失败返回 null。</returns>
    public static async Task<ListenTogetherSpaceStats?> GetListenTogetherSpaceStatsAsync(
        string showId,
        string targetEncUin = "",
        string selfEncUin = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(showId)) return null;

        string uinsParam = (!string.IsNullOrEmpty(selfEncUin) && !string.IsNullOrEmpty(targetEncUin))
            ? $"{selfEncUin},{targetEncUin}"
            : (!string.IsNullOrEmpty(targetEncUin) ? targetEncUin : selfEncUin);

        var url = $"https://i2.y.qq.com/n3/client/pages/close_friend_space/index.html?type=1&roomid={showId}&uins={uinsParam}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
        req.Headers.Referrer = new Uri("https://y.qq.com/");

        var cookieHeader = UserSession.Current.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            req.Headers.Add("Cookie", cookieHeader);
        }

        try
        {
            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            const string prefix = "window.__ssrFirstPageData__=";
            int startIndex = html.IndexOf(prefix, StringComparison.Ordinal);
            if (startIndex == -1) return null;

            int endIndex = html.IndexOf("</script>", startIndex, StringComparison.Ordinal);
            if (endIndex == -1) return null;

            var jsStatement = html.Substring(startIndex + prefix.Length, endIndex - (startIndex + prefix.Length)).Trim().TrimEnd(';');
            using var rawDoc = JsonDocument.Parse(jsStatement);
            var rootElem = rawDoc.RootElement;
            if (rootElem.ValueKind == JsonValueKind.String)
            {
                var innerJson = rootElem.GetString();
                if (string.IsNullOrEmpty(innerJson)) return null;
                using var innerDoc = JsonDocument.Parse(innerJson);
                return ExtractSpaceStatsFromDoc(innerDoc);
            }

            return ExtractSpaceStatsFromDoc(rawDoc);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MusicApi", $"拉取好友空间统计异常: {ex.Message}");
            return null;
        }
    }

    private static ListenTogetherSpaceStats? ExtractSpaceStatsFromDoc(JsonDocument doc)
    {
        try
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("rsp_1", out var rsp1)) return null;
            if (!rsp1.TryGetProperty("req_0", out var req0)) return null;
            if (!req0.TryGetProperty("data", out var dataElem)) return null;
            if (!dataElem.TryGetProperty("Together", out var togetherElem)) return null;

            JsonElement togetherData;
            if (togetherElem.TryGetProperty("TogetherData", out var td)) togetherData = td;
            else if (togetherElem.TryGetProperty("BaseData", out var bd)) togetherData = bd;
            else return null;

            long listenTimeSec = togetherData.TryGetProperty("ListenTime", out var lt) ? lt.GetInt64() : 0;
            int listenSongsCount = togetherData.TryGetProperty("ListenNum", out var ln) ? ln.GetInt32() : 0;
            int favSongsCount = togetherData.TryGetProperty("FavoriteSongNum", out var fn) ? fn.GetInt32() : 0;

            return new ListenTogetherSpaceStats
            {
                ListenMinutes = Math.Max(0, listenTimeSec / 60),
                ListenSongsCount = listenSongsCount,
                FavoriteSongsCount = favSongsCount
            };
        }
        catch
        {
            return null;
        }
    }
}
