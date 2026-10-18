using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using Xunit;

namespace QmTui.Tests;

public class OfficialAppTokenRefreshTests
{
    [Fact]
    public async Task EnsureMusicKeyAsync_WithValidExistingKey_ReturnsTrueWithoutNetwork()
    {
        var originalCookies = UserSession.Current.Cookies;
        var originalKey = UserSession.Current.MusicKey;
        try
        {
            UserSession.Current.Cookies = new Dictionary<string, string>
            {
                ["qm_keyst"] = "valid_test_musickey"
            };
            UserSession.Current.MusicKey = "valid_test_musickey";

            var result = await LoginService.EnsureMusicKeyAsync(forceRefresh: false, CancellationToken.None);
            Assert.True(result);
        }
        finally
        {
            UserSession.Current.Cookies = originalCookies;
            UserSession.Current.MusicKey = originalKey;
        }
    }

    [Fact]
    public async Task RefreshOfficialAppTokenAsync_WithInvalidMusicId_ReturnsFalse()
    {
        var result = await LoginService.RefreshOfficialAppTokenAsync(
            musicId: "invalid_alpha_uin",
            refreshToken: "dummy_refresh_token",
            refreshKey: "dummy_refresh_key",
            ct: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task RefreshOfficialAppTokenAsync_WithEmptyMusicId_ReturnsFalse()
    {
        var result = await LoginService.RefreshOfficialAppTokenAsync(
            musicId: "",
            refreshToken: "dummy_refresh_token",
            refreshKey: "dummy_refresh_key",
            ct: CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task EnsureMusicKeyAsync_WithRefreshToken_CatchesNetworkFailureGracefully()
    {
        var originalCookies = UserSession.Current.Cookies;
        var originalUin = UserSession.Current.Uin;
        var originalKey = UserSession.Current.MusicKey;

        try
        {
            UserSession.Current.Uin = "10001";
            UserSession.Current.MusicKey = "";
            UserSession.Current.Cookies = new Dictionary<string, string>
            {
                ["uin"] = "10001",
                ["refresh_token"] = "mock_invalid_refresh_token",
                ["refresh_key"] = "mock_refresh_key",
                ["tmeLoginType"] = "2"
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, cts.Token);
            Assert.False(result);
        }
        finally
        {
            UserSession.Current.Uin = originalUin;
            UserSession.Current.MusicKey = originalKey;
            UserSession.Current.Cookies = originalCookies;
        }
    }

    [Fact]
    [Trait("Category", "LocalSessionProbe")]
    public async Task ProbeLocalSessionRenewAsync()
    {
        UserSession.Load();
        if (!UserSession.Current.IsLoggedIn)
        {
            return;
        }

        var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, CancellationToken.None);
        Assert.True(renewed);
        Assert.False(string.IsNullOrEmpty(UserSession.Current.MusicKey));
    }
}
