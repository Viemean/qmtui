using System.Buffers.Binary;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 环形音频 PCM 缓冲区，用于无 GC 开销保存最近滑动音频切片
/// </summary>
public sealed class RollingAudioBuffer
{
    private readonly byte[] _buffer;
    private int _head;
    private int _count;
    private readonly Lock _lock = new();

    public RollingAudioBuffer(int capacityBytes)
    {
        _buffer = new byte[capacityBytes];
    }

    public int Capacity => _buffer.Length;

    public int AvailableBytes
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            int dataLen = data.Length;
            if (dataLen >= _buffer.Length)
            {
                data.Slice(dataLen - _buffer.Length).CopyTo(_buffer);
                _head = 0;
                _count = _buffer.Length;
                return;
            }

            int spaceToEnd = _buffer.Length - _head;
            if (dataLen <= spaceToEnd)
            {
                data.CopyTo(_buffer.AsSpan(_head, dataLen));
                _head = (_head + dataLen) % _buffer.Length;
            }
            else
            {
                data.Slice(0, spaceToEnd).CopyTo(_buffer.AsSpan(_head, spaceToEnd));
                data.Slice(spaceToEnd).CopyTo(_buffer.AsSpan(0, dataLen - spaceToEnd));
                _head = dataLen - spaceToEnd;
            }

