using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// 负责管理本地音频解码器硬件/软件能力。
/// </summary>
public static class DeviceAudioCapability
{
    private static readonly Lock s_lock = new();
    private static bool s_isInitialized;
    private static bool s_supportsDolby;
    private static bool s_supportsDtsX;
    private static bool? s_overrideDolby;
    private static bool? s_overrideDtsX;

    /// <summary>
    /// 获取系统是否具备 Dolby AC-4 解码器。
    /// </summary>
    public static bool IsDolbySupported
    {
        get
        {
            if (s_overrideDolby.HasValue) return s_overrideDolby.Value;
            EnsureInitialized();
            return s_supportsDolby;
        }
    }

    /// <summary>
    /// 获取系统是否具备 DTS:X 解码器。
    /// </summary>
    public static bool IsDtsXSupported
    {
        get
        {
            if (s_overrideDtsX.HasValue) return s_overrideDtsX.Value;
            EnsureInitialized();
            return s_supportsDtsX;
        }
    }

    /// <summary>
    /// 判断指定音质档位是否受本地设备解码能力支持。
    /// </summary>
    /// <param name="tier">待判定的音质档位。</param>
    /// <returns>若设备支持该格式解码返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool IsTierSupported(AudioQualityTier tier) => tier switch
    {
        AudioQualityTier.Dolby => IsDolbySupported,
        AudioQualityTier.DtsX => IsDtsXSupported,
        _ => true
    };

    /// <summary>
    /// 设置测试覆盖值（供单元测试场景模拟解码能力）。
    /// </summary>
    internal static void SetCapabilityOverride(bool? supportsDolby, bool? supportsDtsX)
    {
        s_overrideDolby = supportsDolby;
        s_overrideDtsX = supportsDtsX;
    }

    /// <summary>
    /// 重置测试覆盖与初始化状态。
    /// </summary>
    internal static void ResetForTesting()
    {
        s_overrideDolby = null;
        s_overrideDtsX = null;
        s_isInitialized = false;
        s_supportsDolby = false;
        s_supportsDtsX = false;
    }

    /// <summary>
    /// 初始化设备音频解码能力状态。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (s_isInitialized) return;
        lock (s_lock)
        {
            if (s_isInitialized) return;

            s_supportsDolby = false;
            s_supportsDtsX = false;
            s_isInitialized = true;
            AppLogger.Info("DeviceAudioCapability", $"Audio decoder capability initialized: Dolby(AC-4)={s_supportsDolby}, DTS:X={s_supportsDtsX}");
        }
    }
}
