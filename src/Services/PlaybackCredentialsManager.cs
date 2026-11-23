using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

public abstract record ImportValidationResult
{
    public sealed record Success(PlaybackCredentials Credentials) : ImportValidationResult;
    public sealed record Expired(PlaybackCredentials Credentials, string ExpireAt) : ImportValidationResult;
    public sealed record NonVip(PlaybackCredentials Credentials) : ImportValidationResult;
    public sealed record DowngradeWarning(PlaybackCredentials Credentials, PlaybackVipTier CurrentTier, PlaybackVipTier ImportedTier) : ImportValidationResult;
}

public static class PlaybackCredentialsManager
{
    private static readonly string s_storagePath = Path.Combine(AppPathHelper.ConfigDir, "playback_credentials.json");
    private static readonly Lock s_lock = new();

    private const byte MagicHeader = 0x4D; // 'M'
    private const byte Version = 0x01;
    private const byte FlagPasswordProtected = 0x01;
    private const byte FlagDefaultProtected = 0x00;

    private static readonly byte[] s_defaultKeySalt =
    [
        0x59, 0x75, 0x7A, 0x75, 0x6B, 0x69, 0x4D, 0x65,
        0x6C, 0x6F, 0x64, 0x69, 0x73, 0x74, 0x54, 0x56
    ];

    private const string DefaultPassphrase = "melodist-playback-token-vault-v1";

    private static readonly HashSet<string> s_essentialCookieKeys =
    [
        "qm_keyst", "qqmusic_key", "pskey", "p_skey", "skey", "uin", "musicid", "wxuin", "wxopenid", "pt2gguin"
    ];

    private static readonly HashSet<string> s_refreshCookieKeys =
    [
        "refresh_token", "refresh_key", "psrf_qqopenid", "psrf_qqaccess_token", "psrf_qqunionid", "openid", "access_token", "tmeLoginType"
    ];

    public static PlaybackCredentials? CurrentCredentials { get; private set; }

    public static bool HasCustomCredentials => CurrentCredentials != null;

    public static event Action<PlaybackCredentials?>? CredentialsChanged;

    static PlaybackCredentialsManager()
    {
        LoadFromDisk();
    }

    public static void SetCredentials(PlaybackCredentials? creds)
    {
        lock (s_lock)
        {
            CurrentCredentials = creds;
            SaveToDisk(creds);
        }
        CredentialsChanged?.Invoke(creds);
    }

