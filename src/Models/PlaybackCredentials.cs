using System.Text.Json.Serialization;

namespace QmTui.Models;

public enum PlaybackVipTier
{
    None = 0,
    Green = 1,
    Svip = 2
}

public sealed class PlaybackCredentials
{
    [JsonPropertyName("uin")]
    public string Uin { get; set; } = "";

    [JsonPropertyName("musicKey")]
    public string MusicKey { get; set; } = "";

    [JsonPropertyName("cookies")]
    public Dictionary<string, string> Cookies { get; set; } = [];

    [JsonPropertyName("isVip")]
    public bool IsVip { get; set; }

    [JsonPropertyName("isHugeVip")]
    public bool IsHugeVip { get; set; }

    [JsonPropertyName("isSvip")]
    public bool IsSvip { get; set; }

    [JsonPropertyName("vipExpireAt")]
    public string VipExpireAt { get; set; } = "";

    [JsonPropertyName("expireTimestamp")]
    public long ExpireTimestamp { get; set; }

    [JsonPropertyName("nick")]
    public string Nick { get; set; } = "";

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("isRevoked")]
    public bool IsRevoked { get; set; }

    [JsonPropertyName("allowRefresh")]
    public bool AllowRefresh { get; set; }

    [JsonIgnore]
    public PlaybackVipTier VipTier
    {
        get
        {
            if (IsSvip) return PlaybackVipTier.Svip;
            if (IsHugeVip || IsVip) return PlaybackVipTier.Green;
            return PlaybackVipTier.None;
        }
    }
}
