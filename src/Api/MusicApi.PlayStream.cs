using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    private const string PlayStreamTimeKeySalt = "gk2$Lh-&l4#!4iow";

    private static readonly HttpClient s_playStreamClient = new(HttpHelper.CreateDefaultHandler(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)))
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// 向 stat6 数据网关上报歌曲单次流式收听时长流水。
    /// </summary>
    /// <param name="songId">数字格式曲目唯一标识符。</param>
    /// <param name="durationSec">音频总时长（秒）。</param>
    /// <param name="playTimeSec">当前片段实际播放有效时长（秒）。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>若上报被服务器成功接收返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static async Task<bool> ReportPlayStreamAsync(long songId, int durationSec, int playTimeSec, CancellationToken ct = default)
    {
        if (songId <= 0 || playTimeSec < 5 || !UserSession.Current.IsLoggedIn)
        {
            return false;
        }

        try
        {
            var uin = UserSession.Current.Uin;
            var authst = UserSession.Current.MusicKey;
            var device = DeviceReportService.GetDeviceInfo();
            long optime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long playTimeMs = (long)playTimeSec * 1000;
            int loginType = authst.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;

            var cookies = UserSession.Current.Cookies;
            cookies.TryGetValue("psrf_qqopenid", out var openId);
            cookies.TryGetValue("psrf_qqaccess_token", out var accessToken);
            openId ??= "";
            accessToken ??= "";

            var timeKey = Convert.ToHexString(
                MD5.HashData(Encoding.UTF8.GetBytes($"{optime}{playTimeSec}{uin}{PlayStreamTimeKeySalt}"))
            ).ToUpperInvariant();

            var vkey = Convert.ToHexStringLower(
                MD5.HashData(Encoding.UTF8.GetBytes($"{device.OpenUdid}:{optime}"))
            );

            int hUid = 0;
            foreach (char ch in device.OpenUdid)
            {
                hUid = (31 * hUid + ch);
            }
            string uidDigits = Math.Abs((long)hUid).ToString().PadLeft(10, '0')[..10];

            int hTrace = 0;
            string traceSeed = $"{device.OpenUdid}:{optime}";
            foreach (char ch in traceSeed)
            {
                hTrace = (31 * hTrace + ch);
            }
            string traceId = $"11_{Math.Abs((long)hTrace).ToString().PadLeft(11, '0')[..11]}_{optime}";

            string sid = DateTimeOffset.FromUnixTimeSeconds(optime).ToLocalTime().ToString("yyyyMMddHHmmss") + uidDigits;

            static string ComputeKh(string s)
            {
                int kh = 0;
                foreach (char c in s)
                {
                    kh = (31 * kh + c);
                }
                return Math.Abs((long)kh).ToString();
            }

            var reversedUdid = string.Create(device.OpenUdid.Length, device.OpenUdid, (span, src) =>
            {
                for (int i = 0; i < src.Length; i++)
                {
                    span[i] = src[src.Length - 1 - i];
                }
            });
            string tid = $"{ComputeKh(device.OpenUdid).PadLeft(10, '0')[..10]}{ComputeKh(reversedUdid).PadLeft(9, '0')[..9]}";

            var p1 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{device.OpenUdid}_p1")));
            var p2 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{device.OpenUdid}_p2")));
            var p3 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{device.OpenUdid}_p3")))[..24];
            string taid = $"{p1}{p2}{p3}";

            var xml = string.Concat(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<root>",
                $"<OpenUDID>{device.OpenUdid}</OpenUDID>",
                $"<udid>{device.OpenUdid}</udid>",
                "<ct>11</ct><cv>12030508</cv><v>12030508</v>",
                $"<chid>74648</chid><os_ver>{device.OsVersion}</os_ver>",
                $"<aid>{device.AndroidId}</aid><phonetype>{device.Model}</phonetype>",
                $"<devicelevel>{device.SdkInt}</devicelevel><newdevicelevel>{device.SdkInt}</newdevicelevel>",
                "<deviceScore>800.0</deviceScore>",
                "<QIMEI36></QIMEI36>",
                $"<taid>{taid}</taid>",
                $"<tmeAppID>qqmusic</tmeAppID><tid>{tid}</tid>",
                "<modeSwitch>6</modeSwitch><teenMode>0</teenMode>",
                $"<uid>{uidDigits}</uid><sid>{sid}</sid>",
                $"<OpenUDID2>{device.OpenUdid}</OpenUDID2>",
                "<ui_mode>1</ui_mode><nettype>1030</nettype>",
                $"<tmeLoginType>{loginType}</tmeLoginType><tmeLoginMethod>2</tmeLoginMethod>",
                $"<fPersonality>0</fPersonality><wid>{uidDigits}</wid>",
                "<v4ip>192.168.1.100</v4ip>",
                $"<qq>{uin}</qq>",
                $"<authst>{authst}</authst>",
                $"<psrf_qqopenid>{openId}</psrf_qqopenid>",
                "<psrf_access_token_expiresAt>0</psrf_access_token_expiresAt>",
                $"<psrf_qqaccess_token>{accessToken}</psrf_qqaccess_token>",
                "<hotfix>100000000</hotfix>",
                $"<traceid>{traceId}</traceid>",
                "<cid>228</cid>",
                $"<item cmd=\"1\" optime=\"{optime}\" nettype=\"1030\" QQ=\"{uin}\" uid=\"{uidDigits}\" os=\"{device.OsVersion}\" model=\"{device.Model}\" version=\"12.3.5.8\" songtype=\"1\" playtype=\"4\" from=\"1,132,151,\" dts=\"0\" openstore=\"0\" crytype=\"5\" paytype=\"3\" desktoplyric=\"0\" playdevice=\"0\" playlist_mode=\"0\" outdev=\"0\" url=\"26\" playmode=\"1\" repeat_times=\"0\" cdn=\"\" cdnip=\"\" hasFirstBuffer=\"1\" hijackflag=\"0\" filetype=\"4\" err=\"0\" size=\"0\" time=\"{playTimeSec}\" retry=\"0\" issoftdecode=\"1\" component_type=\"2\" bandwidth_policy=\"0\" secondCacheCount=\"0\" wait_time=\"0\" player_retry=\"0\" audiotime=\"{durationSec}\" timekey=\"{timeKey}\" vkey=\"{vkey}\" play_duration_mi=\"{playTimeMs}\" play_speed=\"1.0\" vip_level=\"65552\" audio_effect=\"0:0\" songid=\"{songId}\" singerid=\"0\" fversion=\"0\"/>",
                "</root>"
            );

            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var rawBytes = Encoding.UTF8.GetBytes(xml);
                    gzip.Write(rawBytes, 0, rawBytes.Length);
                }
                compressed = ms.ToArray();
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://stat6.y.qq.com/android/fcgi-bin/imusic_tj");
            req.Content = new ByteArrayContent(compressed);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
            req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            req.Headers.TryAddWithoutValidation("User-Agent", $"QQMusic 12030508(android {device.OsVersion})");
            req.Headers.TryAddWithoutValidation("Host", "stat6.y.qq.com");
            req.Headers.TryAddWithoutValidation("Connection", "Keep-Alive");

            using var resp = await s_playStreamClient.SendAsync(req, ct).ConfigureAwait(false);
            bool success = resp.IsSuccessStatusCode;
            if (success)
            {
                AppLogger.Info("MusicApi.PlayStream", $"Reported {playTimeSec}s for songId={songId} (model={device.Model}, aid={device.AndroidId})");
            }
            else
            {
                AppLogger.Warn("MusicApi.PlayStream", $"Report failed with status {resp.StatusCode} for songId={songId}");
            }
            return success;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi.PlayStream", $"ReportPlayStream exception: {ex.Message}");
            return false;
        }
    }
}
