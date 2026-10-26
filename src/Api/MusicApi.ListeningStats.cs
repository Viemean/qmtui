using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

/// <summary>
/// 用户听歌统计数据。
/// </summary>
/// <param name="TodayListenTimeSec">今日累计收听总秒数。</param>
/// <param name="TotalListenDays">历史累计收听天数。</param>
public sealed record ListeningStatsResult(
    int TodayListenTimeSec,
    int TotalListenDays
);

public sealed partial class MusicApi
{
    /// <summary>
    /// 获取当前登录用户的听歌时长统计数据。
    /// </summary>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>若拉取成功返回 <see cref="ListeningStatsResult"/>；未登录或请求失败则返回 <see langword="null"/>。</returns>
    public static async Task<ListeningStatsResult?> GetListeningStatsAsync(CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            return null;
        }

        try
        {
            var todayStr = DateTime.Now.ToString("yyyyMMdd");
            var uin = UserSession.Current.Uin;
            var uinNum = long.TryParse(uin, out var val) ? val : 0L;

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
                    "uin": {{uinNum}}
                  },
                  "req_bar": {
                    "module": "music.individuation.ListenReportSvr",
                    "method": "GetBar",
                    "param": {
                      "type": 1,
                      "date": "{{todayStr}}",
                      "optionType": 1,
                      "version": "v2"
                    }
                  }
                }
                """;

            var responseJson = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("req_bar", out var reqBar) &&
                reqBar.TryGetProperty("data", out var data))
            {
                int todaySec = 0;
                int totalDays = 0;

                if (data.TryGetProperty("today", out var today) &&
                    today.TryGetProperty("listenTime", out var lt) &&
                    lt.TryGetProperty("listenTimeSec", out var secElem) &&
                    secElem.ValueKind == JsonValueKind.Number)
                {
                    todaySec = secElem.GetInt32();
                }

                if (data.TryGetProperty("total", out var total) &&
                    total.TryGetProperty("day", out var dayElem) &&
                    dayElem.ValueKind == JsonValueKind.Number)
                {
                    totalDays = dayElem.GetInt32();
                }

                return new ListeningStatsResult(todaySec, totalDays);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi.ListeningStats", $"GetListeningStatsAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 拉取指定周期（日/周/月/年）与日期的综合听歌足迹报告与排行榜单。
    /// </summary>
    /// <param name="period">统计周期维度。</param>
    /// <param name="date">目标日期字符串（为空表示拉取当前最新）。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>解析后的综合报告数据对象，失败时返回兜底空数据。</returns>
    public static async Task<ListeningReportData> GetListeningRecordReportAsync(
        ListeningReportPeriod period = ListeningReportPeriod.Day,
        string? date = null,
        CancellationToken ct = default)
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            return new ListeningReportData(period, date ?? "", "", 0L, 0, 0, 0, 0, [], [], [], [], [], []);
        }

        try
        {
            var uin = UserSession.Current.Uin;
            var uinNum = long.TryParse(uin, out var parsedUin) ? parsedUin : 0L;

            string barParam = string.IsNullOrWhiteSpace(date)
                ? $$"""{ "type": {{(int)period}}, "optionType": 1, "version": "v2" }"""
                : $$"""{ "type": {{(int)period}}, "date": "{{date}}", "optionType": 1, "version": "v2" }""";

            string rankParam = string.IsNullOrWhiteSpace(date)
                ? $$"""{ "type": {{(int)period}}, "version": "v2" }"""
                : $$"""{ "type": {{(int)period}}, "date": "{{date}}", "version": "v2" }""";

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
                    "uin": {{uinNum}}
                  },
                  "req_bar": {
                    "module": "music.individuation.ListenReportSvr",
                    "method": "GetBar",
                    "param": {{barParam}}
                  },
                  "req_rank": {
                    "module": "music.individuation.ListenReportSvr",
                    "method": "GetRank",
                    "param": {{rankParam}}
                  }
                }
                """;

            var responseJson = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            var barData = root.TryGetProperty("req_bar", out var rb) && rb.TryGetProperty("data", out var bd)
                ? bd
                : default;
            var rankData = root.TryGetProperty("req_rank", out var rr) && rr.TryGetProperty("data", out var rd)
                ? rd
                : default;

            List<string> availableDates = [];
            if (barData.ValueKind == JsonValueKind.Object &&
                barData.TryGetProperty("availableDates", out var adElem) &&
                adElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in adElem.EnumerateArray())
                {
                    var s = d.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        availableDates.Add(s);
                    }
                }
            }

            string dateKey = !string.IsNullOrWhiteSpace(date)
                ? date
                : (availableDates.Count > 0 ? availableDates[0] : DateTime.Now.ToString("yyyyMMdd"));

            long listenTimeSec = 0L;
            int listenCnt = 0;
            int activeHour = 0;
            int percent = 0;
            int upOrDown = 0;
            string title = "";

            JsonElement activePeriodObj = default;
            if (barData.ValueKind == JsonValueKind.Object)
            {
                if (period == ListeningReportPeriod.Day && barData.TryGetProperty("today", out var td))
                {
                    activePeriodObj = td;
                }
                else if (period == ListeningReportPeriod.Week && barData.TryGetProperty("week", out var wk))
                {
                    activePeriodObj = wk;
                }
                else if (barData.TryGetProperty("bar", out var b))
                {
                    activePeriodObj = b;
                }
            }

            JsonElement listenTimeElem = default;
            if (activePeriodObj.ValueKind == JsonValueKind.Object && activePeriodObj.TryGetProperty("listenTime", out var ltObj))
            {
                listenTimeElem = ltObj;
            }
            else if (barData.ValueKind == JsonValueKind.Object && barData.TryGetProperty("listenTime", out var rootLt))
            {
                listenTimeElem = rootLt;
            }

            if (activePeriodObj.ValueKind == JsonValueKind.Object && activePeriodObj.TryGetProperty("title", out var tElem))
            {
                title = tElem.GetString() ?? "";
            }

            if (listenTimeElem.ValueKind == JsonValueKind.Object)
            {
                if (listenTimeElem.TryGetProperty("listenTimeSec", out var secE) && secE.TryGetInt64(out var sVal))
                {
                    listenTimeSec = sVal;
                }
                if (listenTimeElem.TryGetProperty("listenCnt", out var cntE) && cntE.TryGetInt32(out var cVal))
                {
                    listenCnt = cVal;
                }
                if (listenTimeElem.TryGetProperty("activeHour", out var ahE) && ahE.TryGetInt32(out var ahVal))
                {
                    activeHour = ahVal;
                }
                if (listenTimeElem.TryGetProperty("percent", out var pE))
                {
                    double pRaw = 0;
                    if (pE.TryGetDouble(out var pd)) pRaw = pd;
                    else if (pE.TryGetInt32(out var pi)) pRaw = pi;

                    percent = pRaw switch
                    {
                        <= 0 => 0,
                        <= 1.0 => (int)Math.Round(pRaw * 100),
                        <= 10.0 => (int)Math.Round(pRaw * 100),
                        _ => (int)Math.Round(pRaw)
                    };
                }
                if (listenTimeElem.TryGetProperty("upOrDown", out var udE) && udE.TryGetInt32(out var udVal))
                {
                    upOrDown = udVal;
                }
            }

            List<ListeningChartPoint> chartPoints = [];
            if (activePeriodObj.ValueKind == JsonValueKind.Object &&
                activePeriodObj.TryGetProperty("chart", out var chartObj) &&
                chartObj.TryGetProperty("chartInfo", out var chartInfo) &&
                chartInfo.ValueKind == JsonValueKind.Object)
            {
                if (period == ListeningReportPeriod.Day)
                {
                    for (int h = 0; h < 24; h++)
                    {
                        string slotKey = h.ToString();
                        long ptVal = 0;
                        long avg = 0;
                        if (chartInfo.TryGetProperty(slotKey, out var pt))
                        {
                            if (pt.TryGetProperty("value", out var vE) && vE.TryGetInt64(out var v)) ptVal = v;
                            if (pt.TryGetProperty("avgValue", out var aE) && aE.TryGetInt64(out var a)) avg = a;
                        }
                        chartPoints.Add(new ListeningChartPoint(slotKey, ptVal, avg, $"{h:D2}:00"));
                    }
                }
                else
                {
                    foreach (var prop in chartInfo.EnumerateObject())
                    {
                        long ptVal = 0;
                        long avg = 0;
                        if (prop.Value.TryGetProperty("value", out var vE) && vE.TryGetInt64(out var v)) ptVal = v;
                        if (prop.Value.TryGetProperty("avgValue", out var aE) && aE.TryGetInt64(out var a)) avg = a;
                        chartPoints.Add(new ListeningChartPoint(prop.Name, ptVal, avg, prop.Name));
                    }
                }
            }

            if (chartPoints.Count == 0 &&
                barData.ValueKind == JsonValueKind.Object &&
                barData.TryGetProperty("bar", out var barBlock) &&
                barBlock.TryGetProperty("optionResults", out var optResMap) &&
                optResMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in optResMap.EnumerateObject())
                {
                    long ptVal = 0;
                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
                    {
                        var first = prop.Value[0];
                        if (first.TryGetProperty("value", out var vE) && vE.TryGetInt64(out var v)) ptVal = v;
                    }
                    string label = prop.Name;
                    if (period == ListeningReportPeriod.Month && label.Length == 6 && int.TryParse(label[4..6], out var mNum))
                    {
                        label = $"{mNum}月";
                    }
                    chartPoints.Add(new ListeningChartPoint(prop.Name, ptVal, 0, label));
                }
            }

            List<ListeningSingerStat> topSingers = [];
            JsonElement singersElem = default;
            if (activePeriodObj.ValueKind == JsonValueKind.Object && activePeriodObj.TryGetProperty("singers", out var sList))
            {
                singersElem = sList;
            }
            else if (barData.ValueKind == JsonValueKind.Object && barData.TryGetProperty("singers", out var sList2))
            {
                singersElem = sList2;
            }

            if (singersElem.ValueKind == JsonValueKind.Array && singersElem.GetArrayLength() > 0)
            {
                int rankIdx = 1;
                foreach (var sItem in singersElem.EnumerateArray())
                {
                    long sId = sItem.TryGetProperty("id", out var idE) && idE.TryGetInt64(out var id) ? id : 0L;
                    string sName = sItem.TryGetProperty("name", out var nE) ? nE.GetString() ?? "" : "";
                    string sCover = sItem.TryGetProperty("cover", out var cE) ? cE.GetString() ?? "" : "";
                    long sSec = sItem.TryGetProperty("listenTimeSec", out var secE) && secE.TryGetInt64(out var sec) ? sec : 0L;

                    if (!string.IsNullOrWhiteSpace(sName))
                    {
                        topSingers.Add(new ListeningSingerStat(sId, sName, sCover, sSec, rankIdx++));
                    }
                }
            }
            else if (barData.ValueKind == JsonValueKind.Object &&
                     barData.TryGetProperty("optionResults", out var optRes) &&
                     optRes.ValueKind == JsonValueKind.Array)
            {
                int rankIdx = 1;
                foreach (var sItem in optRes.EnumerateArray())
                {
                    long sId = sItem.TryGetProperty("id", out var idE) && idE.TryGetInt64(out var id) ? id : 0L;
                    string sName = sItem.TryGetProperty("name", out var nE) ? nE.GetString() ?? "" : "";
                    string sCover = sItem.TryGetProperty("singerAvatar", out var cE) ? cE.GetString() ?? "" : "";
                    long sSec = sItem.TryGetProperty("value", out var valE) && valE.TryGetInt64(out var v) ? v : 0L;

                    if (!string.IsNullOrWhiteSpace(sName))
                    {
                        topSingers.Add(new ListeningSingerStat(sId, sName, sCover, sSec, rankIdx++));
                    }
                }
            }

            List<ListeningHighlight> highLights = [];
            if (barData.ValueKind == JsonValueKind.Object &&
                barData.TryGetProperty("highLights", out var hlArray) &&
                hlArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var hl in hlArray.EnumerateArray())
                {
                    string hlTitle = hl.TryGetProperty("title", out var htE) ? htE.GetString() ?? "" : "";
                    string hlSub = hl.TryGetProperty("subtitle", out var hsE) ? hsE.GetString() ?? "" : "";
                    string hlTips = hl.TryGetProperty("tips", out var hpE) ? hpE.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(hlTitle))
                    {
                        highLights.Add(new ListeningHighlight(hlTitle, hlSub, hlTips));
                    }
                }
            }

            List<ListeningRankSong> rankSongs = [];
            if (rankData.ValueKind == JsonValueKind.Object &&
                rankData.TryGetProperty("rank", out var rankArray) &&
                rankArray.ValueKind == JsonValueKind.Array)
            {
                int rankNo = 1;
                foreach (var rItem in rankArray.EnumerateArray())
                {
                    var trackElem = rItem.TryGetProperty("track", out var tr) ? tr : rItem;
                    var song = ParseSongFromElement(trackElem);
                    int cnt = rItem.TryGetProperty("listenCnt", out var cntE) && cntE.TryGetInt32(out var c) ? c : 0;

                    if (song != null)
                    {
                        rankSongs.Add(new ListeningRankSong(song, rankNo++, cnt));
                    }
                }
            }

            List<ListeningMonthFavoriteSong> favoriteSongs = [];
            if (barData.ValueKind == JsonValueKind.Object &&
                barData.TryGetProperty("SongOfMonth", out var somObj) &&
                somObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in somObj.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out var mNum))
                    {
                        var song = ParseSongFromElement(prop.Value);
                        if (song != null)
                        {
                            favoriteSongs.Add(new ListeningMonthFavoriteSong(mNum, song));
                        }
                    }
                }
                favoriteSongs.Sort((a, b) => a.Month.CompareTo(b.Month));
            }

            return new ListeningReportData(
                Period: period,
                DateKey: dateKey,
                Title: title,
                ListenTimeSec: listenTimeSec,
                ListenCnt: listenCnt,
                ActiveHour: activeHour,
                Percent: percent,
                UpOrDown: upOrDown,
                ChartPoints: chartPoints,
                TopSingers: topSingers,
                RankSongs: rankSongs,
                AvailableDates: availableDates,
                HighLights: highLights,
                FavoriteSongsOfMonth: favoriteSongs
            );
        }
        catch (OperationCanceledException)
        {
            return new ListeningReportData(period, date ?? "", "", 0L, 0, 0, 0, 0, [], [], [], [], [], []);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi.ListeningStats", $"GetListeningRecordReportAsync exception: {ex.Message}");
            return new ListeningReportData(period, date ?? "", "", 0L, 0, 0, 0, 0, [], [], [], [], [], []);
        }
    }
}

