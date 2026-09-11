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
    public async Task AcousticFingerprintExtractor_GoldenPcm_ExtractsAndRecognizesSuccessfully()
    {
        const string testPcmPath = "/tmp/loser_5s.pcm";
        if (!File.Exists(testPcmPath))
        {
            _output.WriteLine("[Skip] /tmp/loser_5s.pcm 不存在，跳过端到端音频提取测试");
            return;
        }

        byte[] pcmBytes = await File.ReadAllBytesAsync(testPcmPath);
        var landmarks = AcousticFingerprintExtractor.ExtractLandmarks(pcmBytes);

        Assert.Equal(4, landmarks.Length);
        _output.WriteLine($"Band 0 点数: {landmarks[0].Count}, Band 1: {landmarks[1].Count}, Band 2: {landmarks[2].Count}, Band 3: {landmarks[3].Count}");
        Assert.Equal(80, landmarks[0].Count);
        Assert.Equal(84, landmarks[1].Count);
        Assert.Equal(87, landmarks[2].Count);
        Assert.Equal(81, landmarks[3].Count);

        byte[] packedFeat = AcousticFingerprintExtractor.PackLandmarks(landmarks);
        _output.WriteLine($"原生打包特征长度: {packedFeat.Length} 字节");
        Assert.Equal(537, packedFeat.Length);

        // 云端识别测试
        var feature = new AcousticFeature(packedFeat, 5.0f);
        var result = await AcousticRecognizeClient.SearchAsync(feature);

        _output.WriteLine($"云端识别响应: Success={result.Success}, Title={result.Title}, Artist={result.Artist}, Offset={result.OffsetSeconds:F3}s, Err={result.ErrorMessage}");
        Assert.True(result.Success, $"云端识别应当成功，错误: {result.ErrorMessage}");
        Assert.Contains("LOSER", result.Title, StringComparison.OrdinalIgnoreCase);
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
}
