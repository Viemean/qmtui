using System.Collections.Concurrent;
using System.Text.Json;
using QmTui.Api;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 听歌足迹双级缓存与数据管理服务。
/// </summary>
public static class ListeningFootprintCacheService
{
    private const long RecentDataTtlMs = 60_000L;

    private static readonly string s_cacheDir = Path.Combine(AppPathHelper.CacheDir, "footprint");
    private static readonly ConcurrentDictionary<string, ListeningReportData> s_memoryCache = new();
    private static readonly ConcurrentDictionary<string, long> s_fetchTimeMap = new();
    private static readonly ConcurrentDictionary<ListeningReportPeriod, List<string>> s_availableDatesByPeriod = new();

    private static readonly Lock s_deltaLock = new();
    private static readonly List<(string DateKey, int PlaySec, long TimestampMs)> s_localDeltas = [];
    private static long s_todayBaselineFetchTimeMs;
    private static long s_lastDisplayedTodaySec;

    static ListeningFootprintCacheService()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ListeningFootprintCacheService", $"Init cache directory failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 登记本地实时流式播放上报的增量秒数，供今日足迹进行乐观累加。
    /// </summary>
    /// <param name="deltaSec">上报有效秒数。</param>
    public static void RecordLocalReportDelta(int deltaSec)
    {
        if (deltaSec <= 0) return;

        string todayKey = DateTime.Now.ToString("yyyyMMdd");
        long nowMs = Environment.TickCount64;

        lock (s_deltaLock)
        {
            s_localDeltas.Add((todayKey, deltaSec, nowMs));
            // 仅保留近 2 小时内的增量流水
            if (s_localDeltas.Count > 100)
            {
                s_localDeltas.RemoveAll(d => nowMs - d.TimestampMs > 7_200_000);
            }
        }
    }

    /// <summary>
    /// 异步获取指定周期与日期的听歌足迹报告。
    /// </summary>
    /// <param name="period">统计周期维度。</param>
    /// <param name="date">目标日期代码（为空表示拉取当期最新）。</param>
    /// <param name="forceRefresh">是否强制绕过内存缓存拉取最新数据。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>综合听歌足迹报告对象。</returns>
    public static async Task<ListeningReportData> GetStatsAsync(
        ListeningReportPeriod period,
        string? date = null,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            return new ListeningReportData(period, date ?? "", "", 0L, 0, 0, 0, 0, [], [], [], [], [], []);
        }

        string dateKey = !string.IsNullOrWhiteSpace(date) ? date : "latest";
        string cacheKey = $"{(int)period}_{dateKey}";
        bool isHistorical = IsHistoricalPeriod(period, date);

        // 非强制刷新时优先命中 L1 内存缓存
        if (!forceRefresh && s_memoryCache.TryGetValue(cacheKey, out var memReport))
        {
            if (isHistorical)
            {
                return memReport;
            }

            if (s_fetchTimeMap.TryGetValue(cacheKey, out long lastFetch) &&
                Environment.TickCount64 - lastFetch < RecentDataTtlMs)
            {
                return memReport;
            }
        }

        // 历史周期优先命中 L2 磁盘持久化缓存
        if (!forceRefresh && isHistorical)
        {
            var diskReport = LoadFromDisk(cacheKey);
            if (diskReport != null)
            {
                s_memoryCache[cacheKey] = diskReport;
                return diskReport;
            }
        }

        // 回源云端 RPC 请求
        var rawReport = await MusicApi.GetListeningRecordReportAsync(period, date, ct).ConfigureAwait(false);

        // 聚合与排序可用日期集合
        var knownDates = s_availableDatesByPeriod.GetOrAdd(period, _ => []);
        lock (knownDates)
        {
            foreach (var d in rawReport.AvailableDates)
            {
                if (!string.IsNullOrWhiteSpace(d) && !knownDates.Contains(d))
                {
                    knownDates.Add(d);
                }
            }
            knownDates.Sort((a, b) => string.CompareOrdinal(b, a));
        }

