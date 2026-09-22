using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 全局独立播放队列服务，隔离界面展示视图与真实待播队列
/// </summary>
public sealed class PlaybackQueueService
{
    private static readonly Lazy<PlaybackQueueService> _lazy = new(() => new PlaybackQueueService());
    public static PlaybackQueueService Instance => _lazy.Value;

    private readonly Lock _lock = new();
    private readonly List<Song> _activeSongs = [];
    private readonly List<int> _shuffleIndices = [];
    private int _shufflePointer = -1;

    public event Action? QueueChanged;

    public IReadOnlyList<Song> ActiveSongs
    {
        get
        {
            lock (_lock)
            {
                return _activeSongs.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _activeSongs.Count;
            }
        }
    }

    public int CurrentIndex { get; private set; } = -1;

    public Song? CurrentSong
    {
        get
        {
            lock (_lock)
            {
                if (CurrentIndex >= 0 && CurrentIndex < _activeSongs.Count)
                {
                    return _activeSongs[CurrentIndex];
                }
                return null;
            }
        }
    }

    public PlaybackMode Mode { get; set; } = PlaybackMode.ListLoop;
    public PlaybackSourceContext? SourceContext { get; set; }

    private PlaybackQueueService() { }

    /// <summary>
    /// 装载全新播放队列并定位起始播放索引与来源上下文
    /// </summary>
    public void SetQueue(IEnumerable<Song> songs, int startIndex = 0, PlaybackSourceContext? sourceContext = null)
    {
        lock (_lock)
        {
            SourceContext = sourceContext;
            _activeSongs.Clear();
            _activeSongs.AddRange(songs);

            if (_activeSongs.Count == 0)
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
            else
            {
                CurrentIndex = Math.Clamp(startIndex, 0, _activeSongs.Count - 1);
                RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
            }
        }

        QueueChanged?.Invoke();
        SaveQueueDebounced();
    }

