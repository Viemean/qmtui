using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 原生声学识别服务门面
/// </summary>
public static class AcousticRecognitionService
{
    /// <summary>
    /// 当前环境中是否有可用的声学特征提取通道
    /// </summary>
    public static bool IsAvailable => true;

    /// <summary>
    /// 识别 16000Hz PCM 采样切片并检索曲库
    /// </summary>
    public static async Task<RecognitionResult?> RecognizePcmSamplesAsync(
        short[] pcm16k, 
        CancellationToken cancellationToken = default)
    {
        if (pcm16k == null || pcm16k.Length < (int)(16000 * 1.5))
        {
            return null;
        }

        try
        {
            // 1. 16000Hz 降采样到 8000Hz (单声道 2 点低通移动平均)
            short[] pcm8k = Downsample16kTo8k(pcm16k);

            // 2. 纯 C# 原生算法提取声学特征
            var feature = AcousticFingerprintExtractor.Extract(pcm8k);

            if (feature == null || feature.Data.Length == 0)
            {
                AppLogger.Force("AcousticRecognitionService", "声学特征提取失败或样本过短");
                return null;
            }

            // 3. 发起云端声学识别检索请求
            var response = await AcousticRecognizeClient.SearchAsync(feature, cancellationToken);
            if (response.Success && response.Song != null)
            {
                AppLogger.Force("AcousticRecognitionService", $"成功匹配曲库: {response.Song.Title} - {response.Song.Artist} (Offset: {response.OffsetSeconds:F3}s)");
                return new RecognitionResult(
                    Success: true,
                    Title: response.Song.Title,
                    Artist: response.Song.Artist,
                    Album: response.Song.Album,
                    MatchedSong: response.Song,
                    ErrorMessage: ""
                );
            }

            if (!string.IsNullOrEmpty(response.ErrorMessage))
            {
                AppLogger.Force("AcousticRecognitionService", $"云端检索未匹配: {response.ErrorMessage}");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Force("AcousticRecognitionService", $"识别链路异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 16000Hz PCM 简单降采样至 8000Hz
    /// </summary>
    public static short[] Downsample16kTo8k(short[] pcm16k)
    {
        int targetLength = pcm16k.Length / 2;
        short[] pcm8k = new short[targetLength];
        for (int i = 0; i < targetLength; i++)
        {
            int sum = pcm16k[i * 2] + pcm16k[i * 2 + 1];
            pcm8k[i] = (short)(sum / 2);
        }
        return pcm8k;
    }
}
