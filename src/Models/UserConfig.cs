using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using QmTui.Utils;

namespace QmTui.Models;

/// <summary>
/// 用户全局偏好配置 (~/.config/qmtui/config.json)
/// </summary>
public sealed class UserConfig
{
    private static readonly string s_configDir = AppPathHelper.ConfigDir;
    private static readonly string s_configPath = Path.Combine(s_configDir, "config.json");

    public static UserConfig Current { get; private set; } = new();

    /// <summary>
    /// 是否在切歌时弹出 Linux 原生桌面气泡通知 (D-Bus org.freedesktop.Notifications)
    /// </summary>
    [JsonPropertyName("enable_song_switch_notification")]
    public bool EnableSongSwitchNotification { get; set; } = true;

    /// <summary>
    /// 是否开启听歌识曲系统内录预录加速 (Rolling Pre-roll Buffer)
    /// </summary>
    [JsonPropertyName("enable_audio_recognition_preroll")]
    public bool EnableAudioRecognitionPreRoll { get; set; } = true;

    /// <summary>
    /// 从 ~/.config/qmtui/config.json 加载偏好配置
    /// </summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(s_configPath))
            {
                Current = new UserConfig();
                return;
            }

            var json = File.ReadAllText(s_configPath);
            var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.UserConfig);
            Current = config ?? new UserConfig();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UserConfig", $"Failed to load config.json, using defaults: {ex.Message}");
            Current = new UserConfig();
        }
    }

    /// <summary>
    /// 持久化保存至 ~/.config/qmtui/config.json
    /// </summary>
    public void Save()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }

            var json = JsonSerializer.Serialize(this, AppJsonContext.Default.UserConfig);
            File.WriteAllText(s_configPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UserConfig", $"Failed to save config.json: {ex.Message}");
        }
    }
}
