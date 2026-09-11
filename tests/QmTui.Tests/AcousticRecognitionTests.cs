using QmTui.Models;
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
}
