using System.Text.Json;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

public class ListenTogetherTests
{
    [Fact]
    public void IsLocalOrWebDavSong_IdentifiesCorrectly()
    {
        var onlineSong = new Song("mid_online", "Online Title", "Artist", "Album", 200, "", 1001);
        Assert.False(ListenTogetherManager.IsLocalOrWebDavSong(onlineSong));

        var localSong = new Song("mid_local", "Local Title", "Artist", "Album", 200, "", 1002)
        {
            LocalFilePath = "/var/music/test.flac"
        };
        Assert.True(ListenTogetherManager.IsLocalOrWebDavSong(localSong));

        var webDavSong = new Song("mid_webdav", "WebDav Title", "Artist", "Album", 200, "", 1003)
        {
            WebDavHref = "http://localhost:8080/music/stream.mp3"
        };
        Assert.True(ListenTogetherManager.IsLocalOrWebDavSong(webDavSong));

        Assert.False(ListenTogetherManager.IsLocalOrWebDavSong(null));
    }

    [Theory]
    [InlineData("https://i.y.qq.com/n2/m/share/listen_together/index.html?showid=LUGC12345&songid=987654&share_encrypt_uin=enc_dummy", "LUGC12345", "enc_dummy", 987654L)]
    [InlineData("https://y.qq.com/n2/m/share/listen_together/index.html?showid=LUGC99999&hosteuin=enc_host", "LUGC99999", "enc_host", 0L)]
    [InlineData("LUGC88888888", "LUGC88888888", "", 0L)]
    [InlineData("一起来听歌吧: LUGC77777777 点击进入", "LUGC77777777", "", 0L)]
    [InlineData("", "", "", 0L)]
    [InlineData("   ", "", "", 0L)]
    [InlineData("https://invalid.example.com/test", "", "", 0L)]
    public async Task ResolveListenTogetherShareTarget_ExtractsCorrectly(string input, string expectedShowId, string expectedEncUin, long expectedSongId)
    {
        var (showId, encUin, songId) = await MusicApi.ResolveListenTogetherShareTargetAsync(input);
        Assert.Equal(expectedShowId, showId);
        Assert.Equal(expectedEncUin, encUin);
        Assert.Equal(expectedSongId, songId);
    }

    [Fact]
    public void LeaveRoom_ResetsRoomStateAndFiresEvent()
    {
        var mgr = ListenTogetherManager.Instance;
        mgr.SetRoomForTesting(new ListenTogetherRoomInfo { ShowId = "LUGC_TEST", UsersCount = 2, IsValid = true });
        Assert.True(mgr.IsInRoom);

        bool eventFired = false;
        ListenTogetherRoomInfo? eventRoom = new ListenTogetherRoomInfo();

        Action<ListenTogetherRoomInfo?> handler = r =>
        {
            eventFired = true;
            eventRoom = r;
        };

        mgr.RoomChanged += handler;
        try
        {
            mgr.LeaveRoom();
            Assert.Null(mgr.CurrentRoom);
            Assert.False(mgr.IsInRoom);
            Assert.Null(mgr.SpaceStats);
            Assert.True(eventFired);
            Assert.Null(eventRoom);
        }
        finally
        {
            mgr.RoomChanged -= handler;
        }
    }

    [Fact]
    public void IsSelfCreatedRoom_ChecksShowIdAndEncUin()
    {
        var mgr = ListenTogetherManager.Instance;
        Assert.False(mgr.IsSelfCreatedRoom(""));
        Assert.False(mgr.IsSelfCreatedRoom("   "));
        Assert.False(mgr.IsSelfCreatedRoom("LUGC_NONEXISTENT", "dummy_other_uin"));
    }

    [Fact]
    public void SpaceStats_ModelCalculation_ComputesMinutesCorrectly()
    {
        const string mockJson = """
        {
            "rsp_1": {
                "req_0": {
                    "data": {
                        "Together": {
                            "TogetherData": {
                                "ListenTime": 7500,
                                "ListenNum": 42,
                                "FavoriteSongNum": 15
                            }
                        }
                    }
                }
            }
        }
        """;

        using var doc = JsonDocument.Parse(mockJson);
        var root = doc.RootElement;
        var togetherData = root.GetProperty("rsp_1").GetProperty("req_0").GetProperty("data").GetProperty("Together").GetProperty("TogetherData");

        long listenTimeSec = togetherData.GetProperty("ListenTime").GetInt64();
        int listenSongsCount = togetherData.GetProperty("ListenNum").GetInt32();
        int favSongsCount = togetherData.GetProperty("FavoriteSongNum").GetInt32();

        var stats = new ListenTogetherSpaceStats
        {
            ListenMinutes = Math.Max(0, listenTimeSec / 60),
            ListenSongsCount = listenSongsCount,
            FavoriteSongsCount = favSongsCount
        };

        Assert.Equal(125, stats.ListenMinutes);
        Assert.Equal(42, stats.ListenSongsCount);
        Assert.Equal(15, stats.FavoriteSongsCount);
    }

    [Fact]
    public void SingleUserRoom_SpaceStats_IsNullByDefault()
    {
        var mgr = ListenTogetherManager.Instance;
        mgr.SetRoomForTesting(new ListenTogetherRoomInfo { ShowId = "LUGC_SINGLE", UsersCount = 1, IsValid = true });
        try
        {
            Assert.True(mgr.IsInRoom);
            Assert.Equal(1, mgr.CurrentRoom?.UsersCount);
            Assert.Null(mgr.SpaceStats);
        }
        finally
        {
            mgr.LeaveRoom();
        }
    }
}
