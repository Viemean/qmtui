using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 官方优图听歌识曲响应实体
/// </summary>
public record YoutuRecognizeResult(
    bool Success,
    string Title,
    string Artist,
    string Album,
    Song? Song = null,
    double OffsetSeconds = 0,
    string ErrorMessage = ""
);

/// <summary>
/// QAFP 指纹特征实体
/// </summary>
public record QafpFeature(
    byte[] Data,
    float Duration,
    int FeatureType = 0,
    float Confidence = 0.0f
);

/// <summary>
/// 官方优图听歌识曲 HTTP REST 客户端
/// </summary>
public static class YoutuRecognizeClient
{
    private const int QafpVersion = 201506;
    private const string Endpoint = "http://c.y.qq.com/youtu/humming/search";

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static (string Source, string Salt, byte[] AesKey)? s_cachedChannelConfig;
    private static readonly Lock s_configLock = new();

    private static readonly byte[] s_obfSource = [41, 42, 40, 5, 34, 51, 59, 53, 55, 51, 5, 41, 42, 63, 63, 62, 5, 59, 52, 62, 40, 53, 51, 62];
    private static readonly byte[] s_obfSalt = [41, 42, 40, 5, 34, 51, 59, 53, 55, 51, 5, 41, 42, 63, 63, 62, 5, 59, 52, 62, 40, 53, 51, 62, 59, 110, 111, 59, 107, 56];
    private static readonly byte[] s_obfAesKey = [41, 42, 40, 5, 98, 59, 104, 57, 62, 63, 59, 56, 109, 56, 98, 107];

