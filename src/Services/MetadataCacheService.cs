using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 本地元数据与歌词轻量缓存服务（基于 Native AOT 强类型序列化，零反射开销）
/// </summary>
public static class MetadataCacheService
{
    private static readonly string s_metadataDir = Path.Combine(AppPathHelper.CacheDir, "metadata");
    private static readonly string s_lyricsDir = Path.Combine(AppPathHelper.CacheDir, "lyrics");
    private static readonly Lock s_fileLock = new();

    static MetadataCacheService()
    {
        try
        {
            Directory.CreateDirectory(s_metadataDir);
            Directory.CreateDirectory(s_lyricsDir);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MetadataCacheService", $"Init cache directories failed: {ex.Message}");
        }
    }

    #region 每日推荐与百万收藏快照缓存

    public static DailyRecommendCache? GetDailyRecommend(string uin, string date) =>
        GetDailyDatedSongsCache("daily", uin, date);

    public static void SaveDailyRecommend(string uin, string date, List<Song> songs) =>
        SaveDailyDatedSongsCache("daily", uin, date, songs);

    public static DailyRecommendCache? GetMillionRecommend(string uin, string date) =>
        GetDailyDatedSongsCache("million", uin, date);

    public static void SaveMillionRecommend(string uin, string date, List<Song> songs) =>
        SaveDailyDatedSongsCache("million", uin, date, songs);

    private static DailyRecommendCache? GetDailyDatedSongsCache(string category, string uin, string date)
    {
        if (string.IsNullOrWhiteSpace(uin) || string.IsNullOrWhiteSpace(date)) return null;

        var path = Path.Combine(s_metadataDir, $"{category}_{uin}_{date}.json");
        lock (s_fileLock)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                var cache = JsonSerializer.Deserialize(json, AppJsonContext.Default.DailyRecommendCache);
                if (cache != null && cache.Date == date && cache.Songs.Count > 0)
                {
                    return cache;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MetadataCacheService", $"Read {category} recommend cache failed for {date}: {ex.Message}");
            }
        }
        return null;
    }

    public static void SaveDailyDatedSongsCache(string category, string uin, string date, List<Song> songs)
    {
        if (string.IsNullOrWhiteSpace(uin) || string.IsNullOrWhiteSpace(date) || songs.Count == 0) return;

        var targetPath = Path.Combine(s_metadataDir, $"{category}_{uin}_{date}.json");
        var cache = new DailyRecommendCache
        {
            Date = date,
            Uin = uin,
            Songs = songs
        };

        lock (s_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(cache, AppJsonContext.Default.DailyRecommendCache);
                var tmpPath = targetPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, targetPath, overwrite: true);

                // 清理历史日期的旧快照缓存
                CleanupOutdatedDailySnapshots(category, uin, date);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MetadataCacheService", $"Save {category} recommend cache failed: {ex.Message}");
            }
        }
    }

    private static void CleanupOutdatedDailySnapshots(string category, string uin, string currentDate)
    {
        try
        {
            var prefix = $"{category}_{uin}_";
            foreach (var file in Directory.GetFiles(s_metadataDir, $"{prefix}*.json"))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.Equals($"{category}_{uin}_{currentDate}.json", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 忽略非关键清理异常
        }
    }

    #endregion

    #region “我的喜欢”歌单缓存

    public static FavoriteCache? GetFavoriteCache(string uin)
    {
        if (string.IsNullOrWhiteSpace(uin)) return null;

        var path = Path.Combine(s_metadataDir, $"favorites_{uin}.json");
        lock (s_fileLock)
        {
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.FavoriteCache);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MetadataCacheService", $"Read favorites cache failed for uin {uin}: {ex.Message}");
            }
        }
        return null;
    }

    public static void SaveFavoriteCache(string uin, int totalCount, string firstSongMid, List<Song> songs)
    {
        if (string.IsNullOrWhiteSpace(uin)) return;

        var targetPath = Path.Combine(s_metadataDir, $"favorites_{uin}.json");
        var cache = new FavoriteCache
        {
            Uin = uin,
            TotalCount = totalCount,
            FirstSongMid = firstSongMid,
            LastSyncTime = DateTime.UtcNow,
            Songs = songs
        };

        lock (s_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(cache, AppJsonContext.Default.FavoriteCache);
                var tmpPath = targetPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, targetPath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MetadataCacheService", $"Save favorites cache failed: {ex.Message}");
            }
        }
    }

    #endregion

    #region 歌词不可变缓存

    public static List<LyricLine>? GetLyrics(string songMid)
    {
        if (string.IsNullOrWhiteSpace(songMid)) return null;

        var path = Path.Combine(s_lyricsDir, $"{songMid}.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var lyrics = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListLyricLine);
            if (lyrics != null && lyrics.Count > 0 && lyrics[0].Text != "暂无歌词")
            {
                return lyrics;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MetadataCacheService", $"Read lyric cache failed for {songMid}: {ex.Message}");
        }
        return null;
    }

    public static void SaveLyrics(string songMid, List<LyricLine> lyrics)
    {
        if (string.IsNullOrWhiteSpace(songMid) || lyrics == null || lyrics.Count == 0) return;
        if (lyrics.Count == 1 && lyrics[0].Text == "暂无歌词") return;

        var targetPath = Path.Combine(s_lyricsDir, $"{songMid}.json");
        try
        {
            var json = JsonSerializer.Serialize(lyrics, AppJsonContext.Default.ListLyricLine);
            var tmpPath = targetPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MetadataCacheService", $"Save lyric cache failed for {songMid}: {ex.Message}");
        }
    }

    #endregion
}
