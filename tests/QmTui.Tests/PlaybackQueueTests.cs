using System.Collections.Generic;
using System.Linq;
using QmTui.Models;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

[Collection("PlaybackQueue")]
public class PlaybackQueueTests
{
    private static List<Song> MakeSongs(int count) =>
        Enumerable.Range(1, count)
                  .Select(i => new Song($"mid_{i}", $"Title {i}", $"Artist {i}", $"Album {i}", 200, "", i))
                  .ToList();

    private static PlaybackQueueService Q => PlaybackQueueService.Instance;

    // ── 空队列边界 ───────────────────────────────────────────────

    [Fact]
    public void EmptyQueue_AllQueriesReturnNull()
    {
        Q.SetQueue([]);
        Assert.Null(Q.CurrentSong);
        Assert.Null(Q.GetNextSong());
        Assert.Null(Q.GetPrevSong());
        Assert.Null(Q.PeekNextSong());
        Assert.Equal(0, Q.Count);
    }

    // ── 列表循环模式 ─────────────────────────────────────────────

    [Fact]
    public void ListLoop_GetNext_WrapsAroundFromLast()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 2);  // 从 mid_3 开始
        Q.Mode = PlaybackMode.ListLoop;

        var next = Q.GetNextSong(isAutoPlayback: true);
        Assert.NotNull(next);
        Assert.Equal("mid_1", next.Mid);   // 循环回到头部
    }

    [Fact]
    public void ListLoop_GetPrev_WrapsAroundFromFirst()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 0);
        Q.Mode = PlaybackMode.ListLoop;

        var prev = Q.GetPrevSong();
        Assert.NotNull(prev);
        Assert.Equal("mid_3", prev.Mid);   // 循环到末尾
    }

    // ── 单曲循环模式 ─────────────────────────────────────────────

    [Fact]
    public void SingleLoop_AutoPlayback_ReturnsSameSong()
    {
        Q.SetQueue(MakeSongs(5), startIndex: 2);
        Q.Mode = PlaybackMode.SingleLoop;

        var current = Q.CurrentSong!.Mid;
        var next = Q.GetNextSong(isAutoPlayback: true);
        Assert.NotNull(next);
        Assert.Equal(current, next.Mid);   // 自动切歌仍停留在当前曲
    }

    [Fact]
    public void SingleLoop_ManualNext_AdvancesToNextSong()
    {
        Q.SetQueue(MakeSongs(5), startIndex: 0);
        Q.Mode = PlaybackMode.SingleLoop;

        var next = Q.GetNextSong(isAutoPlayback: false);
        Assert.NotNull(next);
        Assert.Equal("mid_2", next.Mid);   // 手动切歌前进一首
    }

    // ── InsertNext ───────────────────────────────────────────────

    [Fact]
    public void InsertNext_PlacesSongImmediatelyAfterCurrent()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 0);
        Q.Mode = PlaybackMode.ListLoop;

        var insertSong = new Song("inserted", "Inserted", "Art", "Alb", 100, "", 99);
        Q.InsertNext(insertSong);

        Assert.Equal(4, Q.Count);
        var next = Q.GetNextSong(isAutoPlayback: false);
        Assert.NotNull(next);
        Assert.Equal("inserted", next.Mid);
    }

    [Fact]
    public void InsertNext_IntoEmptyQueue_BecomesCurrent()
    {
        Q.SetQueue([]);
        var song = new Song("only", "Only", "Art", "Alb", 100, "", 1);
        Q.InsertNext(song);

        Assert.Equal(1, Q.Count);
        Assert.NotNull(Q.CurrentSong);
        Assert.Equal("only", Q.CurrentSong.Mid);
    }

    // ── RemoveAt ─────────────────────────────────────────────────

    [Fact]
    public void RemoveAt_OutOfRange_ReturnsFalse()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 1);
        Assert.False(Q.RemoveAt(-1));
        Assert.False(Q.RemoveAt(99));
        Assert.Equal(3, Q.Count);
    }

    [Fact]
    public void RemoveAt_BeforeCurrent_DecrementsCurrentIndex()
    {
        Q.SetQueue(MakeSongs(4), startIndex: 2);   // CurrentIndex = 2 (mid_3)
        Q.Mode = PlaybackMode.ListLoop;
        var currentMid = Q.CurrentSong!.Mid;

        Q.RemoveAt(0);   // 删除 mid_1（在 current 之前）

        Assert.Equal(3, Q.Count);
        Assert.Equal(currentMid, Q.CurrentSong?.Mid);  // 当前歌曲不变
    }

    [Fact]
    public void RemoveAt_LastItem_QueueBecomesEmpty()
    {
        Q.SetQueue(MakeSongs(1), startIndex: 0);
        Q.RemoveAt(0);

        Assert.Equal(0, Q.Count);
        Assert.Null(Q.CurrentSong);
    }

    // ── ClearUpcoming ─────────────────────────────────────────────

    [Fact]
    public void ClearUpcoming_KeepsCurrentSongOnly()
    {
        Q.SetQueue(MakeSongs(5), startIndex: 2);
        var currentMid = Q.CurrentSong!.Mid;

        Q.ClearUpcoming();

        Assert.Equal(1, Q.Count);
        Assert.Equal(currentMid, Q.CurrentSong?.Mid);
    }

    [Fact]
    public void ClearUpcoming_OnSingleSongQueue_DoesNothing()
    {
        Q.SetQueue(MakeSongs(1), startIndex: 0);
        Q.ClearUpcoming();
        Assert.Equal(1, Q.Count);
    }

    // ── 队列持久化往返 ─────────────────────────────────────────────

    [Fact]
    public void Queue_SerializationRoundtrip_PreservesSongsAndCurrentIndex()
    {
        var songs = new List<Song>
        {
            new("mid_1", "Song 1", "Artist 1", "Album 1", 180, "media_1", 1001, "album_mid_1")
            {
                LocalFilePath = "/music/song1.flac"
            },
            new("mid_2", "Song 2", "Artist 2", "Album 2", 240, "media_2", 1002, "album_mid_2")
            {
                WebDavHref = "/webdav/song2.mp3",
                WebDavServerId = "srv_1"
            }
        };

        Q.SetQueue(songs, startIndex: 1);
        var json = Q.ToQueueJson();

        Assert.Contains("\"current_index\":1", json);
        Assert.Contains("\"album_mid\":\"album_mid_1\"", json);
        Assert.Contains("\"local_file_path\":\"/music/song1.flac\"", json);
        Assert.Contains("\"webdav_href\":\"/webdav/song2.mp3\"", json);
        Assert.Contains("\"webdav_server_id\":\"srv_1\"", json);

        Q.SetQueue([]);
        Assert.Equal(0, Q.Count);

        Q.LoadQueueFromJson(json);
        Assert.Equal(2, Q.Count);
        Assert.Equal(1, Q.CurrentIndex);
        Assert.NotNull(Q.CurrentSong);
        Assert.Equal("mid_2", Q.CurrentSong.Mid);
        Assert.Equal("album_mid_2", Q.CurrentSong.AlbumMid);
        Assert.Equal("/webdav/song2.mp3", Q.CurrentSong.WebDavHref);
        Assert.Equal("srv_1", Q.CurrentSong.WebDavServerId);

        var first = Q.ActiveSongs[0];
        Assert.Equal("album_mid_1", first.AlbumMid);
        Assert.Equal("/music/song1.flac", first.LocalFilePath);
    }

    [Fact]
    public void PeekPrevSong_ListLoop_ReturnsExpectedWithoutAdvancingIndex()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 0);
        Q.Mode = PlaybackMode.ListLoop;

        var prevInLoop = Q.PeekPrevSong();
        Assert.NotNull(prevInLoop);
        Assert.Equal("mid_3", prevInLoop.Mid);
        Assert.Equal(0, Q.CurrentIndex);

        Q.SetCurrentIndex(1);
        var prevAtMiddle = Q.PeekPrevSong();
        Assert.NotNull(prevAtMiddle);
        Assert.Equal("mid_1", prevAtMiddle.Mid);
        Assert.Equal(1, Q.CurrentIndex);
    }

    [Fact]
    public void SetCurrentIndex_FiresQueueChanged()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 0);
        int eventCount = 0;
        Q.QueueChanged += OnChanged;

        try
        {
            Q.SetCurrentIndex(2);
            Assert.True(eventCount > 0);
            Assert.Equal(2, Q.CurrentIndex);
        }
        finally
        {
            Q.QueueChanged -= OnChanged;
        }

        void OnChanged() => eventCount++;
    }

    [Fact]
    public void SyncCurrentSong_ExistingAndNew_FiresQueueChanged()
    {
        Q.SetQueue(MakeSongs(3), startIndex: 0);
        int eventCount = 0;
        Q.QueueChanged += OnChanged;

        try
        {
            // 对齐已有歌曲
            var existingSong = new Song("mid_2", "Title 2", "Artist 2", "Album 2", 200, "", 2);
            Q.SyncCurrentSong(existingSong);
            Assert.Equal(1, Q.CurrentIndex);
            Assert.Equal(1, eventCount);

            // 对齐新歌曲（重置单曲队列）
            var newSong = new Song("mid_new", "Title New", "Artist New", "Album New", 180, "", 999);
            Q.SyncCurrentSong(newSong);
            Assert.Equal(0, Q.CurrentIndex);
            Assert.Equal(1, Q.Count);
            Assert.Equal("mid_new", Q.CurrentSong?.Mid);
            Assert.Equal(2, eventCount);
        }
        finally
        {
            Q.QueueChanged -= OnChanged;
        }

        void OnChanged() => eventCount++;
    }
}
