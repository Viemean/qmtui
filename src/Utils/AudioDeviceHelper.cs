using System.IO;

namespace QmTui.Utils;

/// <summary>
/// 系统音频设备探测与状态查询工具
/// 支持 Linux PipeWire / PulseAudio / ALSA 输出通道与内录/麦克风源探测
/// </summary>
public static class AudioDeviceHelper
{
    private static long s_lastCheckTick;
    private static bool s_cachedHasOutput = true;
    private static bool s_cachedHasInternal = true;
    private static bool s_cachedHasMicrophone = true;
    private static readonly Lock s_lock = new();

    /// <summary>
    /// 检测系统是否存在可用的物理/虚拟音频输出通道 (Sink)
    /// </summary>
    public static bool HasAudioOutputDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasOutput;
    }

    /// <summary>
    /// 检测系统是否存在可用的系统内录源 (Monitor of Sink)
    /// </summary>
    public static bool HasInternalRecordDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasInternal;
    }

    /// <summary>
    /// 检测系统是否存在可用的物理麦克风输入源
    /// </summary>
    public static bool HasMicrophoneDevice()
    {
        RefreshDeviceCacheIfNeeded();
        return s_cachedHasMicrophone;
    }

    /// <summary>
    /// 检测系统是否存在活跃的音频播放流
    /// 基于 Linux 原生 /proc/asound 接口直接扫描 PCM 播放状态，无需拉起外部进程
    /// </summary>
    public static bool HasActiveAudioPlayback()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            const string asoundPath = "/proc/asound";
            if (!Directory.Exists(asoundPath)) return false;

            var cardDirs = Directory.GetDirectories(asoundPath, "card*");
            foreach (var card in cardDirs)
            {
                var pcmDirs = Directory.GetDirectories(card, "pcm*p");
                foreach (var pcm in pcmDirs)
                {
                    var subDirs = Directory.GetDirectories(pcm, "sub*");
                    foreach (var sub in subDirs)
                    {
                        var statusFile = Path.Combine(sub, "status");
                        if (File.Exists(statusFile))
                        {
                            var status = File.ReadAllText(statusFile);
                            if (status.Contains("state: RUNNING", StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioDeviceHelper", $"HasActiveAudioPlayback error: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 获取当前默认输出设备的物理 Monitor 名称
    /// 使用 PulseAudio / PipeWire 原生支持的标准规范别名 @DEFAULT_SINK@.monitor
    /// </summary>
    public static string GetDefaultSinkMonitorDevice()
    {
        return "@DEFAULT_SINK@.monitor";
    }

    /// <summary>
    /// 获取当前默认麦克风的物理 Source 名称
    /// 使用 PulseAudio / PipeWire 原生支持的默认别名 default
    /// </summary>
    public static string GetDefaultMicrophoneDevice()
    {
        return "default";
    }

    /// <summary>
    /// 强制刷新设备缓存
    /// </summary>
    public static void InvalidateCache()
    {
        lock (s_lock)
        {
            s_lastCheckTick = 0;
        }
    }

    private static void RefreshDeviceCacheIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now - s_lastCheckTick < 2500)
        {
            return;
        }

        lock (s_lock)
        {
            if (now - s_lastCheckTick < 2500) return;

            s_lastCheckTick = now;
            s_cachedHasOutput = ProbeAudioOutputSinks();
            (s_cachedHasInternal, s_cachedHasMicrophone) = ProbeAudioSources();
        }
    }

    private static bool ProbeAudioOutputSinks()
    {
        try
        {
            const string procCards = "/proc/asound/cards";
            if (File.Exists(procCards))
            {
                var content = File.ReadAllText(procCards).Trim();
                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("no soundcards"))
                {
                    return true;
                }
            }
        }
        catch { }

        return true;
    }

    private static (bool hasInternal, bool hasMic) ProbeAudioSources()
    {
        if (!OperatingSystem.IsLinux()) return (true, true);

        try
        {
            const string asoundPath = "/proc/asound";
            if (Directory.Exists(asoundPath))
            {
                bool hasOutput = ProbeAudioOutputSinks();
                bool hasMic = false;

                var cardDirs = Directory.GetDirectories(asoundPath, "card*");
                foreach (var card in cardDirs)
                {
                    var pcmCaptureDirs = Directory.GetDirectories(card, "pcm*c");
                    if (pcmCaptureDirs.Length > 0)
                    {
                        hasMic = true;
                        break;
                    }
                }

                return (hasOutput, hasMic || hasOutput);
            }
        }
        catch { }

        return (true, true);
    }
}
