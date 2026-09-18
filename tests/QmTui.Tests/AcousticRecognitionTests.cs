using QmTui.Models;
using QmTui.Services;
using QmTui.Services.AudioRecognition;
using Xunit;
using Xunit.Abstractions;

namespace QmTui.Tests;

/// <summary>
/// 原生声学识别测试套件
/// </summary>
public class AcousticRecognitionTests
{
    private readonly ITestOutputHelper _output;

    private static short[] CreateSyntheticPcm8k(double durationSeconds)
    {
        int sampleCount = (int)(8000 * durationSeconds);
        short[] pcm8k = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / 8000.0;
            double s1 = Math.Sin(2.0 * Math.PI * 440.0 * t);
            double s2 = Math.Sin(2.0 * Math.PI * 880.0 * t);
            pcm8k[i] = (short)((s1 + s2) * 10000);
        }
        return pcm8k;
    }

    public AcousticRecognitionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Downsample16kTo8k_CalculatesAverageCorrectly()
    {
        short[] pcm16k = [100, 200, -300, -100, 500, 700];
        var pcm8k = AcousticRecognitionService.Downsample16kTo8k(pcm16k);

        Assert.Equal(3, pcm8k.Length);
        Assert.Equal(150, pcm8k[0]);
        Assert.Equal(-200, pcm8k[1]);
        Assert.Equal(600, pcm8k[2]);
    }

    [Fact]
    public void AcousticFingerprintExtractor_Extract_SyntheticPcm_ProducesValidFeature()
    {
        short[] pcm8k = CreateSyntheticPcm8k(4.0);
        var feat = AcousticFingerprintExtractor.Extract(pcm8k);

        Assert.NotNull(feat);
        Assert.True(feat.Data.Length > 20, $"原生特征长度应大于头部，实际: {feat.Data.Length}");
        Assert.Equal(4.0f, feat.Duration, precision: 1);
        _output.WriteLine($"提取合成信号成功: {feat.Data.Length} 字节, 时长: {feat.Duration}s");
    }

    [Fact]
    public void AcousticFingerprintExtractor_Extract_EmptyOrShort_ReturnsNull()
    {
        Assert.Null(AcousticFingerprintExtractor.Extract([]));
        Assert.Null(AcousticFingerprintExtractor.Extract(new short[1600]));
    }

    [Fact]
    public async Task AcousticFingerprintExtractor_SliceDurations_Experiment()
    {
        const string testPcmPath = "/tmp/loser_5s.pcm";
        if (!File.Exists(testPcmPath)) return;

        byte[] allPcmBytes = await File.ReadAllBytesAsync(testPcmPath);
        short[] allSamples = new short[allPcmBytes.Length / 2];
        Buffer.BlockCopy(allPcmBytes, 0, allSamples, 0, allPcmBytes.Length);

        double[] testDurations = [2.0, 2.5, 3.0, 3.5, 4.0, 5.0];
        foreach (var dur in testDurations)
        {
            int count = (int)(8000 * dur);
            var slice = allSamples.AsSpan(0, count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var feat = AcousticFingerprintExtractor.Extract(slice);
            sw.Stop();

            if (feat == null)
            {
                _output.WriteLine($"[Duration {dur:F1}s] Extract 返回 null (耗时: {sw.ElapsedMilliseconds}ms)");
                continue;
            }

            var netSw = System.Diagnostics.Stopwatch.StartNew();
            var res = await AcousticRecognizeClient.SearchAsync(feat);
            netSw.Stop();

            _output.WriteLine($"[Duration {dur:F1}s] 特征: {feat.Data.Length} 字节, 提取耗时: {sw.Elapsed.TotalMilliseconds:F2}ms, 网络耗时: {netSw.ElapsedMilliseconds}ms, 识别成功: {res.Success}, 歌曲: {res.Title}, 错误: {res.ErrorMessage}");
        }
    }

    [Fact]
    public void AcousticFingerprintExtractor_Performance_Benchmark()
    {
        const string testPcmPath = "/tmp/loser_5s.pcm";
        if (!File.Exists(testPcmPath)) return;

        byte[] pcmBytes = File.ReadAllBytes(testPcmPath);
        short[] pcmSamples = new short[pcmBytes.Length / 2];
        Buffer.BlockCopy(pcmBytes, 0, pcmSamples, 0, pcmBytes.Length);

        // 预热 2 次
        for (int i = 0; i < 2; i++) _ = AcousticFingerprintExtractor.Extract(pcmSamples);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 30;
        for (int i = 0; i < iterations; i++)
        {
            var feat = AcousticFingerprintExtractor.Extract(pcmSamples);
            Assert.NotNull(feat);
        }
        sw.Stop();
        double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"[Benchmark] AcousticFingerprintExtractor.Extract 平均单次耗时: {avgMs:F2} ms (30 次迭代)");
    }

    [Fact]
    public void RollingAudioBuffer_WriteAndGetRecent_MaintainsCorrectOrder()
    {
        var ring = new RollingAudioBuffer(10);
        Assert.Equal(0, ring.AvailableBytes);

        // 写入 6 字节: [1, 2, 3, 4, 5, 6]
        ring.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(6, ring.AvailableBytes);

        var snapshot1 = ring.GetRecentBytes(4);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, snapshot1);

        // 写入 8 字节触发环形溢出覆盖: [7, 8, 9, 10, 11, 12, 13, 14]
        // 缓冲区容量 10，此时应保留最后 10 字节: [5, 6, 7, 8, 9, 10, 11, 12, 13, 14]
        ring.Write(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 });
        Assert.Equal(10, ring.AvailableBytes);

        var snapshot2 = ring.GetRecentBytes(5);
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, snapshot2);

        var allSnapshot = ring.GetRecentBytes(10);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 }, allSnapshot);
    }

    [Theory]
    [InlineData(120.0, 15)]
    [InlineData(26.0, 15)]
    [InlineData(25.0, 5)]
    [InlineData(15.0, 5)]
    [InlineData(10.0, 0)]
    [InlineData(5.0, 0)]
    public void AudioSliceDecoder_CalculateSkipSeconds_MatchesStrategy(double durationSeconds, int expectedSkip)
    {
        int actual = AudioSliceDecoder.CalculateSkipSeconds(durationSeconds);
        Assert.Equal(expectedSkip, actual);
    }

    [Fact]
    public void LocalLyricAutoMatcher_ComputePathAndMetaKeys_Stable()
    {
        string pathKey1 = LocalLyricAutoMatcher.ComputePathKey("/music/sample.flac");
        string pathKey2 = LocalLyricAutoMatcher.ComputePathKey("/music/sample.flac");
        Assert.Equal(32, pathKey1.Length);
        Assert.Equal(pathKey1, pathKey2);

        // 验证元数据清洗与时长分桶：01. きみの名前 (你的名字) 与 きみの名前 (你的名字)，时长 238s 与 239s 命中同一档位 (240s)
        string metaKey1 = LocalLyricAutoMatcher.ComputeMetaKey("01. きみの名前 (你的名字)", "藤川千愛", 238.2);
        string metaKey2 = LocalLyricAutoMatcher.ComputeMetaKey("きみの名前 (你的名字)", "藤川千愛", 239.5);
        Assert.Equal(32, metaKey1.Length);
        Assert.Equal(metaKey1, metaKey2);
    }

    [Fact]
    public void LocalLyricAutoMatcher_UnifiedCache_ThreeTierCrossSharing()
    {
        const string songMid = "test_mid_002LRrPN";
        var lines = new List<LyricLine>
        {
            new(TimeSpan.Zero, "抜け殻みたいな空に", "凝望着宛如空壳的天空"),
            new(TimeSpan.FromSeconds(5), "ほら キミの声が僕を救うよ", "看啊 你的声音拯救了我")
        };

        var localSong = new Song("local_123", "01. きみの名前 (你的名字)", "藤川千愛", "Album", 239)
        {
            LocalFilePath = "/mnt/Sun/Music/01.flac"
        };
        var webDavSong = new Song("webdav_456", "きみの名前 (你的名字)", "藤川千愛", "Album", 238)
        {
            WebDavHref = "http://nas/01.flac"
        };
        var onlineSong = new Song(songMid, "きみの名前 (你的名字)", "藤川千愛", "Album", 240);

        // 1. 在线播放写入母本库与元数据索引
        LocalLyricAutoMatcher.SaveUnifiedCache(songMid, "album_mid_789", "きみの名前 (你的名字)", "藤川千愛", lines, onlineSong);

        // 2. 本地歌曲通过元数据索引跨来源 0ms 命中该母本
        var localHit = LocalLyricAutoMatcher.TryGetCachedLyrics(localSong);
        Assert.NotNull(localHit);
        Assert.Equal(songMid, localHit.SongMid);
        Assert.Equal(2, localHit.Lines.Count);
        Assert.Equal("凝望着宛如空壳的天空", localHit.Lines[0].Trans);

        // 3. WebDAV 歌曲通过元数据索引跨来源 0ms 命中该母本
        var webDavHit = LocalLyricAutoMatcher.TryGetCachedLyrics(webDavSong);
        Assert.NotNull(webDavHit);
        Assert.Equal(songMid, webDavHit.SongMid);
        Assert.Equal("看啊 你的声音拯救了我", webDavHit.Lines[1].Trans);

        // 4. 在线歌曲根据 Mid 直接命中母本
        var onlineHit = LocalLyricAutoMatcher.TryGetCachedLyrics(onlineSong);
        Assert.NotNull(onlineHit);
        Assert.Equal(songMid, onlineHit.SongMid);
    }

    [Fact]
    public void LocalLyricAutoMatcher_NeedsMatching_LogicRules()
    {
        var song = new Song("001", "Test Song", "Test Artist", "Album", 180);

        // 1. 无歌词需匹配
        Assert.True(LocalLyricAutoMatcher.NeedsMatching(song, []));

        // 2. 占位歌词需匹配
        Assert.True(LocalLyricAutoMatcher.NeedsMatching(song, [new LyricLine(TimeSpan.Zero, "暂无歌词")]));

        // 3. 多行且有翻译，不需要匹配
        Assert.False(LocalLyricAutoMatcher.NeedsMatching(song, [
            new LyricLine(TimeSpan.Zero, "Hello", "你好"),
            new LyricLine(TimeSpan.FromSeconds(2), "World", "世界")
        ]));
    }
}