        var mergedAvailableDates = knownDates.Count > 0 ? knownDates.ToArray() : rawReport.AvailableDates;

        // 今日日视图应用乐观更新与单调递增保障
        string todayStr = DateTime.Now.ToString("yyyyMMdd");
        bool isTodayView = period == ListeningReportPeriod.Day && (string.IsNullOrWhiteSpace(date) || date == todayStr);

        ListeningReportData finalReport;
        if (isTodayView)
        {
            long fetchTime = Environment.TickCount64;
            s_todayBaselineFetchTimeMs = fetchTime;

            int uncommittedSec = 0;
            int uncommittedCnt = 0;
            lock (s_deltaLock)
            {
                var uncommitted = s_localDeltas.Where(d => d.DateKey == todayStr && d.TimestampMs > fetchTime).ToList();
                uncommittedSec = uncommitted.Sum(d => d.PlaySec);
                uncommittedCnt = uncommitted.Count;
            }

            long finalSec = Math.Max(rawReport.ListenTimeSec + uncommittedSec, s_lastDisplayedTodaySec);
            s_lastDisplayedTodaySec = finalSec;

            int finalCnt = rawReport.ListenCnt + uncommittedCnt;

            finalReport = rawReport with
            {
                AvailableDates = mergedAvailableDates,
                ListenTimeSec = finalSec,
                ListenCnt = finalCnt
            };
        }
        else
        {
            finalReport = rawReport with
            {
                AvailableDates = mergedAvailableDates
            };
        }

        // 写入双级缓存
        s_memoryCache[cacheKey] = finalReport;
        s_fetchTimeMap[cacheKey] = Environment.TickCount64;

        if (isHistorical && finalReport.ListenTimeSec > 0)
        {
            SaveToDisk(cacheKey, finalReport);
        }

        return finalReport;
    }

    /// <summary>
    /// 清除所有内存缓存与本地持久化足迹快照。
    /// </summary>
    public static void ClearCache()
    {
        s_memoryCache.Clear();
        s_fetchTimeMap.Clear();
        s_availableDatesByPeriod.Clear();
        lock (s_deltaLock)
        {
            s_localDeltas.Clear();
        }
        s_todayBaselineFetchTimeMs = 0L;
        s_lastDisplayedTodaySec = 0L;

        try
        {
            if (Directory.Exists(s_cacheDir))
            {
                foreach (var file in Directory.GetFiles(s_cacheDir, "*.json"))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ListeningFootprintCacheService", $"Failed to delete disk cache files: {ex.Message}");
        }
    }

    private static bool IsHistoricalPeriod(ListeningReportPeriod period, string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return false;

        var now = DateTime.Now;
        return period switch
        {
            ListeningReportPeriod.Day => string.CompareOrdinal(date, now.ToString("yyyyMMdd")) < 0,
            ListeningReportPeriod.Week => string.CompareOrdinal(date, GetCurrentMondayString(now)) < 0,
            ListeningReportPeriod.Month => string.CompareOrdinal(date, now.ToString("yyyyMM")) < 0,
            ListeningReportPeriod.Year => string.CompareOrdinal(date, now.Year.ToString()) < 0,
            _ => false
        };
    }

    private static string GetCurrentMondayString(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-1 * diff).ToString("yyyyMMdd");
    }

    private static ListeningReportData? LoadFromDisk(string cacheKey)
    {
        try
        {
            string filePath = Path.Combine(s_cacheDir, $"{cacheKey}.json");
            if (!File.Exists(filePath)) return null;

            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.ListeningReportData);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ListeningFootprintCacheService", $"LoadFromDisk failed for {cacheKey}: {ex.Message}");
            return null;
        }
    }

    private static void SaveToDisk(string cacheKey, ListeningReportData report)
    {
        try
        {
            string filePath = Path.Combine(s_cacheDir, $"{cacheKey}.json");
            string json = JsonSerializer.Serialize(report, AppJsonContext.Default.ListeningReportData);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ListeningFootprintCacheService", $"SaveToDisk failed for {cacheKey}: {ex.Message}");
        }
    }
}
