using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    public static async Task<List<QualityOption>> ProbeSongQualitiesAsync(string songMid, string mediaMid = "", CancellationToken ct = default)
    {
        return await ProbeSongQualitiesInternalAsync(songMid, mediaMid, canRetryWithRenew: true, ct).ConfigureAwait(false);
    }

    private static async Task<List<QualityOption>> ProbeSongQualitiesInternalAsync(string songMid, string mediaMid, bool canRetryWithRenew, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mediaMid)) mediaMid = songMid;

        await LoginService.EnsureMusicKeyAsync(false, ct).ConfigureAwait(false);

        var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
        var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
        var authst = !string.IsNullOrEmpty(UserSession.Current.MusicKey)
            ? UserSession.Current.MusicKey
            : (UserSession.Current.Cookies.TryGetValue("qm_keyst", out var mk) && !string.IsNullOrEmpty(mk)
                ? mk
                : (UserSession.Current.Cookies.TryGetValue("qqmusic_key", out var qmk) ? qmk : ""));

        var requests = AudioQualityHelper.ProbeRequests;
        var requestJson = new StringBuilder(1536);
        requestJson.Append("{\"comm\":{\"uin\":\"").Append(JsonEncodedText.Encode(uin))
            .Append("\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"")
            .Append(JsonEncodedText.Encode(authst)).Append("\"},")
            .Append("\"songinfo\":{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{\"song_mid\":\"")
            .Append(JsonEncodedText.Encode(songMid)).Append("\"}}");

        foreach (var request in requests)
        {
            requestJson.Append(",\"").Append(request.Key)
                .Append("\":{\"module\":\"vkey.GetVkeyServer\",\"method\":\"CgiGetVkey\",\"param\":{\"guid\":\"10000\",\"songmid\":[\"")
                .Append(JsonEncodedText.Encode(songMid)).Append("\"],\"songtype\":[0],\"uin\":\"")
                .Append(JsonEncodedText.Encode(uin)).Append("\",\"loginflag\":1,\"platform\":\"20\",\"filename\":[\"")
                .Append(request.Prefix).Append(JsonEncodedText.Encode(mediaMid)).Append(request.Extension)
                .Append("\"]}}");
        }
        requestJson.Append('}');
        var jsonPayload = requestJson.ToString();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            req.Headers.Referrer = new Uri("https://y.qq.com/");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.Add("Cookie", cookieHeader);
            }

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var respStr = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(respStr);
            var options = ParseProbedQualities(doc.RootElement, requests);

            if (canRetryWithRenew && UserSession.Current.IsLoggedIn)
            {
                var hasVipSource = options.Any(o => o.Tier != AudioQualityTier.Standard && !string.IsNullOrEmpty(o.BitrateInfo));
                var hasVipUrl = options.Any(o => o.Tier != AudioQualityTier.Standard && o.Available);
                if (hasVipSource && !hasVipUrl)
                {
                    AppLogger.Info("MusicApi", "ProbeSongQualities: VIP audio tracks exist but no valid VIP URL obtained, attempting token renewal...");
                    var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    if (renewed)
                    {
                        return await ProbeSongQualitiesInternalAsync(songMid, mediaMid, canRetryWithRenew: false, ct).ConfigureAwait(false);
                    }
                }
            }

            return options;
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", "ProbeSongQualitiesAsync exception", ex);
            var options = new List<QualityOption>(AudioQualityHelper.SelectionOrder.Count);
            foreach (var tier in AudioQualityHelper.SelectionOrder)
            {
                options.Add(new QualityOption(tier, AudioQualityHelper.GetBadge(tier), AudioQualityHelper.GetQualityName(tier), AudioQualityHelper.GetDefaultSpec(tier), "", false));
            }
            return options;
        }
    }

    internal static List<QualityOption> ParseProbedQualities(JsonElement root, (string Key, AudioQualityTier Tier, string Prefix, string Extension)[] requests)
    {
        long interval = 0;
        long[] sizeNew = [];
        long sizeDolby = 0;
        long hiresRaw = 0;
        long flacSize = 0;
        long size320 = 0;
        long size128 = 0;
        int hiresSample = 0;
        int hiresBitdepth = 0;
        bool hasFileObj = false;

        if (root.TryGetProperty("songinfo", out var songInfoObj) &&
            songInfoObj.TryGetProperty("data", out var songData) &&
            songData.TryGetProperty("track_info", out var trackInfo))
        {
            if (trackInfo.TryGetProperty("interval", out var intervalProp)) intervalProp.TryGetInt64(out interval);
            if (trackInfo.TryGetProperty("file", out var fileObj))
            {
                hasFileObj = true;
                if (fileObj.TryGetProperty("size_new", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    sizeNew = values.EnumerateArray()
                        .Select(value => value.TryGetInt64(out var size) ? size : 0)
                        .ToArray();
                }

                sizeDolby = ReadJsonInt64(fileObj, "size_dolby");
                if (sizeDolby == 0) sizeDolby = GetArrayValue(sizeNew, 3);

                hiresRaw = ReadJsonInt64(fileObj, "size_hires");
                if (hiresRaw == 0) hiresRaw = ReadJsonInt64(fileObj, "size_96flac");
                if (hiresRaw == 0) hiresRaw = ReadJsonInt64(fileObj, "size_24bit");
                if (hiresRaw == 0) hiresRaw = GetArrayValue(sizeNew, 11);

                flacSize = ReadJsonInt64(fileObj, "size_flac");
                if (flacSize == 0) flacSize = GetArrayValue(sizeNew, 12);

                size320 = ReadJsonInt64(fileObj, "size_320mp3");
                if (size320 == 0) size320 = GetArrayValue(sizeNew, 3);

                size128 = ReadJsonInt64(fileObj, "size_128mp3");

                hiresSample = (int)ReadJsonInt64(fileObj, "hires_sample");
                hiresBitdepth = (int)ReadJsonInt64(fileObj, "hires_bitdepth");
            }
        }

        var isTrueHiRes = hiresRaw > 0 || hiresSample > 48000 || hiresBitdepth > 16;
        var hiResSize = isTrueHiRes ? (hiresRaw > 0 ? hiresRaw : flacSize) : 0L;

        var sizeByTier = new Dictionary<AudioQualityTier, long>
        {
            [AudioQualityTier.Master] = GetArrayValue(sizeNew, 0),
            [AudioQualityTier.Premium] = GetArrayValue(sizeNew, 4) > 0 ? GetArrayValue(sizeNew, 4) : GetArrayValue(sizeNew, 1),
            [AudioQualityTier.Atmos51] = GetArrayValue(sizeNew, 1) > 0 ? GetArrayValue(sizeNew, 1) : GetArrayValue(sizeNew, 2),
            [AudioQualityTier.Atmos71] = GetArrayValue(sizeNew, 2) > 0 ? GetArrayValue(sizeNew, 2) : GetArrayValue(sizeNew, 3),
            [AudioQualityTier.Dolby] = sizeDolby,
            [AudioQualityTier.HiRes] = hiResSize,
            [AudioQualityTier.SQ] = flacSize,
            [AudioQualityTier.HQ] = size320,
            [AudioQualityTier.Standard] = size128
        };

        (string? Url, bool HasValidUrl) ExtractUrl(string reqKey, string prefix)
        {
            if (root.TryGetProperty(reqKey, out var reqObj) &&
                reqObj.TryGetProperty("data", out var data))
            {
                string? sip = null;
                if (data.TryGetProperty("sip", out var sips) && sips.ValueKind == JsonValueKind.Array && sips.GetArrayLength() > 0)
                {
                    sip = sips[0].GetString();
                }

                if (data.TryGetProperty("midurlinfo", out var midUrlInfo) &&
                    midUrlInfo.ValueKind == JsonValueKind.Array &&
                    midUrlInfo.GetArrayLength() > 0)
                {
                    var info = midUrlInfo[0];
                    var purl = info.TryGetProperty("purl", out var p) ? p.GetString() : null;
                    var result = info.TryGetProperty("result", out var r) && r.TryGetInt32(out var res) ? res : 0;

                    var hasValid = !string.IsNullOrWhiteSpace(purl) &&
                                   purl.Length > 5 &&
                                   result == 0 &&
                                   !string.IsNullOrEmpty(sip) &&
                                   purl.Contains(prefix, StringComparison.OrdinalIgnoreCase);

                    if (hasValid && purl != null)
                    {
                        return (sip + purl, true);
                    }
                }
            }
            return (null, false);
        }

        var options = new List<QualityOption>(requests.Length);
        foreach (var request in requests)
        {
            var (playUrl, hasValidUrl) = ExtractUrl(request.Key, request.Prefix);
            var size = sizeByTier.GetValueOrDefault(request.Tier, 0L);

            bool available;
            if (hasFileObj)
            {
                if (request.Tier == AudioQualityTier.HiRes)
                {
                    available = isTrueHiRes && size > 0 && hasValidUrl;
                }
                else
                {
                    available = size > 0 && hasValidUrl;
                }
            }
            else
            {
                available = hasValidUrl;
            }

            var spec = AudioQualityHelper.GetDefaultSpec(request.Tier);
            if (request.Tier == AudioQualityTier.HiRes && (hiresSample > 0 || hiresBitdepth > 0))
            {
                var depth = hiresBitdepth > 0 ? hiresBitdepth : 24;
                var rate = hiresSample > 0 ? hiresSample / 1000 : 96;
                spec = $"{depth}bit / {rate}kHz";
            }
            else if (request.Tier == AudioQualityTier.Master && (hiresSample > 0 || hiresBitdepth > 0))
            {
                var depth = hiresBitdepth > 0 ? hiresBitdepth : 24;
                var rate = hiresSample > 0 ? hiresSample / 1000 : 192;
                spec = $"{depth}bit / {rate}kHz";
            }

            var bitrate = size > 0 && interval > 0
                ? $"{(long)Math.Round((size * 8.0) / interval / 1000.0)}kbps"
                : "";

            options.Add(new QualityOption(
                request.Tier,
                AudioQualityHelper.GetBadge(request.Tier),
                AudioQualityHelper.GetQualityName(request.Tier),
                spec,
                bitrate,
                available,
                available ? playUrl : null,
                size));
        }

        return options;
    }

    /// <summary>
    /// 根据用户指定或偏好的音质获取直链，支持智能梯度回退
    /// </summary>
    public static async Task<(string? Url, string Quality, AudioQualityTier Tier)> GetPlayUrlForTierAsync(string songMid, string mediaMid = "", AudioQualityTier preferred = AudioQualityTier.SQ, CancellationToken ct = default)
    {
        var options = await ProbeSongQualitiesAsync(songMid, mediaMid, ct).ConfigureAwait(false);

        // 先尝试用户偏好的目标档位
        var target = options.FirstOrDefault(o => o.Tier == preferred && o.Available);
        if (target != null && !string.IsNullOrEmpty(target.PlayUrl))
        {
            return (target.PlayUrl, target.Badge, target.Tier);
        }

        // 若目标档位不可用，则只向定义好的兼容档位回退，避免在特殊编码间横跳。
        foreach (var tier in AudioQualityHelper.GetFallbackTiers(preferred))
        {
            var opt = options.FirstOrDefault(o => o.Tier == tier && o.Available);
            if (opt != null && !string.IsNullOrEmpty(opt.PlayUrl))
            {
                return (opt.PlayUrl, opt.Badge, opt.Tier);
            }
        }

        // 任意可用项兜底
        var anyAvailable = options.FirstOrDefault(o => o.Available && !string.IsNullOrEmpty(o.PlayUrl));
        if (anyAvailable != null)
        {
            return (anyAvailable.PlayUrl, anyAvailable.Badge, anyAvailable.Tier);
        }

        return (null, "无音源", AudioQualityTier.Standard);
    }
    private static long ReadJsonInt64(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static long GetArrayValue(long[] source, int index) =>
        index >= 0 && index < source.Length ? source[index] : 0;

    private static string GetQualityName(AudioQualityTier tier) => AudioQualityHelper.GetQualityName(tier);


    /// <summary>
    /// 获取歌曲直链播放 URL 与对应音质档位（自动读取用户偏好音质）
    /// </summary>
    public static async Task<(string? Url, string Quality)> GetPlayUrlWithQualityAsync(string songMid, string mediaMid = "", CancellationToken ct = default)
    {
        var preferredTier = AudioQualityHelper.Parse(UserSession.Current.PreferredQuality);
        var (url, qName, _) = await GetPlayUrlForTierAsync(songMid, mediaMid, preferredTier, ct).ConfigureAwait(false);
        return (url, qName);
    }

    /// <summary>
    /// 获取歌曲直链播放 URL
    /// </summary>
    public static async Task<string?> GetPlayUrlAsync(string songMid, CancellationToken ct = default)
    {
        var (url, _) = await GetPlayUrlWithQualityAsync(songMid, songMid, ct).ConfigureAwait(false);
        return url;
    }

    /// <summary>

    public static async Task<long> ResolveSongIdAsync(string songMid, CancellationToken ct = default)
    {
        try
        {
            var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
            var authst = UserSession.Current.MusicKey ?? "";
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
                $"\"songinfo\":{{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{{\"song_mid\":\"{songMid}\"}}}}}}";

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
            if (root.TryGetProperty("songinfo", out var songInfoObj) &&
                songInfoObj.TryGetProperty("data", out var songData) &&
                songData.TryGetProperty("track_info", out var trackInfo) &&
                trackInfo.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number)
            {
                var id = idProp.GetInt64();
                AppLogger.Info("MusicApi", $"ResolveSongIdAsync: mid={songMid} resolved to id={id}");
                return id;
            }
            AppLogger.Warn("MusicApi", $"ResolveSongIdAsync: track_info.id not found in response for mid={songMid}: {json}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"ResolveSongIdAsync error for {songMid}", ex);
        }
        return 0;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_visualMidCache = new();

    /// <summary>
    /// 获取单曲专属视觉封面 MID（track_info.vs[1]），用于无 AlbumMid 单曲的原画/超高清封面拉取
    /// </summary>
    public static async Task<string?> GetSongVisualMidAsync(string songMid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;
        if (s_visualMidCache.TryGetValue(songMid, out var cached)) return cached;

        try
        {
            var uin = string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
            var authst = UserSession.Current.MusicKey ?? "";
            var url = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":19,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
                $"\"songinfo\":{{\"module\":\"music.pf_song_detail_svr\",\"method\":\"get_song_detail_yqq\",\"param\":{{\"song_mid\":\"{songMid}\"}}}}}}";

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
            if (root.TryGetProperty("songinfo", out var songInfoObj) &&
                songInfoObj.TryGetProperty("data", out var songData) &&
                songData.TryGetProperty("track_info", out var trackInfo) &&
                trackInfo.TryGetProperty("vs", out var vsProp) &&
                vsProp.ValueKind == JsonValueKind.Array)
            {
                var vsList = new List<string>(vsProp.GetArrayLength());
                foreach (var v in vsProp.EnumerateArray())
                {
                    vsList.Add(v.GetString() ?? "");
                }

                // 规范定义：vs[1] 恒定为 Single 主视觉封面 MID
                string? visualMid = null;
                if (vsList.Count > 1 && !string.IsNullOrWhiteSpace(vsList[1]))
                {
                    visualMid = vsList[1];
                }
                else
                {
                    visualMid = vsList.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                }

                if (!string.IsNullOrWhiteSpace(visualMid))
                {
                    s_visualMidCache[songMid] = visualMid;
                    return visualMid;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"GetSongVisualMidAsync error for {songMid}", ex);
        }
        return null;
    }

    /// <summary>
    /// 获取同步 LRC 歌词与翻译（解析 Base64 并进行双语时间轴对齐）
    /// </summary>
    public static async Task<List<LyricLine>> GetLyricsAsync(string songMid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(songMid))
        {
            return [new LyricLine(TimeSpan.Zero, "暂无歌词")];
        }

        // 1. 优先读取统一母本缓存 (mid_<songMid>.json)
        var masterCached = LocalLyricAutoMatcher.ReadMasterCacheByMid(songMid);
        if (masterCached != null && masterCached.Lines.Count > 0)
        {
            return masterCached.Lines.ConvertAll(l => l.ToDomain());
        }

        // 2. 兼容读取旧版 MetadataCacheService 缓存
        var cached = MetadataCacheService.GetLyrics(songMid);
        if (cached != null && cached.Count > 0)
        {
            return cached;
        }

        try
        {
            // 3. 调用 PlayLyricInfo 接口以获取原生原文与翻译歌词
            var jsonPayload = $"{{\"comm\":{{\"ct\":24,\"cv\":0}},\"playLyricInfo\":{{\"module\":\"music.musichallSong.PlayLyricInfo\",\"method\":\"GetPlayLyricInfo\",\"param\":{{\"songMID\":\"{songMid}\",\"songID\":0,\"qrc\":0,\"trans\":1,\"roma\":1,\"isHQ\":1}}}}}}";

            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var resp = await s_httpClient.PostAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", content, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("playLyricInfo", out var info) &&
                info.TryGetProperty("data", out var data))
            {
                var b64Lyric = data.TryGetProperty("lyric", out var l) ? l.GetString() : null;
                var b64Trans = data.TryGetProperty("trans", out var t) ? t.GetString() : null;

                var rawLyric = LyricParser.DecodeBase64(b64Lyric);
                var rawTrans = LyricParser.DecodeBase64(b64Trans);

                if (!string.IsNullOrWhiteSpace(rawLyric))
                {
                    var merged = LyricParser.MergeLyrics(rawLyric, rawTrans);
                    MetadataCacheService.SaveLyrics(songMid, merged);
                    LocalLyricAutoMatcher.SaveUnifiedCache(songMid, "", "", "", merged);
                    return merged;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", "GetLyricsAsync PlayLyricInfo error, falling back", ex);
        }

        // 4. 兜底备用：传统 fcg_query_lyric_new.fcg 接口
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songMid}&format=json&nobase64=1";
            var json = await s_httpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("lyric", out var lyricElem))
            {
                var rawLrc = lyricElem.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rawLrc))
                {
                    var merged = LyricParser.MergeLyrics(rawLrc, "");
                    MetadataCacheService.SaveLyrics(songMid, merged);
                    LocalLyricAutoMatcher.SaveUnifiedCache(songMid, "", "", "", merged);
                    return merged;
                }
            }
        }
        catch
        {
            // Ignore
        }

        return [new LyricLine(TimeSpan.Zero, "暂无歌词")];
    }
}
