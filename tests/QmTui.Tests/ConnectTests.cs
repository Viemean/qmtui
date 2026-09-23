using System.Text.Json;
using QmTui.Connect.Discovery;
using QmTui.Connect.Models;
using QmTui.Connect.Server;
using QmTui.Connect.Storage;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;
using Xunit;

namespace QmTui.Tests;

public class ConnectTests
{
    [Fact]
    public void ConnectMessage_Serialization_Roundtrip()
    {
        var msg = new ConnectMessage(
            Action: ConnectActions.CmdPause,
            Payload: "",
            Id: "test-id-123"
        );

        var json = JsonSerializer.Serialize(msg, ConnectJsonContext.Default.ConnectMessage);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.ConnectMessage);

        Assert.NotNull(deserialized);
        Assert.Equal(ConnectActions.CmdPause, deserialized.Action);
        Assert.Equal("test-id-123", deserialized.Id);
    }

    [Fact]
    public void PlaySongCommand_Serialization_Roundtrip()
    {
        var song = new ConnectSong(
            SongId: 1001,
            SongMid: "002uOeP94TmyOw",
            Name: "ハレロ (紅組)",
            Singer: "22/7",
            Album: "ハレロ",
            DurationSeconds: 242,
            CurrentTier: "HiRes"
        );

        var cmd = new PlaySongCommand(
            Song: song,
            Queue: [song],
            Index: 0,
            StartPositionMs: 15000,
            QualityTier: "HiRes"
        );

        var json = JsonSerializer.Serialize(cmd, ConnectJsonContext.Default.PlaySongCommand);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.PlaySongCommand);

        Assert.NotNull(deserialized);
        Assert.Equal("002uOeP94TmyOw", deserialized.Song.SongMid);
        Assert.Equal(15000, deserialized.StartPositionMs);
        Assert.Equal("HiRes", deserialized.QualityTier);

        var domainSong = deserialized.Song.ToDomainSong();
        Assert.Equal("ハレロ (紅組)", domainSong.Title);
        Assert.Equal("22/7", domainSong.Artist);
        Assert.Equal(242, domainSong.Duration);

        // 测试 AudioSource STREAM_PROXY 与 CoverUrl 解析
        var streamProxyCmd = new PlaySongCommand(
            Song: song,
            AudioSource: new AudioSourceDescriptor(
                SourceType: AudioSourceType.STREAM_PROXY,
                StreamUrl: "http://192.168.1.50:8766/stream/webdav?server=srv1&href=test.flac"
            )
        );
        var proxyJson = JsonSerializer.Serialize(streamProxyCmd, ConnectJsonContext.Default.PlaySongCommand);
        var deserializedProxy = JsonSerializer.Deserialize(proxyJson, ConnectJsonContext.Default.PlaySongCommand);
        Assert.NotNull(deserializedProxy?.AudioSource);
        Assert.Equal(AudioSourceType.STREAM_PROXY, deserializedProxy.AudioSource.SourceType);
        Assert.Equal("http://192.168.1.50:8766/stream/webdav?server=srv1&href=test.flac", deserializedProxy.AudioSource.StreamUrl);

        // 测试从 Domain Song 生成 ConnectSong 时的封面 URL 自动拼接
        var songWithAlbum = new Song("mid1", "Title", "Artist", "Album", 180, AlbumMid: "001albMid");
        var connectSong = ConnectSong.FromDomainSong(songWithAlbum);
        Assert.Equal("https://y.gtimg.cn/music/photo_new/T002R800x800M000001albMid.jpg?max_age=2592000", connectSong.CoverUrl);
    }

    [Fact]
    public void PairRequestPayload_Serialization_Roundtrip()
    {
        var dev = new ConnectDevice(
            Id: "client-uuid-1",
            Name: "Melodist Mobile (Pixel)",
            Type: ConnectDeviceType.MOBILE,
            Host: "192.168.1.150",
            Port: 8765,
            Token: "token-secret"
        );

        var req = new PairRequestPayload(
            Device: dev,
            PinCode: "1234"
        );

        var json = JsonSerializer.Serialize(req, ConnectJsonContext.Default.PairRequestPayload);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.PairRequestPayload);

        Assert.NotNull(deserialized);
        Assert.Equal("1234", deserialized.PinCode);
        Assert.Equal("client-uuid-1", deserialized.Device.Id);
        Assert.Equal(ConnectDeviceType.MOBILE, deserialized.Device.Type);
    }

    [Fact]
    public void PlayerStateEvent_Serialization_Roundtrip()
    {
        var song = new ConnectSong(
            SongId: 2002,
            SongMid: "000qiToY03CMRk",
            Name: "妄想感傷代償連盟",
            Singer: "DECO*27",
            DurationSeconds: 215,
            CurrentTier: "HiRes"
        );

        var evt = new PlayerStateEvent(
            CurrentSong: song,
            IsPlaying: true,
            PositionMs: 42000,
            DurationMs: 215000,
            Volume: 0.8f,
            QueueSize: 1,
            CurrentIndex: 0,
            LoopMode: "ListLoop",
            CurrentTier: "HiRes",
            IsFavorite: true
        );

        var json = JsonSerializer.Serialize(evt, ConnectJsonContext.Default.PlayerStateEvent);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.PlayerStateEvent);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsPlaying);
        Assert.Equal(42000, deserialized.PositionMs);
        Assert.Equal("HiRes", deserialized.CurrentTier);
        Assert.Equal("妄想感傷代償連盟", deserialized.CurrentSong?.Name);
    }

    [Fact]
    public void ConnectStorage_PinCodeAndDevicePersistence()
    {
        var storage = new ConnectStorage();
        Assert.NotEmpty(storage.LocalDeviceId);
        Assert.NotEmpty(storage.LocalToken);
        Assert.NotEmpty(storage.CurrentPinCode);

        var pin = storage.GenerateNewPinCode();
        Assert.Equal(6, pin.Length);
        Assert.True(int.TryParse(pin, out _));

        var dev = new ConnectDevice(
            Id: "test-dev-999",
            Name: "Test Phone",
            Type: ConnectDeviceType.MOBILE,
            Token: "token-999"
        );

        storage.SavePairedDevice(dev);
        Assert.True(storage.IsDeviceTrusted("test-dev-999", "token-999"));
        Assert.False(storage.IsDeviceTrusted("test-dev-999", "wrong-token"));

        storage.RemovePairedDevice("test-dev-999");
        Assert.False(storage.IsDeviceTrusted("test-dev-999", "token-999"));
    }

    [Fact]
    public void QrPairData_Serialization_And_Encoder()
    {
        var qrData = new QrPairData(
            Version: 1,
            DeviceId: "tv-uuid-1234",
            DeviceName: "QMTUI Linux",
            Host: "192.168.1.100",
            Port: 8765,
            Token: "token-tv",
            PinCode: "839201"
        );

        var json = JsonSerializer.Serialize(qrData, ConnectJsonContext.Default.QrPairData);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.QrPairData);

        Assert.NotNull(deserialized);
        Assert.Equal("839201", deserialized.PinCode);
        Assert.Equal("192.168.1.100", deserialized.Host);
        Assert.Equal(8765, deserialized.Port);

        var blockLines = QrCodeEncoder.EncodeToBlockText(json, QrCodeEncoder.EccLevel.L);
        Assert.NotEmpty(blockLines);

        var matrix = QrCodeEncoder.GenerateMatrix(json, QrCodeEncoder.EccLevel.L);
        int size = matrix.GetLength(0);
        int pad = 4;
        int w = size + pad * 2;
        int h = size + pad * 2;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)255); // 白底
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (matrix[r, c]) // 黑块
                {
                    int idx = ((r + pad) * w + (c + pad)) * 3;
                    rgb[idx] = 0;
                    rgb[idx + 1] = 0;
                    rgb[idx + 2] = 0;
                }
            }
        }
        var lumSource = new ZXing.RGBLuminanceSource(rgb, w, h);
        var reader = new ZXing.QrCode.QRCodeReader();
        var result = reader.decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(lumSource)));
        Assert.NotNull(result);
        Assert.Equal(json, result.Text);
    }

    [Fact]
    public void GestureSwipeCommand_Serialization_Roundtrip()
    {
        var cmd = new GestureSwipeCommand("left");
        var json = JsonSerializer.Serialize(cmd, ConnectJsonContext.Default.GestureSwipeCommand);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.GestureSwipeCommand);

        Assert.NotNull(deserialized);
        Assert.Equal("left", deserialized.Direction);
    }

    [Fact]
    public void PlayerStateEvent_InactiveState_SerializesNullSongCorrectly()
    {
        var evt = new PlayerStateEvent(
            CurrentSong: null,
            IsPlaying: false,
            PositionMs: 0,
            DurationMs: 0,
            Volume: 0.8f,
            QueueSize: 0,
            CurrentIndex: -1,
            LoopMode: "ListRepeat",
            IsAodActive: false
        );

        var json = JsonSerializer.Serialize(evt, ConnectJsonContext.Default.PlayerStateEvent);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.PlayerStateEvent);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.CurrentSong);
        Assert.False(deserialized.IsPlaying);
        Assert.Equal(0, deserialized.PositionMs);
        Assert.Equal(-1, deserialized.CurrentIndex);
    }

    [Fact]
    public void ResolveCoverUrl_LocalAndWebDav_ReturnsPerMidEndpoint()
    {
        var localSong = new Song("local_123456", "Local Song", "Local Artist", "Local Album", 200)
        {
            LocalFilePath = "/home/music/test.mp3"
        };
        var localUrl = ConnectSong.ResolveCoverUrl(localSong);
        Assert.StartsWith("http://", localUrl);
        Assert.Contains(":8765/cover?mid=local_123456", localUrl);

        var webdavSong = new Song("webdav_srv1_hash", "WebDAV Song", "WebDAV Artist", "WebDAV Album", 200)
        {
            WebDavHref = "/music/webdav_song.flac",
            WebDavServerId = "srv1"
        };
        var webdavUrl = ConnectSong.ResolveCoverUrl(webdavSong);
        Assert.StartsWith("http://", webdavUrl);
        Assert.Contains(":8765/cover?mid=webdav_srv1_hash", webdavUrl);
    }

    [Fact]
    public void ConnectMessage_DecodeData_SupportsBothDataAndPayload()
    {
        var seekCmd = new SeekCommand(PositionMs: 45000);

        // 1. 验证仅包含 data (JsonElement) 时能够正确解析 (Melodist Kotlin 默认行为)
        var msgWithData = ConnectMessage.Create(ConnectActions.CmdSeek, seekCmd, ConnectJsonContext.Default.SeekCommand);
        // 模拟清空 payload 仅保留 data
        var jsonWithOnlyData = $"{{\"action\":\"{ConnectActions.CmdSeek}\",\"data\":{{\"positionMs\":45000}}}}";
        var parsedOnlyData = JsonSerializer.Deserialize(jsonWithOnlyData, ConnectJsonContext.Default.ConnectMessage);
        Assert.NotNull(parsedOnlyData);
        var decodedFromData = parsedOnlyData.DecodeData(ConnectJsonContext.Default.SeekCommand);
        Assert.NotNull(decodedFromData);
        Assert.Equal(45000, decodedFromData.PositionMs);

        // 2. 验证仅包含 payload (字符串) 时的向后兼容解析
        var jsonWithOnlyPayload = $"{{\"action\":\"{ConnectActions.CmdSeek}\",\"payload\":\"{{\\\"positionMs\\\":45000}}\"}}";
        var parsedOnlyPayload = JsonSerializer.Deserialize(jsonWithOnlyPayload, ConnectJsonContext.Default.ConnectMessage);
        Assert.NotNull(parsedOnlyPayload);
        var decodedFromPayload = parsedOnlyPayload.DecodeData(ConnectJsonContext.Default.SeekCommand);
        Assert.NotNull(decodedFromPayload);
        Assert.Equal(45000, decodedFromPayload.PositionMs);

        // 3. 验证通过 ConnectMessage.Create 生成的消息同时包含 data 与 payload
        Assert.NotNull(msgWithData.Data);
        Assert.NotEmpty(msgWithData.Payload);
        Assert.Contains("45000", msgWithData.Payload);
    }

    [Fact]
    public void LyricsSyncPayload_Serialization_Roundtrip()
    {
        var lines = new List<ConnectLyricLine>
        {
            new(0, "ハレロ"),
            new(3500, "青空を見上げて", "仰望蔚蓝天空")
        };
        var payload = new LyricsSyncPayload(
            SongMid: "002uOeP94TmyOw",
            Title: "ハレロ",
            Singer: "22/7",
            Lyrics: lines,
            SourceDeviceId: "tv-uuid",
            Timestamp: 123456789,
            LyricOffsetMs: 150
        );

        var json = JsonSerializer.Serialize(payload, ConnectJsonContext.Default.LyricsSyncPayload);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.LyricsSyncPayload);

        Assert.NotNull(deserialized);
        Assert.Equal("002uOeP94TmyOw", deserialized.SongMid);
        Assert.Equal("ハレロ", deserialized.Title);
        Assert.Equal(2, deserialized.Lyrics?.Count);
        Assert.Equal("仰望蔚蓝天空", deserialized.Lyrics?[1].TransText);
        Assert.Equal(150, deserialized.LyricOffsetMs);
    }

    [Fact]
    public void GestureSwipePayload_SettlingThreshold_Evaluation()
    {
        var swipeLeft = new GestureSwipePayload(
            State: "SETTLING",
            Fraction: 0.8f,
            TargetFraction: 1.0f,
            DurationMs: 250
        );
        Assert.Equal("SETTLING", swipeLeft.State);
        Assert.True(swipeLeft.TargetFraction > 0.5f);

        var swipeRight = new GestureSwipePayload(
            State: "SETTLING",
            Fraction: -0.8f,
            TargetFraction: -1.0f,
            DurationMs: 250
        );
        Assert.True(swipeRight.TargetFraction < -0.5f);
    }

    [Fact]
    public void ConnectSong_ResolveCoverUrl_WithDynamicPort()
    {
        var localSong = new Song(
            Mid: "webdav_854721_c7a53bdd10",
            Title: "Test WebDav",
            Artist: "Artist",
            Album: "Album",
            Duration: 200
        );

        var connectSong8766 = ConnectSong.FromDomainSong(localSong, AudioQualityTier.HiRes, port: 8766);
        Assert.Contains(":8766/cover?mid=", connectSong8766.CoverUrl);
        Assert.Equal("HiRes", connectSong8766.CurrentTier);
    }

    [Fact]
    public void PlayerStateEvent_AvailableTiers_Serialization_Roundtrip()
    {
        var evt = new PlayerStateEvent(
            IsPlaying: true,
            PositionMs: 12000,
            DurationMs: 240000,
            CurrentTier: "HiRes",
            AvailableTiers: ["Standard", "HQ", "SQ", "HiRes", "Master"],
            LyricOffsetMs: 50
        );

        var json = JsonSerializer.Serialize(evt, ConnectJsonContext.Default.PlayerStateEvent);
        var deserialized = JsonSerializer.Deserialize(json, ConnectJsonContext.Default.PlayerStateEvent);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsPlaying);
        Assert.Equal(5, deserialized.AvailableTiers?.Count);
        Assert.Equal("Master", deserialized.AvailableTiers?[4]);
        Assert.Equal(50, deserialized.LyricOffsetMs);
    }

    [Fact]
    public void ConnectMdns_DnsSdPacket_IsMarkedAsResponse()
    {
        var packet = ConnectMdnsService.BuildCompleteDnsSdPacket("Melodist-TV-1234", "192.168.1.100", 8765, "dev-1", "TV", "tok", "123456");
        Assert.NotNull(packet);
        Assert.True(packet.Length > 12);

        // QR bit must be set to 1 (Response)
        Assert.True((packet[2] & 0x80) != 0);

        // Must reject own response packet as query to prevent multicast feedback storm
        Assert.False(ConnectMdnsService.ShouldHandleQuery(packet, "Melodist-TV-1234"));
    }

    [Fact]
    public void ConnectMdns_ShouldHandleQuery_ValidatesDnsHeader()
    {
        // Reject short packet
        Assert.False(ConnectMdnsService.ShouldHandleQuery(new byte[11], "Melodist-TV-1234"));

        // Reject response packet (QR=1)
        byte[] responsePacket = new byte[30];
        responsePacket[2] = 0x84; // QR=1
        responsePacket[4] = 0x00; responsePacket[5] = 0x01; // QDCOUNT=1
        Assert.False(ConnectMdnsService.ShouldHandleQuery(responsePacket, "Melodist-TV-1234"));

        // Reject packet with zero questions (QDCOUNT=0)
        byte[] zeroQdPacket = new byte[30];
        zeroQdPacket[2] = 0x00; // QR=0
        zeroQdPacket[4] = 0x00; zeroQdPacket[5] = 0x00; // QDCOUNT=0
        Assert.False(ConnectMdnsService.ShouldHandleQuery(zeroQdPacket, "Melodist-TV-1234"));

        // Reject query for unrelated service
        var unrelatedBytes = System.Text.Encoding.ASCII.GetBytes("\0\0\0\0\0\x01\0\0\0\0\0\0_googlecast._tcp.local");
        Assert.False(ConnectMdnsService.ShouldHandleQuery(unrelatedBytes, "Melodist-TV-1234"));

        // Accept query for melodist-connect service
        var validBytes = System.Text.Encoding.ASCII.GetBytes("\0\0\0\0\0\x01\0\0\0\0\0\0_melodist-connect._tcp.local");
        Assert.True(ConnectMdnsService.ShouldHandleQuery(validBytes, "Melodist-TV-1234"));
    }

    [Fact]
    public void TvConnectServer_Lifecycle_ManagesClientsSafely()
    {
        var storage = new ConnectStorage();
        using var server = new TvConnectServer(storage, port: 19876);
        Assert.Equal(0, server.ConnectedCount);
        Assert.False(server.IsRunning);

        server.Stop();
        Assert.Equal(0, server.ConnectedCount);
    }

    [Fact]
    public void Song_IsLocal_IsWebDav_MutualExclusion()
    {
        var localSong = new Song("local_123", "Title", "Artist", "Album", 180)
        {
            LocalFilePath = "/music/local.mp3"
        };
        Assert.True(localSong.IsLocal);
        Assert.False(localSong.IsWebDav);

        var webDavSong = new Song("webdav_123", "Title", "Artist", "Album", 180)
        {
            WebDavHref = "/dav/music/track.flac",
            LocalFilePath = "/home/user/.cache/qqmusic-tui/webdav/track.flac"
        };
        Assert.True(webDavSong.IsWebDav);
        Assert.False(webDavSong.IsLocal);

        var connectSong = ConnectSong.FromDomainSong(webDavSong);
        Assert.True(connectSong.IsWebDav);
        Assert.False(connectSong.IsLocal);
    }

    [Fact]
    public void FromDomainSong_LocalSong_UsesPcLocalPrefixAndStreamDirectUrl_WhenFileExists()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"qmtui_test_{Guid.NewGuid():N}.flac");
        File.WriteAllText(tempFile, "fake audio");

        try
        {
            var localSong = new Song("local_abcdef123456", "本地曲目", "歌手", "专辑", 200)
            {
                LocalFilePath = tempFile
            };

            var connectSong = ConnectSong.FromDomainSong(localSong, port: 8765);

            // 物理文件在 PC 存在时：广播给移动端时添加 pc_local_ 且 isLocal 为 false，提供 PC /stream/local 直通流
            Assert.Equal("pc_local_abcdef123456", connectSong.SongMid);
            Assert.False(connectSong.IsLocal);
            Assert.StartsWith("http://", connectSong.MediaMid);
            Assert.Contains("/stream/local?path=", connectSong.MediaMid);
            Assert.Contains(Uri.EscapeDataString(tempFile), connectSong.MediaMid);

            // 验证反向还原
            var domainSong = connectSong.ToDomainSong();
            Assert.Equal("local_abcdef123456", domainSong.Mid);
            Assert.True(domainSong.IsLocal);
            Assert.Equal(tempFile, domainSong.LocalFilePath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FromDomainSong_MobileLocalSong_PreservesLocalAndDoesNotForgePcStream()
    {
        // 模拟从手机端队列传入的手机自有文件（如 /storage/emulated/0/...，PC 本地物理不存在）
        var mobileSong = new Song("local_mobile123456", "手机本地曲目", "歌手", "专辑", 200)
        {
            LocalFilePath = "/storage/emulated/0/Music/sample.flac"
        };

        var connectSong = ConnectSong.FromDomainSong(mobileSong, port: 8765);

        // PC 本地不存在该物理文件时：严禁转为 pc_local_，严禁伪造 PC stream/local 直通流
        Assert.Equal("local_mobile123456", connectSong.SongMid);
        Assert.True(connectSong.IsLocal);
        Assert.DoesNotContain("/stream/local", connectSong.MediaMid);
    }

    [Fact]
    public void ToDomainSong_RestoresLocalPathFromStreamLocalUrl_WhenPathMissing()
    {
        var connectSong = new ConnectSong(
            SongId: 0,
            SongMid: "pc_local_789xyz",
            Name: "远程曲目",
            Singer: "歌手",
            Album: "专辑",
            MediaMid: "http://192.168.1.100:8765/stream/local?path=%2Fhome%2Fuser%2FMusic%2Ftest.mp3"
        );

        var domainSong = connectSong.ToDomainSong();
        Assert.Equal("local_789xyz", domainSong.Mid);
        Assert.True(domainSong.IsLocal);
        Assert.Equal("/home/user/Music/test.mp3", domainSong.LocalFilePath);
    }

    [Fact]
    public async Task TvConnectServer_ServesStreamLocalWithRangeAndHead()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"qmtui_test_stream_{Guid.NewGuid():N}.flac");
        var testData = new byte[1024];
        for (int i = 0; i < testData.Length; i++) testData[i] = (byte)(i % 256);
        await File.WriteAllBytesAsync(tempFile, testData);

        try
        {
            var storage = new ConnectStorage();
            using var server = new TvConnectServer(storage, port: 18765);
            server.Start();

            using var httpClient = new System.Net.Http.HttpClient();
            var encodedPath = Uri.EscapeDataString(tempFile);
            var streamUrl = $"http://127.0.0.1:{server.ActualPort}/stream/local?path={encodedPath}";

            // 1. 测试 HEAD 请求
            var headReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, streamUrl);
            var headResp = await httpClient.SendAsync(headReq);
            Assert.Equal(System.Net.HttpStatusCode.OK, headResp.StatusCode);
            Assert.Equal("audio/flac", headResp.Content.Headers.ContentType?.MediaType);
            Assert.Equal(1024, headResp.Content.Headers.ContentLength);
            Assert.True(headResp.Headers.Contains("Accept-Ranges"));

            // 2. 测试 Range: bytes=10-19 分片请求
            var rangeReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl);
            rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
            var rangeResp = await httpClient.SendAsync(rangeReq);
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, rangeResp.StatusCode);
            Assert.Equal(10, rangeResp.Content.Headers.ContentLength);
            Assert.Equal("bytes 10-19/1024", rangeResp.Content.Headers.GetValues("Content-Range").FirstOrDefault());
            var chunk = await rangeResp.Content.ReadAsByteArrayAsync();
            Assert.Equal(10, chunk.Length);
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(testData[10 + i], chunk[i]);
            }

            // 3. 测试 416 超范围
            var badRangeReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, streamUrl);
            badRangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2000, 3000);
            var badRangeResp = await httpClient.SendAsync(badRangeReq);
            Assert.Equal(System.Net.HttpStatusCode.RequestedRangeNotSatisfiable, badRangeResp.StatusCode);

            // 4. 测试 404 不存在文件
            var notFoundUrl = $"http://127.0.0.1:{server.ActualPort}/stream/local?path=%2Fnot_found_file.flac";
            var notFoundResp = await httpClient.GetAsync(notFoundUrl);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, notFoundResp.StatusCode);

            server.Stop();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task TvConnectServer_ServesCachedCoverByName()
    {
        var coverDir = CacheManager.CoversDir;
        Directory.CreateDirectory(coverDir);
        var coverFilename = $"test_cover_{Guid.NewGuid():N}.jpg";
        var coverFilePath = Path.Combine(coverDir, coverFilename);
        var dummyCoverData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        await File.WriteAllBytesAsync(coverFilePath, dummyCoverData);

        try
        {
            var storage = new ConnectStorage();
            using var server = new TvConnectServer(storage, port: 18775);
            server.Start();

            using var httpClient = new System.Net.Http.HttpClient();
            var coverUrl = $"http://127.0.0.1:{server.ActualPort}/cover?name={coverFilename}";
            var resp = await httpClient.GetAsync(coverUrl);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/jpeg", resp.Content.Headers.ContentType?.MediaType);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(dummyCoverData, bytes);

            server.Stop();
        }
        finally
        {
            if (File.Exists(coverFilePath)) File.Delete(coverFilePath);
        }
    }
}
