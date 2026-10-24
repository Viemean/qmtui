using System.Runtime.InteropServices;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Player;

/// <summary>
/// 负责探测与管理本地 Linux 系统 GStreamer 音频解码器硬件/软件能力。
/// </summary>
public static partial class DeviceAudioCapability
{
    private const string LibGst = "libgstreamer-1.0.so.0";

    private const ulong GST_ELEMENT_FACTORY_TYPE_DECODER = 1UL << 0;
    private const ulong GST_ELEMENT_FACTORY_TYPE_MEDIA_AUDIO = 1UL << 50;
    private const int GST_RANK_MARGINAL = 64;
    private const int GST_PAD_SINK = 1;

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
    /// 初始化并探测本地系统 GStreamer 是否具备对应格式的解码器。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (s_isInitialized) return;
        lock (s_lock)
        {
            if (s_isInitialized) return;

            try
            {
                var type = GST_ELEMENT_FACTORY_TYPE_DECODER | GST_ELEMENT_FACTORY_TYPE_MEDIA_AUDIO;
                var factories = gst_element_factory_list_get_elements(type, GST_RANK_MARGINAL);
                if (factories != 0)
                {
                    try
                    {
                        s_supportsDolby = CanDecodeCaps(factories, "audio/x-ac4");
                        s_supportsDtsX = CanDecodeCaps(factories, "audio/x-gst-fourcc-dtsx");
                    }
                    finally
                    {
                        gst_plugin_feature_list_free(factories);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DeviceAudioCapability", $"Failed to detect GStreamer audio decoders: {ex.Message}");
                s_supportsDolby = false;
                s_supportsDtsX = false;
            }

            s_isInitialized = true;
            AppLogger.Info("DeviceAudioCapability", $"Audio decoder capability probed: Dolby(AC-4)={s_supportsDolby}, DTS:X={s_supportsDtsX}");
        }
    }

    private static bool CanDecodeCaps(nint factories, string capsString)
    {
        var caps = gst_caps_from_string(capsString);
        if (caps == 0) return false;
        try
        {
            var filtered = gst_element_factory_list_filter(factories, caps, GST_PAD_SINK, false);
            if (filtered != 0)
            {
                gst_plugin_feature_list_free(filtered);
                return true;
            }
            return false;
        }
        finally
        {
            gst_caps_unref(caps);
        }
    }

    [LibraryImport(LibGst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_caps_from_string(string str);

    [LibraryImport(LibGst)]
    private static partial void gst_caps_unref(nint caps);

    [LibraryImport(LibGst)]
    private static partial nint gst_element_factory_list_get_elements(ulong type, int minrank);

    [LibraryImport(LibGst)]
    private static partial nint gst_element_factory_list_filter(nint list, nint caps, int direction, [MarshalAs(UnmanagedType.Bool)] bool subsetonly);

    [LibraryImport(LibGst)]
    private static partial void gst_plugin_feature_list_free(nint list);
}
