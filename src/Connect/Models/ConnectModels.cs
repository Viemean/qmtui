using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using QmTui.Models;

namespace QmTui.Connect.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectDeviceType>))]
public enum ConnectDeviceType
{
    TV,
    MOBILE
}

public sealed record ConnectDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] ConnectDeviceType Type,
    [property: JsonPropertyName("host")] string Host = "",
    [property: JsonPropertyName("port")] int Port = 8765,
    [property: JsonPropertyName("token")] string Token = "",
    [property: JsonPropertyName("pairedAt")] long PairedAt = 0
);

[JsonConverter(typeof(JsonStringEnumConverter<AudioSourceType>))]
public enum AudioSourceType
{
    DIRECT_API,
    STREAM_PROXY,
    DIRECT_WEBDAV
}

public sealed record AudioSourceDescriptor(
    [property: JsonPropertyName("sourceType")] AudioSourceType SourceType,
    [property: JsonPropertyName("streamUrl")] string? StreamUrl = null,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null
);

public sealed record ConnectMessage(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("payload")] string Payload = "",
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("timestamp")] long Timestamp = 0
)
{
    public static ConnectMessage Create(string action, string payload = "") =>
        new(action, payload, null, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ConnectMessage Create<T>(string action, T data, JsonTypeInfo<T> jsonTypeInfo)
    {
        var jsonStr = JsonSerializer.Serialize(data, jsonTypeInfo);
        using var doc = JsonDocument.Parse(jsonStr);
        return new ConnectMessage(
            Action: action,
            Payload: jsonStr,
            Data: doc.RootElement.Clone(),
            Id: Guid.NewGuid().ToString(),
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );
    }

    public T? DecodeData<T>(JsonTypeInfo<T> jsonTypeInfo)
    {
        if (Data.HasValue && Data.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            try
            {
                return JsonSerializer.Deserialize(Data.Value, jsonTypeInfo);
            }
            catch
            {
                // 回退至 Payload 反序列化
            }
        }

        if (!string.IsNullOrWhiteSpace(Payload))
        {
            try
            {
                return JsonSerializer.Deserialize(Payload, jsonTypeInfo);
            }
            catch
            {
                return default;
            }
        }

        return default;
    }
}

public static class ConnectActions
{
    public const string PairRequest = "pair_request";
    public const string PairResponse = "pair_response";
    public const string Disconnect = "disconnect";
    public const string Ping = "ping";
    public const string Pong = "pong";

    public const string ReqGetPlayerState = "req_get_player_state";
    public const string ReqGetQueueState = "req_get_queue_state";

    public const string CmdPlaySong = "cmd_play_song";
    public const string CmdEnqueueNext = "cmd_enqueue_next";
    public const string CmdPause = "cmd_pause";
    public const string CmdResume = "cmd_resume";
    public const string CmdSeek = "cmd_seek";
    public const string CmdPrevious = "cmd_prev";
    public const string CmdNext = "cmd_next";
    public const string CmdSetVolume = "cmd_set_volume";
    public const string CmdSwitchTier = "cmd_switch_tier";
    public const string CmdTriggerAod = "cmd_trigger_aod";
    public const string CmdCycleLoopMode = "cmd_cycle_loop_mode";
    public const string CmdOpenPlayer = "cmd_open_player";
    public const string CmdGestureSwipe = "cmd_gesture_swipe";
    public const string CmdToggleFavorite = "cmd_toggle_favorite";
    public const string CmdSyncLyricsScroll = "cmd_sync_lyrics_scroll";
    public const string CmdSyncLyrics = "cmd_sync_lyrics";

    public const string EventPlayState = "event_play_state";
    public const string EventQueueState = "event_queue_state";
    public const string EventSyncLyrics = "event_sync_lyrics";
}

public sealed record PairRequestPayload(
    [property: JsonPropertyName("device")] ConnectDevice Device,
    [property: JsonPropertyName("pinCode")] string PinCode = ""
);

public sealed record PairResponsePayload(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("message")] string Message = "",
    [property: JsonPropertyName("device")] ConnectDevice? Device = null
);

public sealed record ConnectArtist(
    [property: JsonPropertyName("id")] long Id = 0,
    [property: JsonPropertyName("mid")] string Mid = "",
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("avatarUrl")] string AvatarUrl = ""
);

