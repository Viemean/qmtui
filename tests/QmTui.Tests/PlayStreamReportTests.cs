using Xunit;
using QmTui.Api;
using QmTui.Services;

namespace QmTui.Tests;

public class PlayStreamReportTests
{
    [Fact]
    public void GetDeviceInfo_ReturnsValidCompliantProfile()
    {
        var info1 = DeviceReportService.GetDeviceInfo();
        Assert.NotNull(info1);
        Assert.False(string.IsNullOrWhiteSpace(info1.Model));
        Assert.Equal(16, info1.AndroidId.Length);
        Assert.InRange(info1.AndroidId[0], '1', 'z');
        Assert.NotEqual('0', info1.AndroidId[0]);
        Assert.Equal(40, info1.OpenUdid.Length);
        Assert.Equal("14", info1.OsVersion);
        Assert.Equal(34, info1.SdkInt);

        var info2 = DeviceReportService.GetDeviceInfo();
        Assert.Same(info1, info2);
    }

    [Fact]
    public void RotateDeviceProfile_GeneratesNewValidProfile()
    {
        var original = DeviceReportService.GetDeviceInfo();
        var rotated = DeviceReportService.RotateDeviceProfile();
        Assert.NotNull(rotated);
        Assert.Equal(16, rotated.AndroidId.Length);
        Assert.InRange(rotated.AndroidId[0], '1', 'z');
        Assert.NotEqual('0', rotated.AndroidId[0]);
        Assert.Equal(40, rotated.OpenUdid.Length);
        Assert.NotEqual("d9a53a1bfc03fa68", rotated.AndroidId);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(123456, 0)]
    [InlineData(123456, 4)]
    [InlineData(123456, -10)]
    public async Task ReportPlayStreamAsync_InvalidParameters_ReturnsFalseImmediately(long songId, int playTimeSec)
    {
        bool result = await MusicApi.ReportPlayStreamAsync(songId, 200, playTimeSec);
        Assert.False(result);
    }

    [Fact]
    [Trait("Category", "PlatformApi")]
    public async Task GetListeningStatsAsync_WithLocalSession_ReturnsValidStats()
    {
        if (Environment.GetEnvironmentVariable("QMTUI_ONLINE_TEST") != "1") return;

        Models.UserSession.Load();
        if (!Models.UserSession.Current.IsLoggedIn) return;

        var stats = await MusicApi.GetListeningStatsAsync();
        Assert.NotNull(stats);
        Assert.True(stats.TodayListenTimeSec > 0);
    }

    [Fact]
    [Trait("Category", "PlatformApi")]
    public async Task ReportPlayStreamAsync_OnlineIntegration_SuccessfullyReportsAndCloudIncrements()
    {
        if (Environment.GetEnvironmentVariable("QMTUI_ONLINE_TEST") != "1") return;

        Models.UserSession.Load();
        if (!Models.UserSession.Current.IsLoggedIn) return;

        var beforeStats = await MusicApi.GetListeningStatsAsync();
        Assert.NotNull(beforeStats);

        bool reportResult = await MusicApi.ReportPlayStreamAsync(347032530, 299, 15);
        Assert.True(reportResult);

        bool incremented = false;
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(3000);
            var afterStats = await MusicApi.GetListeningStatsAsync();
            if (afterStats != null && afterStats.TodayListenTimeSec >= beforeStats.TodayListenTimeSec + 15)
            {
                incremented = true;
                break;
            }
        }

        Assert.True(incremented);
    }
}
