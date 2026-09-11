using QmTui.Models;
using QmTui.Services.AudioRecognition;
using Xunit;
using Xunit.Abstractions;

namespace QmTui.Tests;

/// <summary>
/// 官方优图听歌识曲测试套件
/// </summary>
public class YoutuRecognitionTests
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

    public YoutuRecognitionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Downsample16kTo8k_CalculatesAverageCorrectly()
    {
        short[] pcm16k = [100, 200, -300, -100, 500, 700];
        var pcm8k = YoutuRecognitionService.Downsample16kTo8k(pcm16k);

        Assert.Equal(3, pcm8k.Length);
        Assert.Equal(150, pcm8k[0]);
        Assert.Equal(-200, pcm8k[1]);
        Assert.Equal(600, pcm8k[2]);
    }

    [Fact]
    public void QafpNativeRunner_IsAvailable_ReturnsTrueWhenRunnerPresent()
    {
        bool available = QafpNativeRunner.IsAvailable;
        _output.WriteLine($"QafpNativeRunner.IsAvailable: {available}");
        _output.WriteLine($"Runner Path: {QafpNativeRunner.RunnerPath}");
        _output.WriteLine($"Sysroot Path: {QafpNativeRunner.SysrootPath}");

        if (!string.IsNullOrEmpty(QafpNativeRunner.RunnerPath) && File.Exists(QafpNativeRunner.RunnerPath))
        {
            Assert.True(available, "当 qafp_runner 与 sysroot 存在时，IsAvailable 必须为 true");
            Assert.True(YoutuRecognitionService.IsAvailable, "YoutuRecognitionService.IsAvailable 必须与 Runner 状态同步为 true");
        }
    }

    [Fact]
    public void QafpNativeRunner_Extract_SyntheticPcm_ProducesValidFingerprint()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            _output.WriteLine("[Skip] QafpNativeRunner 环境不可用");
            return;
        }

        // 构造 3.6 秒 (28800 点) 的 440Hz + 880Hz 双音合成 8000Hz PCM
        short[] pcm8k = new short[28800];
        for (int i = 0; i < pcm8k.Length; i++)
        {
            double t = (double)i / 8000.0;
            double s1 = Math.Sin(2.0 * Math.PI * 440.0 * t);
            double s2 = Math.Sin(2.0 * Math.PI * 880.0 * t);
            pcm8k[i] = (short)((s1 + s2) * 10000);
        }

        var feature = QafpNativeRunner.Extract(pcm8k);

        Assert.NotNull(feature);
        Assert.True(feature.Data.Length > 20, $"特征字节长度应大于基本头部，实际: {feature.Data.Length}");
        Assert.Equal(3.6f, feature.Duration, precision: 1);
        _output.WriteLine($"Synthetic PCM feature extracted: {feature.Data.Length} bytes, duration: {feature.Duration}s");
    }

    [Fact]
    public void QafpNativeRunner_Extract_ShortOrEmptyPcm_HandlesSafely()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            return;
        }

        // 空数组
        var emptyFeat = QafpNativeRunner.Extract([]);
        Assert.Null(emptyFeat);

        // 过短音频 (0.2 秒 1600 点，不足最小分析窗 2 秒)
        short[] shortPcm = new short[1600];
        var shortFeat = QafpNativeRunner.Extract(shortPcm);
        Assert.Null(shortFeat);
    }

    [Fact]
    public void QafpNativeRunner_WorkerSession_ContinuousExtraction_WorksConsistently()
    {
        if (!QafpNativeRunner.IsAvailable)
        {
            _output.WriteLine("[Skip] QafpNativeRunner 环境不可用");
            return;
        }

        short[] pcm8k = CreateSyntheticPcm8k(4.0);

        using var session = QafpNativeRunner.StartWorkerSession();
        Assert.NotNull(session);
        Assert.True(session.IsReady);

        // 连续提取 3 次，验证长连接管道通信与 JNI Reset 状态复位正确性
        for (int i = 0; i < 3; i++)
        {
            var feat = session.Extract(pcm8k);
            Assert.NotNull(feat);
            Assert.True(feat.Data.Length > 20, $"特征长度应大于基本头部: {feat.Data.Length}");
            Assert.Equal(4.0f, feat.Duration, precision: 1);
        }
        _output.WriteLine("WorkerSession 连续 3 次提取通过，特征输出稳定");
    }
}
