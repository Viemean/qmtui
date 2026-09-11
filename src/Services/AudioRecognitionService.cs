using System.Diagnostics;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services.AudioRecognition;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 听歌识曲结果
/// </summary>
public record RecognitionResult(
    bool Success,
    string Title,
    string Artist,
    string Album,
    Song? MatchedSong = null,
    string ErrorMessage = "",
    string Source = "Native",
    double OffsetSeconds = 0.0
);

/// <summary>
/// 音频识别与曲库联动服务
/// </summary>
public static class AudioRecognitionService
{
    /// <summary>
    /// 识别本地音频文件，并联动检索曲库
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本文件不存在");
        }

        try
        {
            // 纯原生声学算法引擎直接识别 PCM 样本
            if (audioFilePath.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase))
            {
                byte[] rawBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
                short[] pcm8k = new short[rawBytes.Length / 2];
                Buffer.BlockCopy(rawBytes, 0, pcm8k, 0, rawBytes.Length);

                if (pcm8k.Length >= 8000 * 2)
                {
                    var feature = AcousticFingerprintExtractor.Extract(pcm8k);
                    if (feature != null)
                    {
                        var res = await AcousticRecognizeClient.SearchAsync(feature, cancellationToken);
                        if (res.Success && res.Song != null)
                        {
                            AppLogger.Force("AudioRecognitionService", $"[原生命中] 文件匹配: {res.Song.Title} - {res.Song.Artist}");
                            return new RecognitionResult(true, res.Song.Title, res.Song.Artist, res.Song.Album, res.Song, "", "原生", res.OffsetSeconds);
                        }
                    }
                }
            }

            return new RecognitionResult(false, "", "", "", null, "原生引擎未匹配到歌曲");
        }
        catch (OperationCanceledException)
        {
            return new RecognitionResult(false, "", "", "", null, "识别已取消");
        }
        catch (Exception ex)
        {
            return new RecognitionResult(false, "", "", "", null, $"识别服务异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 预热听歌识曲网络连接
    /// </summary>
    public static void PreWarm()
    {
        _ = AcousticRecognitionService.IsAvailable;
        _ = AcousticRecognizeClient.PreWarmAsync();
    }

    /// <summary>
    /// 识别 16000Hz PCM 采样切片并联动曲库 (仅使用原生声学算法引擎)
    /// </summary>
    public static async Task<RecognitionResult> RecognizeAndMatchPcmAsync(
        short[] pcmSamples, 
        CancellationToken cancellationToken = default)
    {
        if (pcmSamples == null || pcmSamples.Length < (int)(16000 * 1.5))
        {
            return new RecognitionResult(false, "", "", "", null, "音频样本过短，请等待累积更多音频");
        }

        try
        {
            // 纯原生声学算法引擎直接识别
            var officialRes = await AcousticRecognitionService.RecognizePcmSamplesAsync(pcmSamples, cancellationToken);
            if (officialRes != null && officialRes.Success && officialRes.MatchedSong != null)
            {
                AppLogger.Force("AudioRecognitionService", $"[原生命中] 匹配成功: {officialRes.MatchedSong.Title} - {officialRes.MatchedSong.Artist} (Mid: {officialRes.MatchedSong.Mid})");
                return officialRes with { Source = "原生" };
            }

            return new RecognitionResult(false, "", "", "", null, "原生声学引擎正在分析或未匹配");
        }
        catch (OperationCanceledException)
        {
            return new RecognitionResult(false, "", "", "", null, "识别已取消");
        }
        catch (Exception ex)
        {
            return new RecognitionResult(false, "", "", "", null, $"识别服务异常: {ex.Message}");
        }
    }

    private static async Task<RecognitionResult> MatchWithCatalogAsync(string title, string artist, string album)
    {
        // 1. 构建检索词 (提取声优、角色名、专辑组合)
        var searchQueries = BuildSearchQueries(title, artist, album);
        List<Song>? searchSongs = null;

        foreach (var query in searchQueries)
        {
            var results = await MusicApi.SearchAsync(query, 1, 15);
            if (results != null && results.Count > 0)
            {
                searchSongs = results;
                // 若包含声优或完整歌手匹配，优先停在该列表
                break;
            }
        }

        Song? matchedSong = null;
        if (searchSongs != null && searchSongs.Count > 0)
        {
            matchedSong = FindBestMatchedSong(title, artist, album, searchSongs);
        }

        return new RecognitionResult(true, title, artist, album, matchedSong, "");
    }

    private static List<string> BuildSearchQueries(string title, string artist, string album)
    {
        var queries = new List<string>();

        // 1. 若含有 (CV: xxx) 或 [CV: xxx]，提取声优名优先检索 (如 "星めぐりの歌 宮本侑芽")
        string cvName = ExtractCvName(artist);
        if (!string.IsNullOrWhiteSpace(cvName))
        {
            queries.Add($"{title} {cvName}");
        }

        // 2. 原始完整检索
        if (!string.IsNullOrWhiteSpace(artist))
        {
            queries.Add($"{title} {artist}");
        }

        // 3. 净化后的歌手名 (去除括号备注如 feat./CV)
        string cleanArtist = CleanArtistName(artist);
        if (!string.IsNullOrWhiteSpace(cleanArtist) && cleanArtist != artist && cleanArtist != cvName)
        {
            queries.Add($"{title} {cleanArtist}");
        }

        // 4. 带核心专辑名检索 (如 "星めぐりの歌 死亡遊戯で飯を食う。")
        string cleanAlbum = CleanAlbumName(album);
        if (!string.IsNullOrWhiteSpace(cleanAlbum))
        {
            queries.Add($"{title} {cleanAlbum}");
        }

        // 5. 兜底纯歌名检索
        queries.Add(title);

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ExtractCvName(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(
            artist,
            @"[\(\[（]CV\s*[:：]\s*(?<name>[^\)\]）]+)[\)\]）]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value.Trim() : "";
    }

    private static string CleanArtistName(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(artist, @"[\(\[（].*?[\)\]）]", "").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s*(feat\.|ft\.).*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static string CleanAlbumName(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return "";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(album, @"[\(\[（].*?[\)\]）]", "").Trim();
        cleaned = cleaned.Replace("『", "").Replace("』", "").Replace("「", "").Replace("」", "").Trim();
        return cleaned;
    }

    public static Song? FindBestMatchedSong(string targetTitle, string targetArtist, string targetAlbum, List<Song> candidates)
    {
        string cvName = ExtractCvName(targetArtist);
        string cleanArtist = CleanArtistName(targetArtist);
        string cleanAlbum = CleanAlbumName(targetAlbum);

        Song? bestSong = null;
        int maxScore = 0;

        foreach (var song in candidates)
        {
            int score = 0;

            // 歌名匹配
            bool titleExact = string.Equals(song.Title, targetTitle, StringComparison.OrdinalIgnoreCase);
            bool titleContains = song.Title.Contains(targetTitle, StringComparison.OrdinalIgnoreCase) ||
                                 targetTitle.Contains(song.Title, StringComparison.OrdinalIgnoreCase);

            if (titleExact) score += 40;
            else if (titleContains) score += 25;

            // 歌手匹配 (包括 CV 声优名、净化歌手名、原歌手名)
            if (!string.IsNullOrWhiteSpace(cvName) && song.Artist.Contains(cvName, StringComparison.OrdinalIgnoreCase))
            {
                score += 50; // 声优匹配
            }
            else if (!string.IsNullOrWhiteSpace(cleanArtist) && song.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
            else if (!string.IsNullOrWhiteSpace(targetArtist) && song.Artist.Contains(targetArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            // 专辑匹配
            if (!string.IsNullOrWhiteSpace(targetAlbum) && !string.IsNullOrWhiteSpace(song.Album))
            {
                if (song.Album.Contains(targetAlbum, StringComparison.OrdinalIgnoreCase) ||
                    targetAlbum.Contains(song.Album, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                else if (!string.IsNullOrWhiteSpace(cleanAlbum) && song.Album.Contains(cleanAlbum, StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                }
            }

            if (score > maxScore)
            {
                maxScore = score;
                bestSong = song;
            }
        }

        // 歌手或专辑需要有匹配度 (score >= 65)，才允许匹配；避免同名翻唱误匹配
        if (maxScore >= 65)
        {
            return bestSong;
        }

        if (string.IsNullOrWhiteSpace(targetArtist) && maxScore >= 40)
        {
            return bestSong;
        }

        return null;
    }
}