    private static string Deobfuscate(byte[] bytes)
    {
        var res = new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) res[i] = (byte)(bytes[i] ^ 0x5A);
        return Encoding.UTF8.GetString(res);
    }

    private static (string Source, string Salt, byte[] AesKey) GetChannelConfig()
    {
        lock (s_configLock)
        {
            if (s_cachedChannelConfig != null) return s_cachedChannelConfig.Value;

            var defKey = Deobfuscate(s_obfAesKey);
            s_cachedChannelConfig = (Deobfuscate(s_obfSource), Deobfuscate(s_obfSalt), Encoding.UTF8.GetBytes(defKey));
            return s_cachedChannelConfig.Value;
        }
    }

    /// <summary>
    /// 使用 QAFP 特征直接向官方优图服务器发起识别请求
    /// </summary>
    public static async Task<YoutuRecognizeResult> SearchAsync(QafpFeature feature, CancellationToken cancellationToken = default)
    {
        if (feature.Data == null || feature.Data.Length == 0)
        {
            return new YoutuRecognizeResult(false, "", "", "", null, 0, "指纹特征数据为空");
        }

        var channel = GetChannelConfig();

        try
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long sessionId = timestamp;
            int fpType = feature.FeatureType + 1;

            // 1. 生成时间戳签名 MD5
            string signStr = $"{channel.Salt}{timestamp}";
            string veriStr = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(signStr)));

            // 2. 构造明文控制头 (以 \0 结尾)
            string header = $"v={QafpVersion}&source={channel.Source}&time={timestamp}&veri_str={veriStr}&cmd=1&info={feature.Duration:F1},{feature.Data.Length},10306&type=0&session_id={sessionId}&feature_type={fpType}&confidence={feature.Confidence:F1}\0";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

            // 3. 拼接 Payload: Header + FeatureBytes
            byte[] rawPayload = new byte[headerBytes.Length + feature.Data.Length];
            Buffer.BlockCopy(headerBytes, 0, rawPayload, 0, headerBytes.Length);
            Buffer.BlockCopy(feature.Data, 0, rawPayload, headerBytes.Length, feature.Data.Length);

            // 4. AES-ECB 加密
            byte[] encryptedBody;
            using (var aes = Aes.Create())
            {
                aes.Key = channel.AesKey;
                aes.Mode = CipherMode.ECB;
                encryptedBody = aes.EncryptEcb(rawPayload, PaddingMode.PKCS7);
            }

            // 5. 构建 HTTP POST 请求
            string requestUrl = $"{Endpoint}?sessionid={sessionId}&recognizetype=1&fpType={fpType}";
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "MusicRecognition 34}(android 10)");
            request.Headers.TryAddWithoutValidation("AppId", "85");
            request.Headers.TryAddWithoutValidation("Cookie", "uin=; ct=3003; cv=10306; recognizetype=1");

            var content = new ByteArrayContent(encryptedBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            request.Content = content;

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new YoutuRecognizeResult(false, "", "", "", null, 0, $"HTTP 请求失败: {response.StatusCode}");
            }

            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return ParseResponse(responseBytes);
        }
        catch (OperationCanceledException)
        {
            return new YoutuRecognizeResult(false, "", "", "", null, 0, "识别请求已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Force("YoutuRecognizeClient", $"识别请求异常: {ex}");
            return new YoutuRecognizeResult(false, "", "", "", null, 0, $"网络异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析服务器返回的 JSON 报文
    /// </summary>
    private static YoutuRecognizeResult ParseResponse(byte[] jsonBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ret", out var retProp) || retProp.GetInt32() != 0)
            {
                return new YoutuRecognizeResult(false, "", "", "", null, 0, "服务器未命中歌曲特征");
            }

            // 提取时间偏移量 offset
            double offset = 0.0;
            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.GetArrayLength() > 0)
            {
                var firstResult = resultsProp[0];
                if (firstResult.TryGetProperty("offset", out var offsetProp))
                {
                    if (offsetProp.ValueKind == JsonValueKind.String)
                    {
                        double.TryParse(offsetProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out offset);
                    }
                    else if (offsetProp.ValueKind == JsonValueKind.Number)
                    {
                        offset = offsetProp.GetDouble();
                    }
                }
            }

            // 提取歌曲元数据
            if (!root.TryGetProperty("songlist", out var songlistProp) || songlistProp.GetArrayLength() == 0)
            {
                return new YoutuRecognizeResult(false, "", "", "", null, offset, "返回数据中未包含歌曲信息");
            }

            var songItem = songlistProp[0];
            string songMid = songItem.TryGetProperty("mid", out var midProp) ? midProp.GetString() ?? "" : "";
            string title = songItem.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(title) && songItem.TryGetProperty("name", out var nameProp))
            {
                title = nameProp.GetString() ?? "";
            }

            long id = songItem.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
            int duration = songItem.TryGetProperty("interval", out var intProp) ? intProp.GetInt32() : 0;

            // 提取歌手
            string artist = "";
            var singers = new List<ArtistInfo>();
            if (songItem.TryGetProperty("singer", out var singerProp) && singerProp.ValueKind == JsonValueKind.Array)
            {
                var artistNames = new List<string>();
                foreach (var s in singerProp.EnumerateArray())
                {
                    string sName = s.TryGetProperty("name", out var snProp) ? snProp.GetString() ?? "" : "";
                    string sMid = s.TryGetProperty("mid", out var smProp) ? smProp.GetString() ?? "" : "";
                    long sId = s.TryGetProperty("id", out var sidProp) ? sidProp.GetInt64() : 0;
                    if (!string.IsNullOrEmpty(sName))
                    {
                        artistNames.Add(sName);
                        singers.Add(new ArtistInfo(sName, sMid, sId));
                    }
                }
                artist = string.Join(" / ", artistNames);
            }

            // 提取专辑
            string album = "";
            string albumMid = "";
            if (songItem.TryGetProperty("album", out var albumProp))
            {
                album = albumProp.TryGetProperty("name", out var anProp) ? anProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(album) && albumProp.TryGetProperty("title", out var atProp))
                {
                    album = atProp.GetString() ?? "";
                }
                albumMid = albumProp.TryGetProperty("mid", out var amProp) ? amProp.GetString() ?? "" : "";
            }

            // 提取 media_mid
            string mediaMid = "";
            if (songItem.TryGetProperty("file", out var fileProp) && fileProp.TryGetProperty("media_mid", out var mmProp))
            {
                mediaMid = mmProp.GetString() ?? "";
            }

            var song = new Song(
                Mid: songMid,
                Title: title,
                Artist: artist,
                Album: album,
                Duration: duration,
                MediaMid: mediaMid,
                Id: id,
                AlbumMid: albumMid
            )
            {
                Singers = singers
            };

            return new YoutuRecognizeResult(true, title, artist, album, song, offset, "");
        }
        catch (Exception ex)
        {
            AppLogger.Force("YoutuRecognizeClient", $"解析识别结果失败: {ex}");
            return new YoutuRecognizeResult(false, "", "", "", null, 0, $"解析响应失败: {ex.Message}");
        }
    }
}