    public static void ClearCredentials()
    {
        lock (s_lock)
        {
            CurrentCredentials = null;
            try
            {
                if (File.Exists(s_storagePath))
                {
                    File.Delete(s_storagePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("PlaybackCredentialsManager", $"Failed to delete credentials file: {ex.Message}");
            }
        }
        CredentialsChanged?.Invoke(null);
    }

    public static void MarkCredentialsRevoked()
    {
        lock (s_lock)
        {
            if (CurrentCredentials != null && !CurrentCredentials.IsRevoked)
            {
                CurrentCredentials.IsRevoked = true;
                SaveToDisk(CurrentCredentials);
            }
        }
        CredentialsChanged?.Invoke(CurrentCredentials);
    }

    public static string GetActiveUin()
    {
        var custom = CurrentCredentials;
        if (custom != null && !string.IsNullOrWhiteSpace(custom.Uin) && !IsCredentialInvalid(custom))
        {
            return custom.Uin;
        }
        return string.IsNullOrEmpty(UserSession.Current.Uin) ? "0" : UserSession.Current.Uin;
    }

    public static string GetActiveAuthst()
    {
        var custom = CurrentCredentials;
        if (custom != null && !string.IsNullOrWhiteSpace(custom.MusicKey) && !IsCredentialInvalid(custom))
        {
            return custom.MusicKey;
        }
        return UserSession.Current.MusicKey;
    }

    public static string GetActiveCookieHeader()
    {
        var custom = CurrentCredentials;
        if (custom != null && custom.Cookies.Count > 0 && !IsCredentialInvalid(custom))
        {
            return string.Join("; ", custom.Cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        return UserSession.Current.GetCookieHeader();
    }

    public static PlaybackVipTier GetEffectiveVipTier()
    {
        var custom = CurrentCredentials;
        if (custom != null && !IsCredentialInvalid(custom))
        {
            return custom.VipTier;
        }

        if (UserSession.Current.IsSvip) return PlaybackVipTier.Svip;
        if (UserSession.Current.IsVip) return PlaybackVipTier.Green;
        return PlaybackVipTier.None;
    }

    public static bool IsCredentialExpired(PlaybackCredentials creds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (creds.ExpireTimestamp > 0L)
        {
            return creds.ExpireTimestamp < now;
        }
        var parsed = ParseExpireTimestamp(creds.VipExpireAt);
        if (parsed.HasValue)
        {
            return parsed.Value < now;
        }
        return false;
    }

    public static bool IsCredentialRevoked(PlaybackCredentials? creds = null)
    {
        var target = creds ?? CurrentCredentials;
        return target?.IsRevoked == true;
    }

    public static bool IsCredentialInvalid(PlaybackCredentials? creds = null)
    {
        var target = creds ?? CurrentCredentials;
        if (target == null) return false;
        return target.IsRevoked || IsCredentialExpired(target);
    }

    public static bool CanRefreshCredentials(PlaybackCredentials? creds = null)
    {
        var c = creds ?? CurrentCredentials;
        if (c == null) return false;
        bool hasOfficialToken = c.Cookies.TryGetValue("refresh_token", out var rt) && !string.IsNullOrWhiteSpace(rt);
        bool hasQqToken = (c.Cookies.TryGetValue("psrf_qqopenid", out var qid) && !string.IsNullOrWhiteSpace(qid)
                           || c.Cookies.TryGetValue("openid", out var oid) && !string.IsNullOrWhiteSpace(oid))
                          && (c.Cookies.TryGetValue("psrf_qqaccess_token", out var qat) && !string.IsNullOrWhiteSpace(qat)
                           || c.Cookies.TryGetValue("access_token", out var oat) && !string.IsNullOrWhiteSpace(oat));
        return c.AllowRefresh || hasOfficialToken || hasQqToken;
    }

    public static long? ParseExpireTimestamp(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        if (long.TryParse(dateStr.Trim(), out var ts))
        {
            return ts < 10_000_000_000L ? ts * 1000L : ts;
        }

        var trimmed = dateStr.Trim();
        if (DateTime.TryParse(trimmed, out var dt))
        {
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }
        return null;
    }

    public static bool IsTokenPasswordProtected(string tokenText)
    {
        var bytes = DecodeTokenBytes(tokenText);
        if (bytes.Length < 3 || bytes[0] != MagicHeader)
        {
            throw new FormatException("无效的播放凭证格式");
        }
        return bytes[2] == FlagPasswordProtected;
    }

    public static ImportValidationResult InspectToken(string tokenText, string? password = null)
    {
        var creds = ParseTokenPayload(tokenText, password);
        if (IsCredentialExpired(creds))
        {
            return new ImportValidationResult.Expired(creds, creds.VipExpireAt);
        }
        if (creds.VipTier == PlaybackVipTier.None)
        {
            return new ImportValidationResult.NonVip(creds);
        }

        var currentTier = UserSession.Current.IsSvip ? PlaybackVipTier.Svip
                        : UserSession.Current.IsVip ? PlaybackVipTier.Green
                        : PlaybackVipTier.None;

        if (creds.VipTier < currentTier)
        {
            return new ImportValidationResult.DowngradeWarning(creds, currentTier, creds.VipTier);
        }

        return new ImportValidationResult.Success(creds);
    }

    public static PlaybackCredentials ImportToken(string tokenText, string? password = null)
    {
        var creds = ParseTokenPayload(tokenText, password);
        SetCredentials(creds);
        return creds;
    }

    public static string ExportToken(UserSession session, string? password = null, bool allowRefresh = false)
    {
        Dictionary<string, string> cleanCookies = [];
        foreach (var (k, v) in session.Cookies)
        {
            if (s_essentialCookieKeys.Contains(k) || (allowRefresh && s_refreshCookieKeys.Contains(k)))
            {
                cleanCookies[k] = v;
            }
        }

        string expStr = session.VipExpireAt;
        long expTs = ParseExpireTimestamp(expStr) ?? 0L;

        var payload = new CompactCredentialsPayload
        {
            U = session.Uin,
            K = session.MusicKey,
            C = cleanCookies,
            V = session.IsVip,
            N = session.Nick,
            T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hv = false,
            Sv = session.IsSvip,
            Exp = expStr,
            Et = expTs,
            Ar = allowRefresh
        };

        byte[] rawJsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, CredentialsJsonContext.Default.CompactCredentialsPayload);
        byte[] compressed = CompressZlib(rawJsonBytes);

        bool hasPassword = !string.IsNullOrWhiteSpace(password);
        Span<byte> salt = stackalloc byte[16];
        if (hasPassword)
        {
            RandomNumberGenerator.Fill(salt);
        }
        else
        {
            s_defaultKeySalt.CopyTo(salt);
        }

        Span<byte> iv = stackalloc byte[12];
        RandomNumberGenerator.Fill(iv);

        Span<byte> key = stackalloc byte[32];
        Rfc2898DeriveBytes.Pbkdf2(
            (hasPassword ? password! : DefaultPassphrase).AsSpan(),
            salt,
            key,
            1000,
            HashAlgorithmName.SHA256);

        int outSize = 1 + 1 + 1 + (hasPassword ? 16 : 0) + 12 + compressed.Length + 16;
        byte[] output = new byte[outSize];
        Span<byte> outSpan = output.AsSpan();

        int pos = 0;
        outSpan[pos++] = MagicHeader;
        outSpan[pos++] = Version;
        outSpan[pos++] = hasPassword ? FlagPasswordProtected : FlagDefaultProtected;

        if (hasPassword)
        {
            salt.CopyTo(outSpan.Slice(pos, 16));
            pos += 16;
        }

        iv.CopyTo(outSpan.Slice(pos, 12));
        pos += 12;

        Span<byte> cipherDest = outSpan.Slice(pos, compressed.Length);
        pos += compressed.Length;

        Span<byte> tagDest = outSpan.Slice(pos, 16);

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Encrypt(iv, compressed, cipherDest, tagDest);
        }

        return Base64Url.EncodeToString(output);
    }

    private static PlaybackCredentials ParseTokenPayload(string tokenText, string? password = null)
    {
        byte[] bytes = DecodeTokenBytes(tokenText);
        if (bytes.Length < 3 + 12 + 16 || bytes[0] != MagicHeader)
        {
            throw new FormatException("无效的播放凭证格式");
        }

        ReadOnlySpan<byte> bytesSpan = bytes.AsSpan();
        bool isProtected = bytesSpan[2] == FlagPasswordProtected;
        int offset = 3;

        ReadOnlySpan<byte> saltSpan;
        if (isProtected)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("此播放凭据受密码保护，请输入解密密码");
            }
            saltSpan = bytesSpan.Slice(offset, 16);
            offset += 16;
        }
        else
        {
            saltSpan = s_defaultKeySalt;
        }

        ReadOnlySpan<byte> ivSpan = bytesSpan.Slice(offset, 12);
        offset += 12;

        int totalEncryptedLen = bytes.Length - offset;
        if (totalEncryptedLen < 16)
        {
            throw new FormatException("凭据数据损坏");
        }

        int cipherLen = totalEncryptedLen - 16;
        ReadOnlySpan<byte> cipherSpan = bytesSpan.Slice(offset, cipherLen);
        ReadOnlySpan<byte> tagSpan = bytesSpan.Slice(offset + cipherLen, 16);

        Span<byte> key = stackalloc byte[32];
        Rfc2898DeriveBytes.Pbkdf2(
            (isProtected ? password! : DefaultPassphrase).AsSpan(),
            saltSpan,
            key,
            1000,
            HashAlgorithmName.SHA256);

        byte[] compressed = new byte[cipherLen];
        try
        {
            using var aesGcm = new AesGcm(key, 16);
            aesGcm.Decrypt(ivSpan, cipherSpan, tagSpan, compressed);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("解密失败，密码错误或凭据已损坏");
        }

        byte[] jsonBytes = DecompressZlib(compressed);
        var compact = JsonSerializer.Deserialize(jsonBytes, CredentialsJsonContext.Default.CompactCredentialsPayload)
                      ?? throw new FormatException("凭据解析为空");

        if (string.IsNullOrWhiteSpace(compact.U) && string.IsNullOrWhiteSpace(compact.K) && compact.C.Count == 0)
        {
            throw new FormatException("凭据中未包含有效的账号信息");
        }

        return new PlaybackCredentials
        {
            Uin = compact.U,
            MusicKey = compact.K,
            Cookies = compact.C,
            IsVip = compact.V,
            IsHugeVip = compact.Hv,
            IsSvip = compact.Sv,
            VipExpireAt = compact.Exp,
            ExpireTimestamp = compact.Et,
            Nick = compact.N,
            UpdateTime = compact.T > 0L ? compact.T : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AllowRefresh = compact.Ar
        };
    }

