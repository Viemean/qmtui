using System.IO;
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
            .Append("\",\"format\":\"json\",\"ct\":28,\"cv\":1,\"authst\":\"")
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
                var hasVipSource = options.Any(o => o.Tier > AudioQualityTier.HQ && o.FileSizeBytes > 0);
                var hasVipUrl = options.Any(o => o.Tier > AudioQualityTier.HQ && o.Available);
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

            await RefineQualityMetadataAsync(options, doc.RootElement, songMid, ct).ConfigureAwait(false);

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

                hiresRaw = ReadJsonInt64(fileObj, "size_hires");
                if (hiresRaw == 0) hiresRaw = ReadJsonInt64(fileObj, "size_96flac");
                if (hiresRaw == 0) hiresRaw = ReadJsonInt64(fileObj, "size_24bit");

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
            [AudioQualityTier.Premium] = GetArrayValue(sizeNew, 1),
            [AudioQualityTier.Atmos] = GetArrayValue(sizeNew, 2),
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
            if (request.Tier == AudioQualityTier.Dolby)
            {
                available = sizeDolby > 0 && hasValidUrl;
            }
            else if (hasFileObj)
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

    private static async Task RefineQualityMetadataAsync(List<QualityOption> options, JsonElement root, string songMid, CancellationToken ct)
    {
        long interval = 0;
        long flacSize = 0;
        if (root.TryGetProperty("songinfo", out var si) &&
            si.TryGetProperty("data", out var sd) &&
            sd.TryGetProperty("track_info", out var ti))
        {
            if (ti.TryGetProperty("interval", out var iv)) iv.TryGetInt64(out interval);
            if (ti.TryGetProperty("file", out var fi) && fi.TryGetProperty("size_flac", out var sf)) sf.TryGetInt64(out flacSize);
        }

        var tasks = new List<Task>();
        for (int i = 0; i < options.Count; i++)
        {
            var index = i;
            var opt = options[index];
            if (!opt.Available) continue;

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var isFlac = AudioQualityHelper.GetExtension(opt.Tier) == ".flac";
                    string? newSpec = null;
                    long realSize = opt.FileSizeBytes;

                    // 1. 优先检查本地磁盘缓存
                    if (!string.IsNullOrEmpty(songMid))
                    {
                        var localCached = AudioCacheService.GetCachedAudioPath(songMid, opt.Tier);
                        if (!string.IsNullOrEmpty(localCached) && File.Exists(localCached))
                        {
                            var fi = new FileInfo(localCached);
                            if (realSize <= 0 && fi.Length > 0)
                            {
                                realSize = fi.Length;
                            }

                            if (isFlac && fi.Length >= 42)
                            {
                                using var fs = File.OpenRead(localCached);
                                var buf = new byte[42];
                                int read = await fs.ReadAsync(buf.AsMemory(0, 42), ct).ConfigureAwait(false);
                                if (read == 42 && AudioQualityHelper.TryParseFlacStreamInfo(buf, out var sr, out var bps, out var ch))
                                {
                                    newSpec = AudioQualityHelper.FormatAudioSpec(sr, bps, ch, opt.Tier);
                                }
                            }
                        }
                    }

                    // 2. 若本地未命中 FLAC 规格且存在播放直链，通过 Range: bytes=0-41 嗅探
                    if (isFlac && newSpec == null && !string.IsNullOrEmpty(opt.PlayUrl))
                    {
                        var probeResult = await TryProbeFlacRangeAsync(opt.PlayUrl, ct).ConfigureAwait(false);
                        if (probeResult != null)
                        {
                            if (probeResult.Value.SampleRate > 0)
                            {
                                newSpec = AudioQualityHelper.FormatAudioSpec(
                                    probeResult.Value.SampleRate,
                                    probeResult.Value.BitsPerSample,
                                    probeResult.Value.Channels,
                                    opt.Tier);
                            }
                            if (realSize <= 0 && probeResult.Value.TotalLength > 0)
                            {
                                realSize = probeResult.Value.TotalLength;
                            }
                        }
                    }

                    // 3. 兜底处理：若仍未获取到有效文件大小且有直链，使用 HEAD 请求探测 Content-Length
                    bool needsSizeCorrection = (opt.Tier == AudioQualityTier.HiRes && (realSize <= 0 || (flacSize > 0 && realSize <= flacSize)))
                                               || realSize <= 0;
                    if (needsSizeCorrection && !string.IsNullOrEmpty(opt.PlayUrl))
                    {
                        var fetchedLength = await TryFetchContentLengthAsync(opt.PlayUrl, ct).ConfigureAwait(false);
                        if (fetchedLength > 0)
                        {
                            realSize = fetchedLength;
                        }
                    }

                    // 4. 组装更新后的 QualityOption
                    var updated = opt;
                    if (realSize > 0 && realSize != opt.FileSizeBytes)
                    {
                        var bitrate = opt.BitrateInfo;
                        if (interval > 0)
                        {
                            bitrate = $"{(long)Math.Round((realSize * 8.0) / interval / 1000.0)}kbps";
                        }
                        updated = updated with { FileSizeBytes = realSize, BitrateInfo = bitrate };
                    }
                    if (!string.IsNullOrEmpty(newSpec) && newSpec != opt.Spec)
                    {
                        updated = updated with { Spec = newSpec };
                    }

                    lock (options)
                    {
                        options[index] = updated;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MusicApi", $"Failed to refine metadata for tier {opt.Tier}: {ex.Message}");
                }
            }, ct));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task<(int SampleRate, int BitsPerSample, int Channels, long TotalLength)?> TryProbeFlacRangeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 41);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            long totalLength = 0;
            if (resp.Content.Headers.ContentRange?.Length is { } len && len > 0)
            {
                totalLength = len;
            }

            var buffer = new byte[42];
            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            int totalRead = 0;
            while (totalRead < 42)
            {
                int r = await stream.ReadAsync(buffer.AsMemory(totalRead, 42 - totalRead), cts.Token).ConfigureAwait(false);
                if (r <= 0) break;
                totalRead += r;
            }

            if (totalRead == 42 && AudioQualityHelper.TryParseFlacStreamInfo(buffer, out var sampleRate, out var bitsPerSample, out var channels))
            {
                return (sampleRate, bitsPerSample, channels, totalLength);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"TryProbeFlacRangeAsync error for {url}: {ex.Message}");
        }
        return null;
    }

    private static async Task<long> TryFetchContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is { } cl && cl > 0)
            {
                return cl;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"Failed to fetch Content-Length for {url}: {ex.Message}");
        }
        return 0;
    }
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
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":28,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
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
            var payload = $"{{\"comm\":{{\"uin\":\"{uin}\",\"format\":\"json\",\"ct\":28,\"cv\":1,\"authst\":\"{authst}\",\"tmeAppID\":\"qqmusic\"}}," +
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
