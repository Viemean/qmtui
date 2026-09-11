using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;
using Xunit;

namespace QmTui.Tests;

public class UserConfigTests
{
    [Fact]
    public void UserConfig_DefaultState_NotificationEnabled()
    {
        var config = new UserConfig();
        Assert.True(config.EnableSongSwitchNotification);
    }

    [Fact]
    public void UserConfig_SerializationRoundtrip_MaintainsValues()
    {
        var original = new UserConfig { EnableSongSwitchNotification = false };
        var json = JsonSerializer.Serialize(original, AppJsonContext.Default.UserConfig);

        Assert.Contains("\"enable_song_switch_notification\": false", json);

        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.UserConfig);
        Assert.NotNull(restored);
        Assert.False(restored.EnableSongSwitchNotification);
    }

    [Fact]
    public void UserSession_LastPlayedSong_PreservesAlbumMidAndExtraProperties()
    {
        var session = new UserSession
        {
            LastPlayedSong = new Song("mid_test", "Test Title", "Test Artist", "Test Album", 210, "media_mid_test", 8888, "album_mid_test")
            {
                LocalFilePath = "/music/local.mp3",
                WebDavHref = "/remote/song.flac",
                WebDavServerId = "server_abc"
            }
        };

        var json = session.ToJson();
        Assert.Contains("\"album_mid\":\"album_mid_test\"", json);
        Assert.Contains("\"local_file_path\":\"/music/local.mp3\"", json);
        Assert.Contains("\"webdav_href\":\"/remote/song.flac\"", json);
        Assert.Contains("\"webdav_server_id\":\"server_abc\"", json);
    }
}