public sealed record ConnectSong(
    [property: JsonPropertyName("songId")] long SongId = 0,
    [property: JsonPropertyName("songMid")] string SongMid = "",
    [property: JsonPropertyName("name")] string Name = "",
    [property: JsonPropertyName("singer")] string Singer = "",
    [property: JsonPropertyName("album")] string Album = "",
    [property: JsonPropertyName("albumMid")] string AlbumMid = "",
    [property: JsonPropertyName("durationSeconds")] int DurationSeconds = 0,
    [property: JsonPropertyName("currentTier")] string CurrentTier = "SQ",
    [property: JsonPropertyName("availableTiers")] List<string>? AvailableTiers = null,
    [property: JsonPropertyName("isVip")] bool IsVip = false,
    [property: JsonPropertyName("coverUrl")] string CoverUrl = "",
    [property: JsonPropertyName("mediaMid")] string MediaMid = "",
    [property: JsonPropertyName("singerList")] List<ConnectArtist>? SingerList = null,
    [property: JsonPropertyName("visualMid")] string VisualMid = "",
    [property: JsonPropertyName("localFilePath")] string? LocalFilePath = null,
    [property: JsonPropertyName("isLocal")] bool IsLocal = false,
    [property: JsonPropertyName("isWebDav")] bool IsWebDav = false
)
{
    public Song ToDomainSong()
    {
        var mid = SongMid ?? "";
        bool isPcLocal = mid.StartsWith("pc_local_", StringComparison.OrdinalIgnoreCase);
        if (isPcLocal)
        {
            mid = "local_" + mid["pc_local_".Length..];
        }

        var localPath = LocalFilePath;
        if (string.IsNullOrEmpty(localPath) && !string.IsNullOrEmpty(MediaMid) && MediaMid.Contains("/stream/local"))
        {
            try
            {
                var qIdx = MediaMid.IndexOf("path=", StringComparison.OrdinalIgnoreCase);
                if (qIdx >= 0)
                {
                    var pathPart = MediaMid[(qIdx + 5)..].Split('&')[0];
                    var decoded = Uri.UnescapeDataString(pathPart);
                    if (!string.IsNullOrEmpty(decoded))
                    {
                        localPath = decoded;
                    }
                }
            }
            catch { }
        }

        var singers = SingerList?.Select(a => new ArtistInfo(a.Name, a.Mid, a.Id)).ToList() ?? [];
        var song = new Song(
            Mid: mid,
            Title: string.IsNullOrEmpty(Name) ? "未知曲目" : Name,
            Artist: string.IsNullOrEmpty(Singer) ? "未知歌手" : Singer,
            Album: string.IsNullOrEmpty(Album) ? "" : Album,
            Duration: DurationSeconds,
            MediaMid: string.IsNullOrEmpty(MediaMid) ? mid : MediaMid,
            Id: SongId,
            AlbumMid: string.IsNullOrEmpty(AlbumMid) ? "" : AlbumMid
        )
        {
            LocalFilePath = localPath,
            CoverUrl = CoverUrl,
            Singers = singers
        };

        if (IsWebDav || mid.StartsWith("webdav_", StringComparison.OrdinalIgnoreCase))
        {
            song.WebDavHref = !string.IsNullOrEmpty(MediaMid) ? MediaMid : (localPath ?? mid);
            if (mid.StartsWith("webdav_", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = mid["webdav_".Length..];
                var lastIdx = trimmed.LastIndexOf('_');
                song.WebDavServerId = lastIdx > 0 ? trimmed[..lastIdx] : trimmed;
            }
        }

        return song;
    }

    public static ConnectSong FromDomainSong(Song song, AudioQualityTier tier = AudioQualityTier.SQ, int port = 8765)
    {
        var singerList = song.Singers.Select(s => new ConnectArtist(s.Id, s.Mid, s.Name, "")).ToList();

        // 仅当物理音频文件在当前 PC 本地磁盘真实存在且非 WebDAV 时，才属于 PC 本地托管音频 (pc_local_)
        bool isPcLocalFile = !song.IsWebDav && !string.IsNullOrEmpty(song.LocalFilePath) && File.Exists(song.LocalFilePath);

        string effectiveSongMid = song.Mid;
        bool effectiveIsLocal = song.IsLocal;
        string effectiveMediaMid = song.IsWebDav && !string.IsNullOrEmpty(song.WebDavHref)
            ? song.WebDavHref
            : song.EffectiveMediaMid;

        if (isPcLocalFile)
        {
            // 依据规范 7.3：PC 物理音频文件广播给移动端时，添加 pc_local_ 前缀，isLocal 设为 false，mediaMid 提供 PC 端的 /stream/local 直通流
            effectiveIsLocal = false;
            var rawMid = song.Mid.StartsWith("local_", StringComparison.OrdinalIgnoreCase)
                ? song.Mid["local_".Length..]
                : song.Mid;
            effectiveSongMid = $"pc_local_{rawMid}";

            var localIp = QmTui.Connect.Discovery.ConnectMdnsService.GetBestLocalIpAddress();
            effectiveMediaMid = $"http://{localIp}:{port}/stream/local?path={Uri.EscapeDataString(song.LocalFilePath!)}";
        }
        else
        {
            // 若为移动端本地曲目（文件在手机端，如 /storage/emulated/0/...，PC 本地物理文件不存在）：
            // 严禁标记为 pc_local_，严禁伪造 PC 本地流地址；保持 local_ 前缀与 isLocal=true，使手机端识别为手机自有文件
            if (effectiveSongMid.StartsWith("pc_local_", StringComparison.OrdinalIgnoreCase))
            {
                effectiveSongMid = "local_" + effectiveSongMid["pc_local_".Length..];
                effectiveIsLocal = true;
            }
            if (effectiveMediaMid.Contains("/stream/local") && effectiveMediaMid.Contains($":{port}/"))
            {
                // 清除指向本机不可用端口的无效流地址，防止手机端直接复用导致 404
                effectiveMediaMid = effectiveSongMid;
            }
        }

        string cover = ResolveCoverUrl(song, port);
        return new ConnectSong(
            SongId: song.Id,
            SongMid: effectiveSongMid,
            Name: song.Title,
            Singer: song.Artist,
            Album: song.Album,
            AlbumMid: song.AlbumMid,
            DurationSeconds: song.Duration,
            CurrentTier: tier.ToString(),
            CoverUrl: cover,
            MediaMid: effectiveMediaMid,
            SingerList: singerList,
            LocalFilePath: song.LocalFilePath,
            IsLocal: effectiveIsLocal,
            IsWebDav: song.IsWebDav
        );
    }

    public static string ResolveCoverUrl(Song song, int port = 8765)
    {
        if (!string.IsNullOrEmpty(song.AlbumMid))
        {
            return $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{song.AlbumMid}.jpg?max_age=2592000";
        }
        var firstSingerMid = song.Singers.FirstOrDefault(s => !string.IsNullOrEmpty(s.Mid))?.Mid;
        if (!string.IsNullOrEmpty(firstSingerMid))
        {
            return $"https://y.gtimg.cn/music/photo_new/T001R800x800M000{firstSingerMid}.jpg?max_age=2592000";
        }
        if (!string.IsNullOrEmpty(song.CoverUrl))
        {
            if (song.CoverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                song.CoverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!song.CoverUrl.Contains("/cover/current"))
                {
                    return song.CoverUrl;
                }
            }
        }
        if (!string.IsNullOrEmpty(song.Mid))
        {
            var ip = QmTui.Connect.Discovery.ConnectMdnsService.GetBestLocalIpAddress();
            return $"http://{ip}:{port}/cover?mid={Uri.EscapeDataString(song.Mid)}";
        }
        return "";
    }
}

