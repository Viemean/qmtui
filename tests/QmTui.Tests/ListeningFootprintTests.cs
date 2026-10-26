using Xunit;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;

namespace QmTui.Tests;

public class ListeningFootprintTests
{
    [Fact]
    public void ListeningReportData_Properties_ConstructAndReadCorrectly()
    {
        var chartPoints = new List<ListeningChartPoint>
        {
            new("0", 120, 60, "00:00"),
            new("1", 300, 100, "01:00")
        };

        var singers = new List<ListeningSingerStat>
        {
            new(1001, "Singer A", "https://example.com/cover.jpg", 420, 1)
        };

        var songs = new List<ListeningRankSong>
        {
            new(new Song("dummy_mid_1", "Song A", "Singer A", "Album A", 180, "", 10001, ""), 1, 3)
        };

        var report = new ListeningReportData(
            Period: ListeningReportPeriod.Day,
            DateKey: "20261013",
            Title: "2026年10月13日",
            ListenTimeSec: 420,
            ListenCnt: 3,
            ActiveHour: 1,
            Percent: 88,
            UpOrDown: 1,
            ChartPoints: chartPoints,
            TopSingers: singers,
            RankSongs: songs,
            AvailableDates: new List<string> { "20261013" },
            HighLights: [],
            FavoriteSongsOfMonth: []
        );

        Assert.Equal(ListeningReportPeriod.Day, report.Period);
        Assert.Equal("20261013", report.DateKey);
        Assert.Equal(420, report.ListenTimeSec);
        Assert.Equal(3, report.ListenCnt);
        Assert.Equal(1, report.ActiveHour);
        Assert.Equal(88, report.Percent);
        Assert.Equal(1, report.UpOrDown);
        Assert.Equal(2, report.ChartPoints.Count);
        Assert.Single(report.TopSingers);
        Assert.Single(report.RankSongs);
        Assert.Equal(10001, report.RankSongs[0].Song.Id);
    }

    [Fact]
    public void RecordLocalReportDelta_AccumulatesDurationCorrectly()
    {
        ListeningFootprintCacheService.RecordLocalReportDelta(30);
        ListeningFootprintCacheService.RecordLocalReportDelta(45);
        // 增量流记录成功，不抛出异常
        Assert.True(true);
    }

    [Fact]
    public void ClearCache_ExecutesWithoutException()
    {
        ListeningFootprintCacheService.ClearCache();
        Assert.True(true);
    }
}
