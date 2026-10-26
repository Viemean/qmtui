using System.Runtime.InteropServices;

namespace QmTui.Utils;

/// <summary>
/// 内存治理管理器：通过异步防抖机制在后台平稳期执行 glibc 内存修剪与轻量垃圾回收，
/// 释放 GStreamer 解码本地/WebDAV 无损音频流时的 native arena 内存，
/// 且不在主线程或高频切歌期产生任何停顿开销。
/// </summary>
public static partial class MemoryManager
{
    private static CancellationTokenSource? s_trimCts;
    private static readonly Lock s_lock = new();
    private static long s_lastTrimTick;
    private static long s_lastAllocatedBytes;
    private static long s_lastWorkingSetBytes;

    private const long ManagedGrowthThreshold = 8 * 1024 * 1024;
    private const long WorkingSetGrowthThreshold = 16 * 1024 * 1024;

    [LibraryImport("libc", EntryPoint = "malloc_trim")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int NativeMallocTrim(nuint pad);

    /// <summary>
    /// 触发一次防抖异步内存修剪（若防抖顺延超过 3 秒则触发保底修剪）。
    /// </summary>
    /// <param name="delayMs">异步防抖延迟时间（毫秒），默认 2500ms。</param>
    public static void ScheduleTrim(int delayMs = 2500)
    {
        lock (s_lock)
        {
            var now = Environment.TickCount64;
            if (s_lastTrimTick > 0 && now - s_lastTrimTick > 3000)
            {
                // 连续切歌防抖已超过 3 秒未实际执行，在后台异步修剪一次
                TrimBackground(force: false);
            }

            s_trimCts?.Cancel();
            s_trimCts?.Dispose();
            s_trimCts = new CancellationTokenSource();
            var ct = s_trimCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    TrimNow(force: false);
                }
                catch (OperationCanceledException)
                {
                    // 快速切歌重置正常
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("MemoryManager", $"ScheduleTrim background task error: {ex.Message}");
                }
            }, ct);
        }
    }

    /// <summary>
    /// 在后台线程执行一次非阻塞的内存收敛与页归还。
    /// </summary>
    /// <param name="force">是否绕过增量水位检查强制修剪，默认 false。</param>
    public static void TrimBackground(bool force = false)
    {
        _ = Task.Run(() => TrimNow(force));
    }

    /// <summary>
    /// 执行一次非阻塞的内存收敛与页归还。
    /// </summary>
    /// <param name="force">是否绕过增量水位检查强制修剪，默认 true。</param>
    public static void TrimNow(bool force = true)
    {
        try
        {
            long nowTick = Environment.TickCount64;
            long currentAllocated = GC.GetTotalAllocatedBytes();
            long currentWorkingSet = Environment.WorkingSet;

            if (!force && s_lastTrimTick > 0)
            {
                long managedDelta = currentAllocated - s_lastAllocatedBytes;
                long workingSetDelta = currentWorkingSet - s_lastWorkingSetBytes;

                if (managedDelta < ManagedGrowthThreshold && workingSetDelta < WorkingSetGrowthThreshold)
                {
                    return;
                }
            }

            s_lastTrimTick = nowTick;
            s_lastAllocatedBytes = currentAllocated;
            s_lastWorkingSetBytes = currentWorkingSet;

            // 触发包含 Gen 2 / LOH (大对象堆) 的强制非阻塞回收，及时释放解压图片与音频元数据字节块
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);

            // 在 Linux 平台下通知 glibc 遍历释放所有未使用的 arena 虚拟页还给内核
            if (OperatingSystem.IsLinux())
            {
                NativeMallocTrim(0);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MemoryManager", $"TrimNow error: {ex.Message}");
        }
    }
}