            _count = Math.Min(_buffer.Length, _count + dataLen);
        }
    }

    public byte[] GetRecentBytes(int maxBytes)
    {
        lock (_lock)
        {
            int takeBytes = Math.Min(maxBytes, _count);
            if (takeBytes <= 0) return Array.Empty<byte>();

            byte[] result = new byte[takeBytes];
            int start = (_head - takeBytes + _buffer.Length) % _buffer.Length;
            int part1 = Math.Min(takeBytes, _buffer.Length - start);
            Buffer.BlockCopy(_buffer, start, result, 0, part1);
            if (part1 < takeBytes)
            {
                Buffer.BlockCopy(_buffer, 0, result, part1, takeBytes - part1);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }
}

/// <summary>
/// 系统内录后台智能预录管理器
/// 在内存中维持最近 3.5 秒的系统内录音频切片，实现按快捷键瞬间 0 延迟发射首发识别请求。
/// 具备静音自动休眠，避免常驻阻止声卡睡眠或系统状态栏录音提示。
/// </summary>
public static class AudioPreRollManager
{
    private static readonly Lock s_lock = new();
    private static readonly RollingAudioBuffer s_buffer = new(16000 * 2 * 4); // 4 秒缓冲 (128KB)
    private static Thread? s_workerThread;
    private static CancellationTokenSource? s_cts;
    private static volatile bool s_isEnabled = UserConfig.Current.EnableAudioRecognitionPreRoll;
    private static volatile bool s_hasSignal;
    private static volatile bool s_isSleeping;
    private static long s_consecutiveSilenceChunks;

    public static bool IsEnabled
    {
        get => s_isEnabled;
        set
        {
            s_isEnabled = value;
            if (!value)
            {
                s_buffer.Clear();
                s_hasSignal = false;
            }
            else
            {
                EnsureStarted();
            }
        }
    }

    /// <summary>
    /// 获取当前预录缓冲区中是否有足够时长（>= 2.5s）且具有有效能量的切片
    /// </summary>
    public static bool HasReadyPreRoll(double minSeconds = 2.5)
    {
        int requiredBytes = (int)(16000 * 2 * minSeconds);
        return s_isEnabled && s_buffer.AvailableBytes >= requiredBytes && s_hasSignal;
    }

    /// <summary>
    /// 提取最近的历史预录字节（按需取出用于注入新录音会话）
    /// </summary>
    public static byte[] TakePreRollBytes(int maxBytes = 16000 * 2 * 3)
    {
        if (!s_isEnabled) return Array.Empty<byte>();
        return s_buffer.GetRecentBytes(maxBytes);
    }

    /// <summary>
    /// 确保预录线程已启动
    /// </summary>
    public static void EnsureStarted()
    {
        if (!s_isEnabled) return;
        lock (s_lock)
        {
            if (s_workerThread != null && s_workerThread.IsAlive) return;

            s_cts?.Dispose();
            s_cts = new CancellationTokenSource();
            s_workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "AudioPreRollThread",
                Priority = ThreadPriority.Lowest
            };
            s_workerThread.Start();
        }
    }

    private static unsafe void WorkerLoop()
    {
        byte[] buffer = new byte[2048]; // ~64ms of 16000Hz 16-bit mono
        var ss = new pa_sample_spec
        {
            format = 3, // PA_SAMPLE_S16LE
            rate = 16000,
            channels = 1
        };
        var attr = new pa_buffer_attr
        {
            maxlength = uint.MaxValue,
            tlength = uint.MaxValue,
            prebuf = uint.MaxValue,
            minreq = uint.MaxValue,
            fragsize = 2048
        };

        var token = s_cts?.Token ?? CancellationToken.None;
        IntPtr localHandle = IntPtr.Zero;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!s_isEnabled)
                {
                    if (localHandle != IntPtr.Zero)
                    {
                        try { PulseAudioSimpleNative.pa_simple_free(localHandle); } catch { }
                        localHandle = IntPtr.Zero;
                    }
                    Thread.Sleep(300);
                    continue;
                }

                if (s_isSleeping)
                {
                    if (localHandle != IntPtr.Zero)
                    {
                        try { PulseAudioSimpleNative.pa_simple_free(localHandle); } catch { }
                        localHandle = IntPtr.Zero;
                    }
                    Thread.Sleep(500);
                    if (AudioDeviceHelper.HasInternalRecordDevice())
                    {
                        s_isSleeping = false;
                        s_consecutiveSilenceChunks = 0;
                    }
                    continue;
                }

                if (localHandle == IntPtr.Zero)
                {
                    var inputDevice = AudioDeviceHelper.GetDefaultSinkMonitorDevice();
                    localHandle = PulseAudioSimpleNative.pa_simple_new(
                        null,
                        "qmtui-preroll",
                        pa_stream_direction_t.PA_STREAM_RECORD,
                        inputDevice,
                        "Music PreRoll",
                        in ss,
                        IntPtr.Zero,
                        in attr,
                        out int error
                    );

                    if (localHandle == IntPtr.Zero)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                fixed (byte* pBuf = buffer)
                {
                    int res = PulseAudioSimpleNative.pa_simple_read(localHandle, pBuf, (nuint)buffer.Length, out int error);
                    if (res < 0)
                    {
                        try { PulseAudioSimpleNative.pa_simple_free(localHandle); } catch { }
                        localHandle = IntPtr.Zero;
                        Thread.Sleep(500);
                        continue;
                    }
                }

                // 计算该分片能量（RMS）
                long sumSquares = 0;
                int sampleCount = buffer.Length / 2;
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
                    sumSquares += (long)sample * sample;
                }
                double rms = sampleCount > 0 ? Math.Sqrt((double)sumSquares / sampleCount) : 0;

                if (rms > 80)
                {
                    s_hasSignal = true;
                    s_consecutiveSilenceChunks = 0;
                }
                else
                {
                    s_consecutiveSilenceChunks++;
                    // 连续 30 秒（约 460 个分片）持续静音，触发深度休眠释放句柄，避免占用声卡或亮指示器
                    if (s_consecutiveSilenceChunks > 460)
                    {
                        if (localHandle != IntPtr.Zero)
                        {
                            try { PulseAudioSimpleNative.pa_simple_free(localHandle); } catch { }
                            localHandle = IntPtr.Zero;
                        }
                        s_isSleeping = true;
                        s_hasSignal = false;
                        s_buffer.Clear();
                        continue;
                    }
                }

                s_buffer.Write(buffer);
            }
        }
        finally
        {
            if (localHandle != IntPtr.Zero)
            {
                try { PulseAudioSimpleNative.pa_simple_free(localHandle); } catch { }
            }
        }
    }
}