    private static byte[] DecodeTokenBytes(string tokenText)
    {
        return Base64Url.DecodeFromChars(tokenText.AsSpan().Trim());
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] DecompressZlib(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        zlib.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static void LoadFromDisk()
    {
        try
        {
            if (File.Exists(s_storagePath))
            {
                var json = File.ReadAllText(s_storagePath);
                CurrentCredentials = JsonSerializer.Deserialize(json, CredentialsJsonContext.Default.PlaybackCredentials);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackCredentialsManager", $"Failed to load credentials from disk: {ex.Message}");
            CurrentCredentials = null;
        }
    }

    private static void SaveToDisk(PlaybackCredentials? creds)
    {
        try
        {
            if (creds != null)
            {
                var dir = Path.GetDirectoryName(s_storagePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var json = JsonSerializer.Serialize(creds, CredentialsJsonContext.Default.PlaybackCredentials);
                File.WriteAllText(s_storagePath, json);
            }
            else if (File.Exists(s_storagePath))
            {
                File.Delete(s_storagePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackCredentialsManager", $"Failed to save credentials to disk: {ex.Message}");
        }
    }

    internal sealed class CompactCredentialsPayload
    {
        [JsonPropertyName("u")]
        public string U { get; set; } = "";

        [JsonPropertyName("k")]
        public string K { get; set; } = "";

        [JsonPropertyName("c")]
        public Dictionary<string, string> C { get; set; } = [];

        [JsonPropertyName("v")]
        public bool V { get; set; }

        [JsonPropertyName("n")]
        public string N { get; set; } = "";

        [JsonPropertyName("t")]
        public long T { get; set; }

        [JsonPropertyName("hv")]
        public bool Hv { get; set; }

        [JsonPropertyName("sv")]
        public bool Sv { get; set; }

        [JsonPropertyName("exp")]
        public string Exp { get; set; } = "";

        [JsonPropertyName("et")]
        public long Et { get; set; }

        [JsonPropertyName("ar")]
        public bool Ar { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PlaybackCredentials))]
[JsonSerializable(typeof(PlaybackCredentialsManager.CompactCredentialsPayload))]
internal sealed partial class CredentialsJsonContext : JsonSerializerContext
{
}
