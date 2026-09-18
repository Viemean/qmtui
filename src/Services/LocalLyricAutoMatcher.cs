using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services.AudioRecognition;
using QmTui.Utils;

namespace QmTui.Services;

public sealed record CachedLyricLineItem(
    [property: JsonPropertyName("ms")] long TimestampMs,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("trans")] string Trans = ""
)
{
    public LyricLine ToDomain() => new(TimeSpan.FromMilliseconds(TimestampMs), Text, Trans);
    public static CachedLyricLineItem FromDomain(LyricLine line) =>
        new((long)line.Timestamp.TotalMilliseconds, line.Text, line.Trans ?? "");
}

public sealed record CachedMatchedLyricData(
    [property: JsonPropertyName("songMid")] string SongMid,
    [property: JsonPropertyName("albumMid")] string AlbumMid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("lines")] List<CachedLyricLineItem> Lines
);

[JsonSerializable(typeof(CachedMatchedLyricData))]
[JsonSerializable(typeof(List<CachedLyricLineItem>))]
[JsonSerializable(typeof(CachedLyricLineItem))]
internal sealed partial class MatchedLyricJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 本地、WebDAV 与在线音乐统一智能歌词匹配与多级共享缓存调度器
/// 采用 SongMid 权威母本库 + 物理路径快表 + 元数据软索引三级架构，
/// 支持在线、本地与 WebDAV 三方数据无缝互通，并提供 GStreamer 声学指纹 (ACR) 高精度识别通道。
/// </summary>
public static class LocalLyricAutoMatcher
{
    private static readonly string s_lyricsDir = Path.Combine(AppPathHelper.CacheDir, "lyrics");

    static LocalLyricAutoMatcher()
    {
        try
        {
            if (!Directory.Exists(s_lyricsDir))
            {
                Directory.CreateDirectory(s_lyricsDir);
            }
        }
        catch { }
    }

    /// <summary>
    /// 判断当前歌曲是否需要进行智能匹配
    /// </summary>
    public static bool NeedsMatching(Song song, List<LyricLine> currentLyrics)
    {
        bool isPlaceholder = currentLyrics.Count == 0 ||
            (currentLyrics.Count == 1 && (currentLyrics[0].Text.Contains("暂无歌词") || string.IsNullOrWhiteSpace(currentLyrics[0].Text)));
        if (isPlaceholder) return true;

        // 若已有内嵌歌词且已有翻译，跳过
        if (currentLyrics.Exists(l => !string.IsNullOrWhiteSpace(l.Trans))) return false;

        // 若为外文歌曲但缺少翻译，允许匹配官方双语歌词
        return LyricParser.NeedsTranslation(currentLyrics);
    }

