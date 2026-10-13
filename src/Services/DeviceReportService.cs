using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 播放流水上报设备指纹信息。
/// </summary>
/// <param name="Model">移动设备型号标识。</param>
/// <param name="AndroidId">16 位十六进制设备标识符。</param>
/// <param name="OpenUdid">OpenUDID 校验码。</param>
/// <param name="OsVersion">操作系统版本字符串。</param>
/// <param name="SdkInt">Android SDK API 级别数值。</param>
public sealed record DeviceReportInfo(
    string Model,
    string AndroidId,
    string OpenUdid,
    string OsVersion,
    int SdkInt
);

/// <summary>
/// 播放流水上报设备特征解析与持久化管理服务。
/// </summary>
public static class DeviceReportService
{
    private static readonly (string Model, string AndroidId)[] s_verifiedDevicePool =
    [
        ("V2426A", "722102e37275540e"),
        ("2407FRK8EC", "a1b2c3d4e5f60718"),
        ("23127PN0CC", "3f8a9b2c1d4e5067"),
        ("PCLM10", "5c6d7e8f9012345a"),
        ("PGEM10", "8b7a6c5d4e3f2019")
    ];

    private static int s_poolIndex;

    private static readonly Lock s_initLock = new();
    private static DeviceReportInfo? s_cachedInfo;
    private static string ProfileConfigPath => Path.Combine(AppPathHelper.ConfigDir, "device_profile.json");

    /// <summary>
    /// 获取当前宿主环境绑定的设备指纹信息（优先从持久化配置读取，不存在时自动初始化）。
    /// </summary>
    /// <returns>解析或生成的设备特征对象。</returns>
    public static DeviceReportInfo GetDeviceInfo()
    {
        if (s_cachedInfo != null)
        {
            return s_cachedInfo;
        }

        lock (s_initLock)
        {
            if (s_cachedInfo != null)
            {
                return s_cachedInfo;
            }

            var loaded = TryLoadPersistedProfile();
            if (loaded != null && IsValidProfile(loaded))
            {
                s_cachedInfo = loaded;
                return s_cachedInfo;
            }

            s_cachedInfo = GenerateAndPersistProfile();
            return s_cachedInfo;
        }
    }

    /// <summary>
    /// 强制重新轮换并持久化一组全新的合法设备指纹。
    /// </summary>
    /// <returns>重新生成并持久化的设备特征对象。</returns>
    public static DeviceReportInfo RotateDeviceProfile()
    {
        lock (s_initLock)
        {
            s_cachedInfo = GenerateAndPersistProfile();
            AppLogger.Warn("DeviceReportService", $"Device profile rotated to model={s_cachedInfo.Model}, aid={s_cachedInfo.AndroidId}");
            return s_cachedInfo;
        }
    }

    private static bool IsValidProfile(DeviceReportInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Model) || string.IsNullOrWhiteSpace(info.AndroidId))
        {
            return false;
        }

        if (info.AndroidId.Length != 16)
        {
            return false;
        }

        char first = info.AndroidId[0];
        if (first is < '1' or > '9' && first is < 'a' or > 'f')
        {
            return false;
        }

        // 过滤此前已被服务端拉入丢弃窗口的特定已知异常 ID
        if (string.Equals(info.AndroidId, "d9a53a1bfc03fa68", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static DeviceReportInfo? TryLoadPersistedProfile()
    {
        try
        {
            string path = ProfileConfigPath;
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            string aid = root.TryGetProperty("android_id", out var a) ? a.GetString() ?? "" : "";
            string udid = root.TryGetProperty("open_udid", out var u) ? u.GetString() ?? "" : "";
            string osVer = root.TryGetProperty("os_version", out var o) ? o.GetString() ?? "14" : "14";
            int sdk = root.TryGetProperty("sdk_int", out var s) && s.TryGetInt32(out int sVal) ? sVal : 34;

            if (string.IsNullOrEmpty(udid) && !string.IsNullOrEmpty(aid))
            {
                udid = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(aid)));
            }

            return new DeviceReportInfo(model, aid, udid, osVer, sdk);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DeviceReportService", $"TryLoadPersistedProfile failed: {ex.Message}");
            return null;
        }
    }

    private static DeviceReportInfo GenerateAndPersistProfile()
    {
        int idx = Interlocked.Increment(ref s_poolIndex) - 1;
        var chosen = s_verifiedDevicePool[Math.Abs(idx) % s_verifiedDevicePool.Length];
        var model = chosen.Model;
        var androidId = chosen.AndroidId;

        var openUdid = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(androidId)));

        var info = new DeviceReportInfo(
            Model: model,
            AndroidId: androidId,
            OpenUdid: openUdid,
            OsVersion: "14",
            SdkInt: 34
        );

        try
        {
            string path = ProfileConfigPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = $$"""
                {
                  "model": "{{info.Model}}",
                  "android_id": "{{info.AndroidId}}",
                  "open_udid": "{{info.OpenUdid}}",
                  "os_version": "{{info.OsVersion}}",
                  "sdk_int": {{info.SdkInt}}
                }
                """;
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DeviceReportService", $"Failed to persist device profile: {ex.Message}");
        }

        return info;
    }
}
