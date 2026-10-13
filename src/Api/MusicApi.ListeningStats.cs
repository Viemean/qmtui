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
}
