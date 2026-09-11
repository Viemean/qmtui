using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// “猜你喜欢”个性化音乐电台服务，管理电台推荐流拉取、去重与平滑推流
/// </summary>
public sealed class RadioService
{
    private static readonly Lazy<RadioService> _lazy = new(() => new RadioService());
    public static RadioService Instance => _lazy.Value;

    private readonly Lock _lock = new();
    private readonly List<Song> _queue = [];
    private int _currentIndex;
    private int _playedCount;
    private bool _isPrefetching;

    private RadioService() { }

    public int PlayedCount
    {
        get
        {
            lock (_lock) return _playedCount;
        }
    }

    public int QueueCount
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    public int CurrentIndex
    {
        get
        {
            lock (_lock) return _currentIndex;
        }
    }

    public bool HasActiveRadio
    {
        get
        {
            lock (_lock) return _queue.Count > 0 && _currentIndex < _queue.Count;
        }
    }

    public bool IsCurrentSongInRadio(Song? song)
    {
        if (song == null || string.IsNullOrEmpty(song.Mid)) return false;
        lock (_lock)
        {
            return _queue.Count > 0 && _currentIndex < _queue.Count && _queue.Any(s => s.Mid == song.Mid);
        }
    }

    /// <summary>
    /// 重置并启动全新电台流，拉取首批歌曲
    /// </summary>
    public async Task<Song?> StartRadioAsync(int batchSize = 5)
    {
        lock (_lock)
        {
            _queue.Clear();
            _currentIndex = 0;
            _playedCount = 0;
        }

        var songs = await MusicApi.GetGuessRecommendSongsAsync(batchSize).ConfigureAwait(false);
        if (songs.Count == 0) return null;

        lock (_lock)
        {
            _queue.AddRange(songs);
            _playedCount = 1;
            _currentIndex = 0;
        }

        if (songs.Count <= 3)
        {
            _ = Task.Run(PrefetchNextBatchAsync);
        }

        return songs[0];
    }

    /// <summary>
    /// 获取下一首电台歌曲；若队列不足自动缓冲下一批
    /// </summary>
    public async Task<Song?> GetNextRadioTrackAsync()
    {
        int count;
        lock (_lock)
        {
            count = _queue.Count;
        }

        if (count == 0)
        {
            return await StartRadioAsync().ConfigureAwait(false);
        }

        lock (_lock)
        {
            _currentIndex++;
        }

        bool needFetch;
        lock (_lock)
        {
            needFetch = _currentIndex >= _queue.Count;
        }

        if (needFetch)
        {
            await PrefetchNextBatchAsync().ConfigureAwait(false);
            lock (_lock)
            {
                if (_currentIndex >= _queue.Count)
                {
                    _currentIndex = 0;
                }
            }
        }

        Song? nextSong = null;
        lock (_lock)
        {
            if (_currentIndex < _queue.Count)
            {
                _playedCount++;
                nextSong = _queue[_currentIndex];
            }
        }

        lock (_lock)
        {
            if (_queue.Count - _currentIndex <= 3)
            {
                _ = Task.Run(PrefetchNextBatchAsync);
            }
        }

        return nextSong;
    }

    /// <summary>
    /// 窥探下一首歌曲（不移动播放游标，用于提前缓存音频）
    /// </summary>
    public Song? PeekNextRadioTrack()
    {
        lock (_lock)
        {
            if (_currentIndex + 1 < _queue.Count)
            {
                return _queue[_currentIndex + 1];
            }
            return null;
        }
    }

    /// <summary>
    /// 后台异步预拉取下一批电台推荐歌曲并去重追加
    /// </summary>
    public async Task PrefetchNextBatchAsync()
    {
        lock (_lock)
        {
            if (_isPrefetching) return;
            _isPrefetching = true;
        }

        try
        {
            var moreSongs = await MusicApi.GetGuessRecommendSongsAsync(5).ConfigureAwait(false);
            if (moreSongs.Count > 0)
            {
                AppendUniqueSongs(moreSongs);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RadioService", $"PrefetchNextBatchAsync error: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isPrefetching = false;
            }
        }
    }

    /// <summary>
    /// 向电台队列追加歌曲并基于 Mid 去重
    /// </summary>
    public int AppendUniqueSongs(IEnumerable<Song> songs)
    {
        lock (_lock)
        {
            var existingMids = new HashSet<string>(_queue.Select(s => s.Mid));
            int addedCount = 0;
            foreach (var s in songs)
            {
                if (!string.IsNullOrEmpty(s.Mid) && !existingMids.Contains(s.Mid))
                {
                    _queue.Add(s);
                    existingMids.Add(s.Mid);
                    addedCount++;
                }
            }
            return addedCount;
        }
    }

    /// <summary>
    /// 重置电台队列状态（供测试与特定生命周期复位使用）
    /// </summary>
    internal void ResetForTest(IEnumerable<Song>? initialSongs = null, int startIndex = 0, int playedCount = 0)
    {
        lock (_lock)
        {
            _queue.Clear();
            if (initialSongs != null)
            {
                _queue.AddRange(initialSongs);
            }
            _currentIndex = startIndex;
            _playedCount = playedCount;
            _isPrefetching = false;
        }
    }
}