    /// <summary>
    /// 将歌曲插队至下一顺位播放
    /// </summary>
    public void InsertNext(Song song)
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0)
            {
                _activeSongs.Add(song);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
            else
            {
                int insertIdx = CurrentIndex >= 0 ? CurrentIndex + 1 : 0;
                _activeSongs.Insert(insertIdx, song);

                // 更新 Shuffle 索引映射
                if (_shuffleIndices.Count > 0)
                {
                    for (int i = 0; i < _shuffleIndices.Count; i++)
                    {
                        if (_shuffleIndices[i] >= insertIdx)
                        {
                            _shuffleIndices[i]++;
                        }
                    }

                    int insertShufflePos = _shufflePointer >= 0 ? _shufflePointer + 1 : 0;
                    _shuffleIndices.Insert(insertShufflePos, insertIdx);
                }
                else
                {
                    RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
                }
            }
        }

        AppLogger.Info("PlaybackQueue", $"Song inserted next: {song.Title} - {song.Artist}");
        QueueChanged?.Invoke();
        SaveQueueDebounced();
    }

    /// <summary>
    /// 从队列中移除指定索引的歌曲
    /// </summary>
    public bool RemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _activeSongs.Count) return false;

            _activeSongs.RemoveAt(index);

            if (_activeSongs.Count == 0)
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
            else
            {
                if (index < CurrentIndex)
                {
                    CurrentIndex--;
                }
                else if (CurrentIndex >= _activeSongs.Count)
                {
                    CurrentIndex = _activeSongs.Count - 1;
                }

                // 重建或修正 Shuffle 队列
                RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
            }
        }

        QueueChanged?.Invoke();
        SaveQueueDebounced();
        return true;
    }

    /// <summary>
    /// 清空所有待播歌曲（保留当前正在播放的曲目）
    /// </summary>
    public void ClearUpcoming()
    {
        lock (_lock)
        {
            if (_activeSongs.Count <= 1) return;

            var current = CurrentSong;
            _activeSongs.Clear();
            if (current != null)
            {
                _activeSongs.Add(current);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
            else
            {
                CurrentIndex = -1;
                _shuffleIndices.Clear();
                _shufflePointer = -1;
            }
        }

        QueueChanged?.Invoke();
        SaveQueueDebounced();
    }

    /// <summary>
    /// 推演并切换至下一首播放曲目
    /// </summary>
    public Song? GetNextSong(bool isAutoPlayback = false)
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0) return null;

            // 单曲循环且为播放完毕自动切歌
            if (Mode == PlaybackMode.SingleLoop && isAutoPlayback)
            {
                return CurrentSong;
            }

            // 随机播放模式
            if (Mode == PlaybackMode.Shuffle && _activeSongs.Count > 1)
            {
                EnsureShuffleQueue();

                _shufflePointer++;
                if (_shufflePointer >= _shuffleIndices.Count)
                {
                    int lastSongIdx = _shuffleIndices[^1];
                    RebuildShuffleQueue(_activeSongs.Count, -1);
                    if (_shuffleIndices.Count > 1 && _shuffleIndices[0] == lastSongIdx)
                    {
                        int swapTarget = 1 + Random.Shared.Next(_shuffleIndices.Count - 1);
                        (_shuffleIndices[0], _shuffleIndices[swapTarget]) = (_shuffleIndices[swapTarget], _shuffleIndices[0]);
                    }
                    _shufflePointer = 0;
                }

                int nextIdx = _shuffleIndices[_shufflePointer];
                CurrentIndex = nextIdx;
                return _activeSongs[CurrentIndex];
            }

            // 列表循环模式 (或单曲循环下主动按切歌键)
            CurrentIndex = (CurrentIndex + 1) % _activeSongs.Count;
            return _activeSongs[CurrentIndex];
        }
    }

    /// <summary>
    /// 预先窥视下一首曲目（用于平滑预加载，不改变内部游标）
    /// </summary>
    public Song? PeekNextSong()
    {
        lock (_lock)
        {
            if (_activeSongs.Count <= 1) return null;

            if (Mode == PlaybackMode.Shuffle)
            {
                EnsureShuffleQueue();
                if (_shufflePointer >= 0 && _shufflePointer + 1 < _shuffleIndices.Count)
                {
                    int nextIdx = _shuffleIndices[_shufflePointer + 1];
                    if (nextIdx >= 0 && nextIdx < _activeSongs.Count)
                    {
                        return _activeSongs[nextIdx];
                    }
                }
                return null;
            }

            int next = (CurrentIndex + 1) % _activeSongs.Count;
            return _activeSongs[next];
        }
    }

    /// <summary>
    /// 预先窥视上一首曲目（用于上位机状态同步与前后卡片预渲染，不改变内部游标）
    /// </summary>
    public Song? PeekPrevSong()
    {
        lock (_lock)
        {
            if (_activeSongs.Count <= 1) return null;

            if (Mode == PlaybackMode.Shuffle)
            {
                EnsureShuffleQueue();
                if (_shufflePointer > 0)
                {
                    int prevIdx = _shuffleIndices[_shufflePointer - 1];
                    if (prevIdx >= 0 && prevIdx < _activeSongs.Count)
                    {
                        return _activeSongs[prevIdx];
                    }
                }
                else if (_shuffleIndices.Count > 0)
                {
                    int prevIdx = _shuffleIndices[_shuffleIndices.Count - 1];
                    if (prevIdx >= 0 && prevIdx < _activeSongs.Count)
                    {
                        return _activeSongs[prevIdx];
                    }
                }
                return null;
            }

            int prev = (CurrentIndex - 1 + _activeSongs.Count) % _activeSongs.Count;
            return _activeSongs[prev];
        }
    }

    /// <summary>
    /// 推演并切换至上一首播放曲目
    /// </summary>
    public Song? GetPrevSong()
    {
        lock (_lock)
        {
            if (_activeSongs.Count == 0) return null;

            if (Mode == PlaybackMode.Shuffle && _activeSongs.Count > 1)
            {
                EnsureShuffleQueue();
                if (_shufflePointer > 0)
                {
                    _shufflePointer--;
                }
                else
                {
                    _shufflePointer = _shuffleIndices.Count - 1;
                }

                int prevIdx = _shuffleIndices[_shufflePointer];
                CurrentIndex = prevIdx;
                return _activeSongs[CurrentIndex];
            }

            CurrentIndex = (CurrentIndex - 1 + _activeSongs.Count) % _activeSongs.Count;
            return _activeSongs[CurrentIndex];
        }
    }

    /// <summary>
    /// 定位至队列中的指定索引曲目
    /// </summary>
    public Song? SetCurrentIndex(int index)
    {
        Song? selected = null;
        lock (_lock)
        {
            if (index < 0 || index >= _activeSongs.Count) return null;

            CurrentIndex = index;
            if (Mode == PlaybackMode.Shuffle)
            {
                EnsureShuffleQueue();
                int p = _shuffleIndices.IndexOf(CurrentIndex);
                if (p >= 0)
                {
                    _shufflePointer = p;
                }
            }

            selected = _activeSongs[CurrentIndex];
        }

        QueueChanged?.Invoke();
        SaveQueueDebounced();
        return selected;
    }

    /// <summary>
    /// 对齐外部正在播放的歌曲
    /// </summary>
    public void SyncCurrentSong(Song song)
    {
        lock (_lock)
        {
            int idx = -1;
            for (int i = 0; i < _activeSongs.Count; i++)
            {
                if (_activeSongs[i].Mid == song.Mid)
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                CurrentIndex = idx;
                if (Mode == PlaybackMode.Shuffle)
                {
                    EnsureShuffleQueue();
                    int p = _shuffleIndices.IndexOf(CurrentIndex);
                    if (p >= 0) _shufflePointer = p;
                }
            }
            else
            {
                // 若队列中不存在该歌曲，作为单曲队列初始化
                _activeSongs.Clear();
                _activeSongs.Add(song);
                CurrentIndex = 0;
                _shuffleIndices.Clear();
                _shuffleIndices.Add(0);
                _shufflePointer = 0;
            }
        }
        QueueChanged?.Invoke();
        SaveQueueDebounced();
    }

    private void EnsureShuffleQueue()
    {
        if (_shuffleIndices.Count == _activeSongs.Count && _shufflePointer >= 0 && _shufflePointer < _shuffleIndices.Count)
        {
            if (CurrentIndex >= 0 && _shuffleIndices[_shufflePointer] != CurrentIndex)
            {
                int p = _shuffleIndices.IndexOf(CurrentIndex);
                if (p >= 0) _shufflePointer = p;
            }
            return;
        }

        RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
    }

    private void RebuildShuffleQueue(int count, int currentIdx)
    {
        _shuffleIndices.Clear();
        for (int i = 0; i < count; i++)
        {
            _shuffleIndices.Add(i);
        }

        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (_shuffleIndices[i], _shuffleIndices[j]) = (_shuffleIndices[j], _shuffleIndices[i]);
        }

        if (currentIdx >= 0)
        {
            int p = _shuffleIndices.IndexOf(currentIdx);
            if (p >= 0)
            {
                (_shuffleIndices[0], _shuffleIndices[p]) = (_shuffleIndices[p], _shuffleIndices[0]);
            }
            _shufflePointer = 0;
        }
        else
        {
            _shufflePointer = -1;
        }
    }

    private static readonly string s_queueFilePath = Path.Combine(AppPathHelper.ConfigDir, "queue.json");
    private static readonly Lock s_queueFileLock = new();
    private static int s_queueSaveScheduled;
    private static volatile string? s_pendingQueueJsonSnapshot;

    public void SaveQueue()
    {
        try
        {
            var json = ToQueueJson();
            lock (s_queueFileLock)
            {
                var dir = Path.GetDirectoryName(s_queueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(s_queueFilePath, json);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackQueue", $"Failed to save queue: {ex.Message}");
        }
    }

    public void SaveQueueDebounced(int delayMs = 3000)
    {
        s_pendingQueueJsonSnapshot = ToQueueJson();

        if (Interlocked.CompareExchange(ref s_queueSaveScheduled, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref s_queueSaveScheduled, 0);
                    var snapshot = s_pendingQueueJsonSnapshot;
                    if (!string.IsNullOrEmpty(snapshot))
                    {
                        try
                        {
                            lock (s_queueFileLock)
                            {
                                var dir = Path.GetDirectoryName(s_queueFilePath);
                                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                {
                                    Directory.CreateDirectory(dir);
                                }
                                File.WriteAllText(s_queueFilePath, snapshot);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("PlaybackQueue", $"Failed to save debounced queue: {ex.Message}");
                        }
                    }
                }
            });
        }
    }

    internal string ToQueueJson()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"current_index\":").Append(CurrentIndex).Append(",\"songs\":[");

            for (int i = 0; i < _activeSongs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var s = _activeSongs[i];
                sb.Append('{')
                  .Append("\"mid\":\"").Append(JsonEscape(s.Mid)).Append("\",")
                  .Append("\"title\":\"").Append(JsonEscape(s.Title)).Append("\",")
                  .Append("\"artist\":\"").Append(JsonEscape(s.Artist)).Append("\",")
                  .Append("\"album\":\"").Append(JsonEscape(s.Album)).Append("\",")
                  .Append("\"duration\":").Append(s.Duration).Append(',')
                  .Append("\"media_mid\":\"").Append(JsonEscape(s.MediaMid)).Append("\",")
                  .Append("\"id\":").Append(s.Id).Append(',')
                  .Append("\"album_mid\":\"").Append(JsonEscape(s.AlbumMid)).Append("\",")
                  .Append("\"local_file_path\":\"").Append(JsonEscape(s.LocalFilePath ?? "")).Append("\",")
                  .Append("\"webdav_href\":\"").Append(JsonEscape(s.WebDavHref ?? "")).Append("\",")
                  .Append("\"webdav_server_id\":\"").Append(JsonEscape(s.WebDavServerId ?? "")).Append('\"')
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }

    public void LoadQueue()
    {
        try
        {
            if (!File.Exists(s_queueFilePath)) return;
            var json = File.ReadAllText(s_queueFilePath);
            LoadQueueFromJson(json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackQueue", $"Failed to load queue: {ex.Message}");
        }
    }

    internal void LoadQueueFromJson(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            int savedIndex = root.TryGetProperty("current_index", out var idxEl) ? idxEl.GetInt32() : -1;
            var songs = new List<Song>();

            if (root.TryGetProperty("songs", out var songsEl) && songsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in songsEl.EnumerateArray())
                {
                    string mid = item.TryGetProperty("mid", out var m) ? m.GetString() ?? "" : "";
                    string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string artist = item.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
                    string album = item.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";
                    int duration = item.TryGetProperty("duration", out var d) ? d.GetInt32() : 0;
                    string mediaMid = item.TryGetProperty("media_mid", out var mm) ? mm.GetString() ?? "" : "";
                    long id = item.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idVal) ? idVal : 0;
                    string albumMid = item.TryGetProperty("album_mid", out var amEl) ? amEl.GetString() ?? "" : "";
                    string? local = item.TryGetProperty("local_file_path", out var locEl) ? locEl.GetString() : null;
                    string? wdHref = item.TryGetProperty("webdav_href", out var wdHEl) ? wdHEl.GetString() : null;
                    string? wdServer = item.TryGetProperty("webdav_server_id", out var wdSEl) ? wdSEl.GetString() : null;

                    if (!string.IsNullOrEmpty(mid) || !string.IsNullOrEmpty(title))
                    {
                        var s = new Song(mid, title, artist, album, duration, mediaMid, id, albumMid)
                        {
                            LocalFilePath = string.IsNullOrEmpty(local) ? null : local,
                            WebDavHref = string.IsNullOrEmpty(wdHref) ? null : wdHref,
                            WebDavServerId = string.IsNullOrEmpty(wdServer) ? null : wdServer
                        };
                        songs.Add(s);
                    }
                }
            }

            if (songs.Count > 0)
            {
                lock (_lock)
                {
                    _activeSongs.Clear();
                    _activeSongs.AddRange(songs);
                    CurrentIndex = Math.Clamp(savedIndex, 0, _activeSongs.Count - 1);
                    RebuildShuffleQueue(_activeSongs.Count, CurrentIndex);
                }
                QueueChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PlaybackQueue", $"Failed to parse queue json: {ex.Message}");
        }
    }

    private static string JsonEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
