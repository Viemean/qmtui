using System.Runtime.InteropServices;

namespace QmTui.Utils;

/// <summary>
/// 内存治理管理器：通过异步防抖机制在后台平稳期执行 glibc 内存修剪与轻量垃圾回收，
/// 彻底解决 GStreamer 解码本地/WebDAV 无损大音频流时的 native arena 内存滞留问题，
/// 且不在主线程或高频切歌期产生任何停顿开销。
/// </summary>
public static partial class MemoryManager
{
    private static CancellationTokenSource? s_trimCts;
    private static readonly Lock s_lock = new();
    private static long s_lastTrimTick;

    [LibraryImport("libc", EntryPoint = "malloc_trim")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int NativeMallocTrim(nuint pad);

    /// <summary>
    /// 触发一次防抖异步内存修剪。连续快速切歌时将自动顺延，
    /// 若防抖顺延超过 3 秒则触发保底修剪，避免饿死。
    /// </summary>
    public static void ScheduleTrim(int delayMs = 2500)
    {
        lock (s_lock)
        {
            var now = Environment.TickCount64;
            if (s_lastTrimTick > 0 && now - s_lastTrimTick > 3000)
            {
                // 连续切歌防抖已超过 3 秒未实际执行，立即在后台异步修剪一次
                TrimBackground();
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

                    TrimNow();
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
    /// 在后台线程立即执行一次非阻塞的内存收敛与页归还
    /// </summary>
    public static void TrimBackground()
    {
        _ = Task.Run(TrimNow);
    }

    /// <summary>
    /// 立即执行一次非阻塞的内存收敛与页归还
    /// </summary>
    public static void TrimNow()
    {
        try
        {
            s_lastTrimTick = Environment.TickCount64;

            // 1. 触发包含 Gen 2 / LOH (大对象堆) 的强制非阻塞回收，及时释放解压图片与音频元数据字节块
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);

            // 2. 在 Linux 平台下通知 glibc 遍历释放所有未使用的 arena 虚拟页还给内核
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
