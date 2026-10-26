using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using QmTui.Models;
using QmTui.UI;
using QmTui.Utils;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;
using Xunit.Abstractions;

namespace QmTui.Tests;

public class PerformanceAndMemoryDiagnosticsTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceAndMemoryDiagnosticsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark_LyricParser_ThroughputAndAllocation()
    {
        // 构造包含 5,000 行的超大歌词文本（分钟控制在 00-99 内）
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            int min = (i / 60) % 99;
            int sec = i % 60;
            sb.AppendLine($"[{min:D2}:{sec:D2}.{i % 100:D2}] Lyric line number {i} with some chinese content 离花别话 &apos; test");
        }
        var rawLrc = sb.ToString();

        // 预热 JIT
        _ = LyricParser.ParseLrc("[00:01.00]Warmup");

        long memBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var result = LyricParser.ParseLrc(rawLrc);

        sw.Stop();
        long memAfter = GC.GetTotalAllocatedBytes(precise: true);
        long allocatedBytes = memAfter - memBefore;

        _output.WriteLine($"[LyricParser Benchmark]");
        _output.WriteLine($"  - Total lines parsed: {result.Count}");
        _output.WriteLine($"  - Elapsed time: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMicroseconds:F0} us)");
        _output.WriteLine($"  - Allocated memory: {allocatedBytes / 1024.0:F2} KB");
        _output.WriteLine($"  - Avg throughput: {result.Count / sw.Elapsed.TotalSeconds:F0} lines/sec ({sw.Elapsed.TotalMicroseconds / (double)result.Count:F3} us/line)");

        Assert.Equal(5000, result.Count);
        // 5,000 行歌词解析应当在 150ms 之内完成（高吞吐基准）
        Assert.True(sw.ElapsedMilliseconds < 150, $"LyricParser too slow: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Benchmark_CommentView_FormattingAndMemoryConvergence()
    {
        Application.Init();
        try
        {
            var top = new View { Width = 100, Height = 40, Visible = true, CanFocus = true };
            var commentView = new SongCommentView
            {
                Width = 80,
                Height = 35,
                Visible = true
            };
            top.Add(commentView);

            var song = new Song("mid_diag", "Diagnostic Song", "Diagnostic Artist", "Diagnostic Album", 200, "media");
            commentView.SetSong(song);

            // 预热与基线内存采样
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long baselineAllocated = GC.GetTotalMemory(true);

            var sw = Stopwatch.StartNew();

            // 连续模拟 50 次歌曲切换与评论重构
            for (int cycle = 0; cycle < 50; cycle++)
            {
                var currentSong = new Song($"mid_{cycle}", $"Song {cycle}", "Artist", "Album", 180, "media");
                commentView.SetSong(currentSong);
                commentView.OnActivated();
                commentView.ScrollToTop();
                commentView.MoveNext();
                commentView.ScrollToEnd();
                commentView.OnDeactivated();
            }

            sw.Stop();

            long peakAllocated = GC.GetTotalMemory(false);

            // 触发内存管理器主动修剪并等待后台收敛
            MemoryManager.TrimNow();
            System.Threading.Thread.Sleep(50);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            long finalAllocated = GC.GetTotalMemory(true);
            long leakGrowth = finalAllocated - baselineAllocated;

            _output.WriteLine($"[CommentView 50-Cycle Stress Benchmark]");
            _output.WriteLine($"  - 50 cycles total elapsed: {sw.ElapsedMilliseconds} ms (avg {sw.ElapsedMilliseconds / 50.0:F2} ms/cycle)");
            _output.WriteLine($"  - Baseline Heap: {baselineAllocated / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"  - Peak Heap:     {peakAllocated / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"  - Final Heap:    {finalAllocated / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"  - Residual Net Growth: {leakGrowth / 1024.0:F2} KB");

            // 50 次切换后残留增长应当小于 5MB（无累积句柄或强引用泄漏）
            Assert.True(leakGrowth < 5 * 1024 * 1024, $"Potential memory leak detected: net growth {leakGrowth / 1024.0} KB");
        }
        finally
        {
            Application.Shutdown();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AllocateBigLohBuffers()
    {
        var bigBuffers = new byte[20][];
        for (int i = 0; i < bigBuffers.Length; i++)
        {
            bigBuffers[i] = new byte[2 * 1024 * 1024]; // 2MB 每个，共 40MB
            bigBuffers[i][0] = 0x55;
            bigBuffers[i][^1] = 0xAA;
        }
    }

    [Fact]
    public void Benchmark_MemoryManager_TrimConvergence()
    {
        // 模拟突发性大对象堆分配（模拟图片解码或音轨缓冲）
        AllocateBigLohBuffers();

        long allocatedBefore = GC.GetTotalMemory(false);
        _output.WriteLine($"[MemoryManager Trim Benchmark]");
        _output.WriteLine($"  - Allocated before GC: {allocatedBefore / 1024.0 / 1024.0:F2} MB");

        // 触发同步 Full GC 验证回收
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        MemoryManager.TrimNow();

        long allocatedAfter = GC.GetTotalMemory(true);
        _output.WriteLine($"  - Allocated after trim:  {allocatedAfter / 1024.0 / 1024.0:F2} MB");
        _output.WriteLine($"  - Memory reclaimed:      {(allocatedBefore - allocatedAfter) / 1024.0 / 1024.0:F2} MB");

        Assert.True(allocatedBefore - allocatedAfter > 20 * 1024 * 1024, "Memory should be reclaimed after TrimNow");
    }

    [Fact]
    public void Benchmark_SongListView_BulkSetSongs()
    {
        // 回归用例：向 SongListView 一次性灌入大歌单时，逐条 Add 曾让 ListWrapper
        // 每次重扫全表计算 MaxItemLength，3000 首约耗时 27s（实测）。
        const int songCount = 3000;
        var songs = new List<Song>(songCount);
        for (int i = 0; i < songCount; i++)
        {
            songs.Add(new Song($"mid_{i}", $"Song title {i} with some extra text", $"Artist {i}", $"Album {i}", 240));
        }

        Application.Init();
        try
        {
            var top = new View { Width = 140, Height = 40, Visible = true, CanFocus = true };
            var listView = new SongListView { Width = 100, Height = 35, Visible = true };
            top.Add(listView);

            var sw = Stopwatch.StartNew();
            listView.SetSongs(songs, "bulk load");
            sw.Stop();

            _output.WriteLine($"[SongListView Bulk SetSongs Benchmark]");
            _output.WriteLine($"  - Songs: {songCount}");
            _output.WriteLine($"  - Elapsed: {sw.ElapsedMilliseconds} ms");
            _output.WriteLine($"  - Rows built: {listView.Songs.Count}");

            var inner = listView.SubViews.OfType<ListView>().First();

            Assert.Equal(songCount, listView.Songs.Count);
            Assert.Equal(0, listView.SelectedItem ?? -1);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"SongListView bulk fill too slow: {sw.ElapsedMilliseconds} ms");
            // Change notifications must be back on, otherwise later row highlights never repaint.
            Assert.False(inner.Source!.SuspendCollectionChangedEvent);
            // Content height feeds scroll clamping; a suspended fill must not leave it at 0.
            Assert.Equal(songCount, inner.GetContentHeight());
        }
        finally
        {
            Application.Shutdown();
        }
    }

    [Fact]
    public void Benchmark_SongListView_BulkSetCustomItems()
    {
        // 同一 O(n^2) 也出现在自定义列表入口（我的歌单 / 收藏专辑 / WebDAV 目录），
        // 3000 项在修复前约 18s（实测）。
        const int itemCount = 3000;
        var items = new List<string>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            items.Add($"{i + 1:D2}  [收藏]  Playlist title {i} with extra text  (共 {i} 首)");
        }

        Application.Init();
        try
        {
            var top = new View { Width = 140, Height = 40, Visible = true, CanFocus = true };
            var listView = new SongListView { Width = 100, Height = 35, Visible = true };
            top.Add(listView);

            var sw = Stopwatch.StartNew();
            listView.SetCustomItems(items, "bulk custom", _ => Task.CompletedTask);
            sw.Stop();

            _output.WriteLine($"[SongListView Bulk SetCustomItems Benchmark]");
            _output.WriteLine($"  - Items: {itemCount}");
            _output.WriteLine($"  - Elapsed: {sw.ElapsedMilliseconds} ms");

            var inner = listView.SubViews.OfType<ListView>().First();

            Assert.Equal(itemCount, inner.Source!.Count);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"SongListView bulk custom fill too slow: {sw.ElapsedMilliseconds} ms");
            Assert.False(inner.Source.SuspendCollectionChangedEvent);
            Assert.Equal(itemCount, inner.GetContentHeight());
        }
        finally
        {
            Application.Shutdown();
        }
    }

    [Fact]
    public void ThinScrollBarView_Drag_TracksThumbSmoothly()
    {
        var scrollBar = new ThinScrollBarView();
        scrollBar.UpdateMetrics(totalItems: 3000, visibleItems: 30, firstVisibleItem: 0);

        int lastTarget = -1;
        scrollBar.ScrollPositionChanged += target => lastTarget = target;

        // 模拟在滑块内按下左键并向下拖动
        scrollBar.HandleMouseEvent(MouseFlags.LeftButtonPressed, 0);
        scrollBar.HandleMouseEvent(MouseFlags.LeftButtonPressed, 10);

        Assert.True(lastTarget > 500, $"Scroll target should track drag directly, but was {lastTarget}");

        scrollBar.HandleMouseEvent(MouseFlags.LeftButtonPressed, 19);
        Assert.True(lastTarget >= 2900, $"Scroll target should reach bottom, but was {lastTarget}");

        scrollBar.HandleMouseEvent(MouseFlags.LeftButtonReleased, 19);
    }
}