public sealed record PlaySongCommand(
    [property: JsonPropertyName("song")] ConnectSong Song,
    [property: JsonPropertyName("queue")] List<ConnectSong>? Queue = null,
    [property: JsonPropertyName("index")] int Index = 0,
    [property: JsonPropertyName("startPositionMs")] long StartPositionMs = 0,
    [property: JsonPropertyName("audioSource")] AudioSourceDescriptor? AudioSource = null,
    [property: JsonPropertyName("qualityTier")] string? QualityTier = null
);

public sealed record SwitchTierCommand(
    [property: JsonPropertyName("tier")] string Tier
);

public sealed record EnqueueNextCommand(
    [property: JsonPropertyName("song")] ConnectSong Song,
    [property: JsonPropertyName("audioSource")] AudioSourceDescriptor? AudioSource = null
);

public sealed record SeekCommand(
    [property: JsonPropertyName("positionMs")] long PositionMs
);

public sealed record SetVolumeCommand(
    [property: JsonPropertyName("volume")] float Volume
);

public sealed record GestureSwipeCommand(
    [property: JsonPropertyName("direction")] string Direction = ""
);

public sealed record GestureSwipePayload(
    [property: JsonPropertyName("state")] string State = "IDLE",
    [property: JsonPropertyName("fraction")] float Fraction = 0f,
    [property: JsonPropertyName("targetFraction")] float TargetFraction = 0f,
    [property: JsonPropertyName("durationMs")] long DurationMs = 200,
    [property: JsonPropertyName("timestamp")] long Timestamp = 0
);