    /// <summary>
    /// 异步执行本地/WebDAV 歌曲匹配（优先多级离线缓存，未命中则走 ACR 声学切片识别）
    /// </summary>
    public static async Task<(Song MatchedSong, List<LyricLine> Lyrics)?> MatchLyricsAsync(
        Song song,
        string? playUrl,
        List<LyricLine> currentLyrics,
        bool forceMatch = false,
        CancellationToken cancellationToken = default)
    {
        if ((!forceMatch && !NeedsMatching(song, currentLyrics)) || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // 1. 优先检索本地统一多级缓存 (SongMid 母本 -> 路径快表 -> 元数据软索引)
        var cached = TryGetCachedLyrics(song, playUrl);
        if (cached != null && cached.Lines.Count > 0)
        {
            var domainLyrics = cached.Lines.ConvertAll(l => l.ToDomain());
            var dummySong = song with
            {
                Mid = cached.SongMid,
                AlbumMid = cached.AlbumMid,
                Title = string.IsNullOrWhiteSpace(cached.Title) ? song.Title : cached.Title,
                Artist = string.IsNullOrWhiteSpace(cached.Artist) ? song.Artist : cached.Artist
            };
            AppLogger.Info("LocalLyricAutoMatcher", $"[缓存命中] 成功加载统一歌词母本: {cached.Title} - {cached.Artist} (Mid: {cached.SongMid})");
            return (dummySong, domainLyrics);
        }

        // 2. 定位有效的本地/已缓存音频文件或 WebDAV 流式 URL
        string? audioPathOrUrl = null;
        if (song.IsLocal && !string.IsNullOrEmpty(song.LocalFilePath) && File.Exists(song.LocalFilePath))
        {
            audioPathOrUrl = song.LocalFilePath;
        }
        else if (song.IsWebDav)
        {
            if (!string.IsNullOrEmpty(song.LocalFilePath) && File.Exists(song.LocalFilePath))
            {
                audioPathOrUrl = song.LocalFilePath;
            }
            else
            {
                var server = WebDavService.GetActiveServer();
                if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
                {
                    var localCache = WebDavService.GetLocalCachePath(server, song.WebDavHref);
                    if (File.Exists(localCache) && new FileInfo(localCache).Length > 4096)
                    {
                        audioPathOrUrl = localCache;
                    }
                }

                if (string.IsNullOrEmpty(audioPathOrUrl))
                {
                    if (!string.IsNullOrEmpty(playUrl) && (playUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || playUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        audioPathOrUrl = playUrl;
                    }
                    else if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
                    {
                        audioPathOrUrl = WebDavService.BuildStreamingUriWithAuth(server, song.WebDavHref);
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(audioPathOrUrl))
        {
            return null;
        }

        try
        {
            // 3. 本地/WebDAV 音频切片声学指纹识别 (避开前奏静音，提取 15s~23s)
            string logTarget = audioPathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? song.Title : Path.GetFileName(audioPathOrUrl);
            AppLogger.Info("LocalLyricAutoMatcher", $"开始执行 GStreamer 音频切片声学识别: {logTarget}");
            var pcmSamples = await AudioSliceDecoder.ExtractSlicePcmAsync(audioPathOrUrl, song.Duration, cancellationToken).ConfigureAwait(false);
            if (pcmSamples == null || pcmSamples.Length < 16000 * 2)
            {
                AppLogger.Warn("LocalLyricAutoMatcher", $"音频切片提取失败或样本不足: {logTarget}");
                return null;
            }

            var acrResult = await AudioRecognitionService.RecognizeAndMatchPcmAsync(pcmSamples, cancellationToken).ConfigureAwait(false);
            if (!acrResult.Success || acrResult.MatchedSong == null || string.IsNullOrWhiteSpace(acrResult.MatchedSong.Mid))
            {
                AppLogger.Info("LocalLyricAutoMatcher", $"ACR 声学识别未命中官方曲目: {acrResult.ErrorMessage}");
                return null;
            }

            var matchedSong = acrResult.MatchedSong;
            AppLogger.Info("LocalLyricAutoMatcher", $"[ACR 识别成功] 命中官方曲目: {matchedSong.Title} - {matchedSong.Artist} (Mid: {matchedSong.Mid})");

            // 4. 优先检查识别到的 SongMid 是否已存在于本地母本缓存（例如在线播放时已缓存），若命中则无需重复网络请求
            var midCached = ReadMasterCacheByMid(matchedSong.Mid);
            List<LyricLine>? officialLyrics = null;
            if (midCached != null && midCached.Lines.Count > 0)
            {
                officialLyrics = midCached.Lines.ConvertAll(l => l.ToDomain());
                AppLogger.Info("LocalLyricAutoMatcher", $"[母本复用] 直接命中已缓存的官方双语歌词: {matchedSong.Mid}");
            }
            else
            {
                // 若本地尚无母本，拉取官方双语歌词
                officialLyrics = await MusicApi.GetLyricsAsync(matchedSong.Mid, cancellationToken).ConfigureAwait(false);
            }

            if (officialLyrics == null || officialLyrics.Count == 0 ||
                (officialLyrics.Count == 1 && officialLyrics[0].Text.Contains("暂无歌词")))
            {
                return null;
            }

            // 防负优化检查：若原歌词已有内容，而官方歌词没有提供双语翻译，则保留原歌词（用户强制匹配时不拦截）
            bool newHasTrans = officialLyrics.Exists(l => !string.IsNullOrWhiteSpace(l.Trans));
            if (!forceMatch && currentLyrics.Count > 1 && !newHasTrans)
            {
                AppLogger.Info("LocalLyricAutoMatcher", "官方歌词未包含翻译，保留现有歌词");
                return null;
            }

            // 5. 将结果持久化至统一歌词母本库，并建立路径与元数据关联索引
            SaveUnifiedCache(matchedSong.Mid, matchedSong.AlbumMid, matchedSong.Title, matchedSong.Artist, officialLyrics, song, playUrl);

            return (matchedSong, officialLyrics);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LocalLyricAutoMatcher", $"ACR 匹配异常: {ex.Message}");
            return null;
        }
    }

    #region 统一多级缓存存取

    public static CachedMatchedLyricData? TryGetCachedLyrics(Song song, string? playUrl = null)
    {
        // 1. 若具有合法官方 SongMid，直接查询母本
        if (!string.IsNullOrWhiteSpace(song.Mid) &&
            !song.Mid.StartsWith("local_", StringComparison.OrdinalIgnoreCase) &&
            !song.Mid.StartsWith("webdav_", StringComparison.OrdinalIgnoreCase))
        {
            var byMid = ReadMasterCacheByMid(song.Mid);
            if (byMid != null) return byMid;
        }

        // 2. 物理路径快表查询
        string? pathTarget = !string.IsNullOrEmpty(song.LocalFilePath)
            ? song.LocalFilePath
            : (!string.IsNullOrEmpty(song.WebDavHref) ? song.WebDavHref : playUrl);

        if (!string.IsNullOrEmpty(pathTarget))
        {
            string pathKey = ComputePathKey(pathTarget);
            string pathIndexFile = Path.Combine(s_lyricsDir, $"path_{pathKey}.txt");
            if (File.Exists(pathIndexFile))
            {
                try
                {
                    string targetMid = File.ReadAllText(pathIndexFile, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(targetMid))
                    {
                        var byPathMid = ReadMasterCacheByMid(targetMid);
                        if (byPathMid != null) return byPathMid;
                    }
                }
                catch { }
            }
        }

        // 3. 元数据软索引查询 (CleanTitle + CleanArtist + 时长档)
        if (!string.IsNullOrWhiteSpace(song.Title))
        {
            string metaKey = ComputeMetaKey(song.Title, song.Artist, song.Duration);
            string metaIndexFile = Path.Combine(s_lyricsDir, $"meta_{metaKey}.txt");
            if (File.Exists(metaIndexFile))
            {
                try
                {
                    string targetMid = File.ReadAllText(metaIndexFile, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(targetMid))
                    {
                        var byMetaMid = ReadMasterCacheByMid(targetMid);
                        if (byMetaMid != null) return byMetaMid;
                    }
                }
                catch { }
            }
        }

        return null;
    }

    public static CachedMatchedLyricData? ReadMasterCacheByMid(string songMid)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;

        try
        {
            string masterFile = Path.Combine(s_lyricsDir, $"mid_{songMid}.json");
            if (File.Exists(masterFile))
            {
                string json = File.ReadAllText(masterFile, Encoding.UTF8);
                return JsonSerializer.Deserialize(json, MatchedLyricJsonContext.Default.CachedMatchedLyricData);
            }
        }
        catch { }

        return null;
    }

    public static void SaveUnifiedCache(
        string songMid,
        string albumMid,
        string title,
        string artist,
        List<LyricLine> lyrics,
        Song? song = null,
        string? playUrl = null)
    {
        if (string.IsNullOrWhiteSpace(songMid) || lyrics == null || lyrics.Count == 0) return;

        try
        {
            // 1. 写入 SongMid 母本库
            string masterFile = Path.Combine(s_lyricsDir, $"mid_{songMid}.json");
            var items = lyrics.ConvertAll(CachedLyricLineItem.FromDomain);
            var data = new CachedMatchedLyricData(songMid, albumMid, title, artist, items);
            string json = JsonSerializer.Serialize(data, MatchedLyricJsonContext.Default.CachedMatchedLyricData);
            string tmp = masterFile + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, masterFile, overwrite: true);

            // 2. 建立物理路径快表
            string? pathTarget = song != null && !string.IsNullOrEmpty(song.LocalFilePath)
                ? song.LocalFilePath
                : (song != null && !string.IsNullOrEmpty(song.WebDavHref) ? song.WebDavHref : playUrl);

            if (!string.IsNullOrEmpty(pathTarget))
            {
                string pathKey = ComputePathKey(pathTarget);
                string pathIndexFile = Path.Combine(s_lyricsDir, $"path_{pathKey}.txt");
                File.WriteAllText(pathIndexFile, songMid, Encoding.UTF8);
            }

            // 3. 建立元数据软索引
            string effectiveTitle = !string.IsNullOrWhiteSpace(title) ? title : (song?.Title ?? "");
            string effectiveArtist = !string.IsNullOrWhiteSpace(artist) ? artist : (song?.Artist ?? "");
            double effectiveDuration = song != null && song.Duration > 0 ? song.Duration : 0;

            if (!string.IsNullOrWhiteSpace(effectiveTitle))
            {
                string metaKey = ComputeMetaKey(effectiveTitle, effectiveArtist, effectiveDuration);
                string metaIndexFile = Path.Combine(s_lyricsDir, $"meta_{metaKey}.txt");
                File.WriteAllText(metaIndexFile, songMid, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LocalLyricAutoMatcher", $"SaveUnifiedCache failed for {songMid}: {ex.Message}");
        }
    }

    internal static string ComputePathKey(string pathOrHref)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(pathOrHref.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeMetaKey(string title, string artist, double durationSeconds)
    {
        var cleanTitle = WebDavService.CleanTrackNumberPrefix(title).Trim().ToLowerInvariant();
        var cleanArtist = (string.IsNullOrWhiteSpace(artist) || artist == "未知歌手") ? "" : artist.Trim().ToLowerInvariant();
        int bucket = durationSeconds > 0 ? (int)Math.Round(durationSeconds / 5.0) * 5 : 0;
        string raw = $"{cleanTitle}|{cleanArtist}|{bucket}";
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #endregion
}
