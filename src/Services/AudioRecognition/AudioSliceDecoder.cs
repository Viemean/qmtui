using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Utils;

namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 高性能本地/缓存音频切片 PCM 提取器
/// 利用系统 GStreamer 管道快速局部解码目标区域（避开前奏静音，提取 15s~23s 黄金主歌/副歌片段），
/// 输出 16000Hz 16-bit 单声道 PCM 样本，直接送入 ACR 声学指纹提取器。
/// </summary>
public static class AudioSliceDecoder
{
    private const int TargetSampleRate = 16000;
    private const int SliceDurationSeconds = 8;
    private const int BytesPerSample = 2; // 16-bit
    private const int BytesPerSecond = TargetSampleRate * BytesPerSample; // 32000 bytes/s
    private const int TargetSliceBytes = SliceDurationSeconds * BytesPerSecond; // 256000 bytes (128000 samples)

    /// <summary>
    /// 对本地音频文件或流式 URL 切片解码，返回 16000Hz 单声道 short[] PCM 样本
    /// </summary>
    public static async Task<short[]?> ExtractSlicePcmAsync(string audioPathOrUrl, double durationSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(audioPathOrUrl) || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        bool isUrl = audioPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     audioPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isUrl)
        {
            if (!File.Exists(audioPathOrUrl)) return null;
            var fi = new FileInfo(audioPathOrUrl);
            if (fi.Length < 32 * 1024) return null;
        }

        try
        {
            int skipSeconds = CalculateSkipSeconds(durationSeconds);
            int skipBytes = skipSeconds * BytesPerSecond;

            string srcPipeline = isUrl
                ? $"uridecodebin uri=\"{audioPathOrUrl}\""
                : $"filesrc location=\"{audioPathOrUrl}\" ! decodebin";

            var psi = new ProcessStartInfo
            {
                FileName = "gst-launch-1.0",
                Arguments = $"-q {srcPipeline} ! audioconvert ! audioresample ! audio/x-raw,rate={TargetSampleRate},channels=1,format=S16LE ! fdsink fd=1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stream = proc.StandardOutput.BaseStream;
            byte[] buffer = new byte[TargetSliceBytes];
            byte[] tempSkip = new byte[65536];

            // 1. 跳过前导静音/前奏字节
            int skipped = 0;
            while (skipped < skipBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = Math.Min(tempSkip.Length, skipBytes - skipped);
                int read = await stream.ReadAsync(tempSkip.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                skipped += read;
            }

            // 2. 读取目标 8 秒切片 PCM
            int totalRead = 0;
            while (totalRead < TargetSliceBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, TargetSliceBytes - totalRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                totalRead += read;
            }

            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }

            // 至少需要 3 秒有效 PCM (96000 字节) 才能提取可靠特征
            if (totalRead < BytesPerSecond * 3)
            {
                return null;
            }

            short[] samples = new short[totalRead / 2];
            Buffer.BlockCopy(buffer, 0, samples, 0, totalRead);
            return samples;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioSliceDecoder", $"Failed to extract audio slice for {audioPathOrUrl}: {ex.Message}");
            return null;
        }
    }

    internal static int CalculateSkipSeconds(double durationSeconds) => durationSeconds switch
    {
        > 25 => 15,
        > 10 => 5,
        _ => 0
    };
}