public sealed record ToggleFavoriteCommand(
    [property: JsonPropertyName("song")] ConnectSong? Song = null,
    [property: JsonPropertyName("songMid")] string SongMid = "",
    [property: JsonPropertyName("isFavorite")] bool? IsFavorite = null
);

public sealed record LyricsScrollPayload(
    [property: JsonPropertyName("lineIndex")] int LineIndex = 0,
    [property: JsonPropertyName("isUserScrolling")] bool IsUserScrolling = false,
    [property: JsonPropertyName("timestamp")] long Timestamp = 0
);

public sealed record ConnectLyricLine(
    [property: JsonPropertyName("timestampMs")] long TimestampMs,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("transText")] string TransText = ""
);

public sealed record LyricsSyncPayload(
    [property: JsonPropertyName("songMid")] string SongMid,
    [property: JsonPropertyName("title")] string Title = "",
    [property: JsonPropertyName("singer")] string Singer = "",
    [property: JsonPropertyName("lyrics")] List<ConnectLyricLine>? Lyrics = null,
    [property: JsonPropertyName("sourceDeviceId")] string SourceDeviceId = "",
    [property: JsonPropertyName("timestamp")] long Timestamp = 0,
    [property: JsonPropertyName("lyricOffsetMs")] long LyricOffsetMs = 0
);

public sealed record PlayerStateEvent(
    [property: JsonPropertyName("currentSong")] ConnectSong? CurrentSong = null,
    [property: JsonPropertyName("isPlaying")] bool IsPlaying = false,
    [property: JsonPropertyName("positionMs")] long PositionMs = 0,
    [property: JsonPropertyName("durationMs")] long DurationMs = 0,
    [property: JsonPropertyName("volume")] float Volume = 1.0f,
    [property: JsonPropertyName("queueSize")] int QueueSize = 0,
    [property: JsonPropertyName("currentIndex")] int CurrentIndex = -1,
    [property: JsonPropertyName("loopMode")] string LoopMode = "ListRepeat",
    [property: JsonPropertyName("isAodActive")] bool IsAodActive = false,
    [property: JsonPropertyName("prevSong")] ConnectSong? PrevSong = null,
    [property: JsonPropertyName("nextSong")] ConnectSong? NextSong = null,
    [property: JsonPropertyName("currentTier")] string? CurrentTier = null,
    [property: JsonPropertyName("availableTiers")] List<string>? AvailableTiers = null,
    [property: JsonPropertyName("isFavorite")] bool IsFavorite = false,
    [property: JsonPropertyName("isRadioMode")] bool IsRadioMode = false,
    [property: JsonPropertyName("lyricOffsetMs")] long LyricOffsetMs = 0,
    [property: JsonPropertyName("lyrics")] string? Lyrics = null
);

public sealed record QueueStateEvent(
    [property: JsonPropertyName("queue")] List<ConnectSong> Queue,
    [property: JsonPropertyName("currentIndex")] int CurrentIndex = -1
);

public sealed record QrPairData(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("pinCode")] string PinCode
);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ConnectDeviceType))]
[JsonSerializable(typeof(AudioSourceType))]
[JsonSerializable(typeof(ConnectMessage))]
[JsonSerializable(typeof(ConnectDevice))]
[JsonSerializable(typeof(PairRequestPayload))]
[JsonSerializable(typeof(PairResponsePayload))]
[JsonSerializable(typeof(PlaySongCommand))]
[JsonSerializable(typeof(SwitchTierCommand))]
[JsonSerializable(typeof(EnqueueNextCommand))]
[JsonSerializable(typeof(SeekCommand))]
[JsonSerializable(typeof(SetVolumeCommand))]
[JsonSerializable(typeof(GestureSwipeCommand))]
[JsonSerializable(typeof(GestureSwipePayload))]
[JsonSerializable(typeof(ToggleFavoriteCommand))]
[JsonSerializable(typeof(LyricsScrollPayload))]
[JsonSerializable(typeof(ConnectLyricLine))]
[JsonSerializable(typeof(List<ConnectLyricLine>))]
[JsonSerializable(typeof(LyricsSyncPayload))]
[JsonSerializable(typeof(PlayerStateEvent))]
[JsonSerializable(typeof(QueueStateEvent))]
[JsonSerializable(typeof(QrPairData))]
[JsonSerializable(typeof(ConnectSong))]
[JsonSerializable(typeof(List<ConnectSong>))]
[JsonSerializable(typeof(List<ConnectDevice>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class ConnectJsonContext : JsonSerializerContext
{
}
