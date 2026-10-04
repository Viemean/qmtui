using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QmTui.Services.Shazam;

/// <summary>
/// Shazam 音频指纹识别服务
/// </summary>
public static class NativeShazamService
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    static NativeShazamService()
    {
        s_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (iPad; U; CPU OS 4_3_3 like Mac OS X; en-us) AppleWebKit/533.17.9 (KHTML, like Gecko) Mobile/8J2");
        s_httpClient.DefaultRequestHeaders.Add("X-Shazam-Platform", "IPHONE");
        s_httpClient.DefaultRequestHeaders.Add("X-Shazam-AppVersion", "14.1.0");
        s_httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,ja;q=0.8");
    }

    /// <summary>
    /// 发送预热请求以建立连接池
    /// </summary>
    public static async Task PreWarmConnectionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://amp.shazam.com/");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = await s_httpClient.SendAsync(req, cts.Token);
        }
        catch
        {
            // 忽略预热探针阶段的异常
        }
    }

    /// <summary>
    /// 识别本地 WAV 音频切片文件
    /// </summary>
    public static async Task<(bool Success, string Title, string Artist, string Album, string Error)> RecognizeWavAsync(
        string wavFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavFilePath))
        {
            return (false, "", "", "", $"音频切片不存在: {wavFilePath}");
        }

        try
        {
            var pcmSamples = await ReadWavPcmSamplesAsync(wavFilePath, cancellationToken);
            return await RecognizePcmSamplesAsync(pcmSamples, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (false, "", "", "", "识别已取消");
        }
        catch (Exception ex)
        {
            return (false, "", "", "", $"读取音频异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 识别 16000Hz 16-bit 单声道 PCM 样本
    /// </summary>
    public static async Task<(bool Success, string Title, string Artist, string Album, string Error)> RecognizePcmSamplesAsync(
        ReadOnlyMemory<short> pcmSamples,
        CancellationToken cancellationToken = default)
    {
        if (pcmSamples.Length < (int)(16000 * 1.8)) // 少于 1.8 秒直接跳过
        {
            return (false, "", "", "", "音频样本过短，请等待累积更多音频");
        }

        try
        {
            // 提取 Shazam 音频特征签名
            var sw = Stopwatch.StartNew();
            var sig = ShazamAlgorithm.CreateSignatureFromPcm(pcmSamples.Span);
            var uri = sig.EncodeToUri();
            var sigMs = sw.ElapsedMilliseconds;

            // 3. 构建苹果 Shazam 云端发现接口请求体
            var uuid1 = Guid.NewGuid().ToString().ToUpper();
            var uuid2 = Guid.NewGuid().ToString().ToUpper();
            var url = $"https://amp.shazam.com/discovery/v5/zh-CN/CN/iphone/-/tag/{uuid1}/{uuid2}?sync=true&webv3=true&sampling=true&connected=&shazamapiversion=v3&sharehub=true&hubv5minorversion=v5.1&hidelb=true&video=v3";

            int sampleMs = (int)(sig.NumberSamples * 1000.0 / sig.SampleRateHz);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var jsonString = $$"""
            {
              "timezone": "Asia/Shanghai",
              "signature": {
                "uri": "{{uri}}",
                "samplems": {{sampleMs}}
              },
              "timestamp": {{timestamp}},
              "context": {},
              "geolocation": {}
            }
            """;

            var jsonContent = new StringContent(jsonString, Encoding.UTF8, "application/json");
            using var response = await s_httpClient.PostAsync(url, jsonContent, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, "", "", "", $"Shazam 云端服务响应错误: {(int)response.StatusCode}");
            }

            var respJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
            {
                return (false, "", "", "", "未能匹配到对应歌曲，请靠近声源或尝试其他片段");
            }

            var title = track.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
            var artist = track.TryGetProperty("subtitle", out var aProp) ? aProp.GetString() ?? "" : "";
            var album = "";

            // 从 sections metadata 中解析专辑名称
            if (track.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in sections.EnumerateArray())
                {
                    if (sec.TryGetProperty("type", out var st) && st.GetString() == "SONG" &&
                        sec.TryGetProperty("metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in metaArr.EnumerateArray())
                        {
                            var mTitle = m.TryGetProperty("title", out var mt) ? mt.GetString() ?? "" : "";
                            if (mTitle.Equals("album", StringComparison.OrdinalIgnoreCase) ||
                                mTitle.Equals("专辑", StringComparison.OrdinalIgnoreCase))
                            {
                                album = m.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(album)) break;
                }
            }



            return (true, title, artist, album, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "", "", "", "识别已取消");
        }
        catch (Exception ex)
        {
            return (false, "", "", "", $"原生识曲异常: {ex.Message}");
        }
    }



    /// <summary>
    /// 异步读取 WAV PCM 16-bit 样本
    /// </summary>
    private static async Task<short[]> ReadWavPcmSamplesAsync(string wavFilePath, CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var fs = new FileStream(wavFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            int read = await fs.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
            if (read < bytes.Length)
            {
                Array.Resize(ref bytes, read);
            }
        }

        if (bytes.Length < 44) return Array.Empty<short>();

        // 解析 RIFF WAV Header 找到 data chunk
        int dataOffset = 12;
        int dataLength = 0;
        while (dataOffset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, dataOffset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dataOffset + 4, 4));
            dataOffset += 8;

            if (chunkId == "data")
            {
                dataLength = Math.Min(chunkSize, bytes.Length - dataOffset);
                break;
            }
            dataOffset += chunkSize;
        }

        if (dataLength <= 0 || dataOffset >= bytes.Length)
        {
            // 兜底：假设前 44 字节为头，后续均为数据
            dataOffset = 44;
            dataLength = bytes.Length - 44;
        }

        int sampleCount = dataLength / 2;
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2, 2));
        }

        return samples;
    }
}