/// <summary>
/// 听歌足迹统计周期维度。
/// </summary>
public enum ListeningReportPeriod
{
    Day = 1,
    Week = 2,
    Month = 3,
    Year = 4
}

/// <summary>
/// 听歌走势单个时段/点位数据。
/// </summary>
public sealed record ListeningChartPoint(
    string Slot,
    long Value,
    long AvgValue,
    string Text
);

/// <summary>
/// 常听歌手统计项。
/// </summary>
public sealed record ListeningSingerStat(
    long SingerId,
    string SingerName,
    string SingerCoverUrl,
    long ListenTimeSec,
    int Rank
);

/// <summary>
/// 周期内歌曲收听排行榜项。
/// </summary>
public sealed record ListeningRankSong(
    Song Song,
    int RankNo,
    int ListenCnt
);

/// <summary>
/// 听歌足迹高光时刻项。
/// </summary>
public sealed record ListeningHighlight(
    string Title,
    string Subtitle,
    string Tips
);

/// <summary>
/// 每月最爱歌曲统计项。
/// </summary>
public sealed record ListeningMonthFavoriteSong(
    int Month,
    Song Song
);

/// <summary>
/// 综合听歌足迹报告数据。
/// </summary>
public sealed record ListeningReportData(
    ListeningReportPeriod Period,
    string DateKey,
    string Title,
    long ListenTimeSec,
    int ListenCnt,
    int ActiveHour,
    int Percent,
    int UpOrDown,
    IReadOnlyList<ListeningChartPoint> ChartPoints,
    IReadOnlyList<ListeningSingerStat> TopSingers,
    IReadOnlyList<ListeningRankSong> RankSongs,
    IReadOnlyList<string> AvailableDates,
    IReadOnlyList<ListeningHighlight> HighLights,
    IReadOnlyList<ListeningMonthFavoriteSong> FavoriteSongsOfMonth
);
