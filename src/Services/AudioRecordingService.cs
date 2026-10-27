using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// 录音输入源类型
/// </summary>
public enum AudioRecordSource
{
    /// <summary>
    /// 系统音频内录
    /// </summary>
    SystemInternal,

    /// <summary>
    /// 麦克风录音
    /// </summary>
    Microphone
}

[StructLayout(LayoutKind.Sequential)]
public struct pa_sample_spec
{
    public int format;   // PA_SAMPLE_S16LE = 3
    public uint rate;    // 16000
    public byte channels;// 1
}

[StructLayout(LayoutKind.Sequential)]
public struct pa_buffer_attr
{
    public uint maxlength;
    public uint tlength;
    public uint prebuf;
    public uint minreq;
    public uint fragsize;
}

public enum pa_stream_direction_t
{
    PA_STREAM_NODIRECTION = 0,
    PA_STREAM_PLAYBACK = 1,
    PA_STREAM_RECORD = 2,
    PA_STREAM_UPLOAD = 3
}

internal static partial class PulseAudioSimpleNative
{
    private const string LibPulseSimple = "libpulse-simple.so.0";
    private const string LibPulse = "libpulse.so.0";

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr pa_simple_new(
        string? server,
        string name,
        pa_stream_direction_t dir,
        string? dev,
        string stream_name,
        in pa_sample_spec ss,
        IntPtr map,
        in pa_buffer_attr attr,
        out int error);

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_read")]
    public static unsafe partial int pa_simple_read(IntPtr s, void* data, nuint bytes, out int error);

    [LibraryImport(LibPulseSimple, EntryPoint = "pa_simple_free")]
    public static partial void pa_simple_free(IntPtr s);

    [LibraryImport(LibPulse, EntryPoint = "pa_strerror")]
    public static partial IntPtr pa_strerror(int error);

    public static string GetErrorMessage(int error)
    {
        try
        {
            var ptr = pa_strerror(error);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringUTF8(ptr) ?? $"Code {error}" : $"Code {error}";
        }
        catch
        {
            return $"Code {error}";
        }
    }
}

internal static partial class AudioToolboxNative
{
    private const string LibAudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    public const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'
    public const uint kLinearPCMFormatFlagIsSignedInteger = 4;
    public const uint kLinearPCMFormatFlagIsPacked = 8;

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioQueueBuffer
    {
        public uint mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint mAudioDataByteSize;
        public IntPtr mUserData;
        public uint mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public uint mPacketDescriptionCount;
    }

    [LibraryImport(LibAudioToolbox)]
    public static unsafe partial int AudioQueueNewInput(
        in AudioStreamBasicDescription inFormat,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint, IntPtr, void> inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAQ);

    [LibraryImport(LibAudioToolbox)]
    public static partial int AudioQueueAllocateBuffer(
        IntPtr inAQ,
        uint inBufferByteSize,
        out IntPtr outBuffer);

    [LibraryImport(LibAudioToolbox)]
    public static partial int AudioQueueEnqueueBuffer(
        IntPtr inAQ,
        IntPtr inBuffer,
        uint inNumPacketDescs,
        IntPtr inPacketDescs);

    [LibraryImport(LibAudioToolbox)]
    public static partial int AudioQueueStart(
        IntPtr inAQ,
        IntPtr inStartTime);

    [LibraryImport(LibAudioToolbox)]
    public static partial int AudioQueueStop(
        IntPtr inAQ,
        [MarshalAs(UnmanagedType.I1)] bool inImmediate);

    [LibraryImport(LibAudioToolbox)]
    public static partial int AudioQueueDispose(
        IntPtr inAQ,
        [MarshalAs(UnmanagedType.I1)] bool inImmediate);
}

/// <summary>
/// 实时流式录音会话，基于 libpulse-simple (Linux)、AudioToolbox (macOS) 与 qmtui-recorder.exe (Windows) 原生直连
/// </summary>
public sealed class AudioRecordingSession : IDisposable
{
    private static readonly Lock s_windowsLock = new();
    private static Process? s_windowsProcess;
    private static EventWaitHandle? s_evtLoopback;
    private static EventWaitHandle? s_evtMic;
    private static EventWaitHandle? s_evtStop;
    private static AudioRecordingSession? s_activeSession;
    private static AudioRecordingSession? s_activeMacSession;

    static AudioRecordingSession()
    {
        if (OperatingSystem.IsWindows())
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (s_windowsProcess is { HasExited: false })
                    {
                        s_windowsProcess.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    AppLogger.Debug("AudioRecording", $"ProcessExit kill recorder process: {ex.Message}");
                }
            };
        }
    }

    private readonly AudioRecordSource _source;
    private IntPtr _pulseHandle;
    private IntPtr _macAudioQueue;
    private readonly List<IntPtr> _macBuffers = [];
    private readonly MemoryStream _pcmStream = new(64 * 1024);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread? _recordThread;
    private bool _isDisposed;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                if (_isDisposed) return false;
                if (OperatingSystem.IsWindows())
                {
                    lock (s_windowsLock)
                    {
                        return s_windowsProcess != null && !s_windowsProcess.HasExited;
                    }
                }
                if (OperatingSystem.IsMacOS())
                {
                    return _macAudioQueue != IntPtr.Zero;
                }
                if (OperatingSystem.IsLinux())
                {
                    return _pulseHandle != IntPtr.Zero;
                }
                return false;
            }
        }
    }

    public AudioRecordingSession(AudioRecordSource source, byte[]? initialPreRollBytes = null)
    {
        _source = source;
        if (initialPreRollBytes != null && initialPreRollBytes.Length > 0)
        {
            _pcmStream.Write(initialPreRollBytes, 0, initialPreRollBytes.Length);
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsProcessStarted();
            s_activeSession = this;
            TriggerWindowsEvent(source == AudioRecordSource.Microphone ? s_evtMic : s_evtLoopback);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (source == AudioRecordSource.Microphone)
            {
                StartMacMicrophone();
            }
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var inputDevice = source == AudioRecordSource.SystemInternal
            ? AudioDeviceHelper.GetDefaultSinkMonitorDevice()
            : AudioDeviceHelper.GetDefaultMicrophoneDevice();

        var ss = new pa_sample_spec
        {
            format = 3, // PA_SAMPLE_S16LE
            rate = 16000,
            channels = 1
        };

        // 显式指定分片大小（fragsize = 2048 字节，约 64ms），避免 PulseAudio 服务端默认使用数秒的巨大缓冲导致读阻塞
        var attr = new pa_buffer_attr
        {
            maxlength = uint.MaxValue,
            tlength = uint.MaxValue,
            prebuf = uint.MaxValue,
            minreq = uint.MaxValue,
            fragsize = 2048
        };

        try
        {
            int error;
            _pulseHandle = PulseAudioSimpleNative.pa_simple_new(
                null,
                "qmtui",
                pa_stream_direction_t.PA_STREAM_RECORD,
                inputDevice,
                "Music Recognition",
                in ss,
                IntPtr.Zero,
                in attr,
                out error
            );

            // 若指定特定设备失败且为麦克风模式，尝试以系统默认设备重试
            if (_pulseHandle == IntPtr.Zero && source == AudioRecordSource.Microphone && !string.IsNullOrEmpty(inputDevice))
            {
                _pulseHandle = PulseAudioSimpleNative.pa_simple_new(
                    null,
                    "qmtui",
                    pa_stream_direction_t.PA_STREAM_RECORD,
                    null,
                    "Music Recognition",
                    in ss,
                    IntPtr.Zero,
                    in attr,
                    out error
                );
            }

            if (_pulseHandle == IntPtr.Zero)
            {
                var errStr = PulseAudioSimpleNative.GetErrorMessage(error);
                AppLogger.Warn("AudioRecording", $"Failed to initialize pa_simple_new. Device: {inputDevice}, Error: {error} ({errStr})");
                return;
            }

            AppLogger.Force("AudioRecording", $"Started session via libpulse-simple. Source: {source}, Device: {inputDevice}");

            _recordThread = new Thread(RecordLoop)
            {
                IsBackground = true,
                Name = "PulseRecordThread"
            };
            _recordThread.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioRecording", $"Exception in AudioRecordingSession init: {ex.Message}");
            _pulseHandle = IntPtr.Zero;
        }
    }

    private unsafe void RecordLoop()
    {
        byte[] buffer = new byte[2048]; // ~64ms of 16000Hz 16-bit mono
        fixed (byte* pBuf = buffer)
        {
            while (!_cts.IsCancellationRequested)
            {
                IntPtr handle;
                lock (_lock)
                {
                    handle = _pulseHandle;
                }
                if (handle == IntPtr.Zero) break;

                int res = PulseAudioSimpleNative.pa_simple_read(handle, pBuf, (nuint)buffer.Length, out int error);
                if (res < 0)
                {
                    if (!_cts.IsCancellationRequested)
                    {
                        var errStr = PulseAudioSimpleNative.GetErrorMessage(error);
                        AppLogger.Warn("AudioRecording", $"pa_simple_read error: {error} ({errStr})");
                    }
                    break;
                }

                if (_cts.IsCancellationRequested) break;

                lock (_lock)
                {
                    _pcmStream.Write(buffer, 0, buffer.Length);
                }
            }
        }

        // 录音线程退出读取循环后，释放 pulseHandle
        lock (_lock)
        {
            if (_pulseHandle != IntPtr.Zero)
            {
                try
                {
                    PulseAudioSimpleNative.pa_simple_free(_pulseHandle);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("AudioRecording", $"pa_simple_free exception: {ex.Message}");
                }
                _pulseHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 检查最近 0.5 秒内是否存在有效声音信号（RMS > 80）
    /// 用于在静音段跳过无效识别请求
    /// </summary>
    /// <returns>若录制音频能量超过有效声音阈值返回 <see langword="true"/>；全静音返回 <see langword="false"/>。</returns>
    public bool HasMeaningfulSignal()
    {
        lock (_lock)
        {
            int totalBytes = (int)_pcmStream.Length;
            int totalSamples = totalBytes / 2;
            // 取最后 0.5 秒（8000 个样本）
            const int windowSamples = 8000;
            int startSample = Math.Max(0, totalSamples - windowSamples);
            int count = totalSamples - startSample;
            if (count <= 0) return false;

            var rawBytes = _pcmStream.GetBuffer();
            long sumSquares = 0;
            for (int i = startSample; i < totalSamples; i++)
            {
                short val = BinaryPrimitives.ReadInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
                sumSquares += (long)val * val;
            }
            double rms = Math.Sqrt((double)sumSquares / count);
            return rms > 80;
        }
    }

    /// <summary>
    /// 获取当前缓冲区中已累积的 16000Hz 单声道 16-bit PCM 采样数组。
    /// 麦克风模式下额外执行 DC 去除与预加重滤波（α=0.87），补偿空气传播导致的高频衰减，
    /// 提升目标频带的 SNR，使早期切片具备有效指纹识别能力。
    /// AGC 仅基于最后 1.5 秒样本计算 RMS，避免录音初始静音段拉偏增益系数。
    /// </summary>
    /// <returns>最新环形缓冲区内的音频 PCM 采样切片数组。</returns>
    public short[] GetSnapshotSamples()
    {
        lock (_lock)
        {
            int totalBytes = (int)_pcmStream.Length;
            int sampleCount = totalBytes / 2;
            if (sampleCount == 0) return [];

            var rawBytes = _pcmStream.GetBuffer();
            var samples = new short[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
            }

            // 麦克风模式：DC 去除 + 预加重滤波，补偿空气传播高频衰减
            // 预加重后信噪比有效提升，指纹特征可以更早浮现。
            // α=0.87：提升约 3~4 dB/倍频程，在强化中高频的同时
            // 保留足够的低频内容，对不同型号麦克风频响兼容性更好
            if (_source == AudioRecordSource.Microphone && sampleCount > 1)
            {
                // DC 去除：减去全段均值，消除直流偏置对 FFT 功率谱的干扰
                long dcSum = 0;
                for (int i = 0; i < sampleCount; i++) dcSum += samples[i];
                int dcOffset = (int)(dcSum / sampleCount);

                // 预加重：y[n] = x[n] - α·x[n-1]，α=0.87（温和一阶高通）
                const float alpha = 0.87f;
                int prev = samples[0] - dcOffset;
                samples[0] = (short)Math.Clamp(prev, short.MinValue, short.MaxValue);
                for (int i = 1; i < sampleCount; i++)
                {
                    int cur = (samples[i] - dcOffset) - (int)(alpha * prev);
                    samples[i] = (short)Math.Clamp(cur, short.MinValue, short.MaxValue);
                    prev = samples[i] - dcOffset;
                }
            }

            // 仅取最后 1.5 秒（24000 个样本）计算 RMS，排除录音前段静音/噪声干扰
            const int agcWindowSamples = 24000; // 16000 Hz * 1.5s
            int agcStart = Math.Max(0, sampleCount - agcWindowSamples);
            int agcCount = sampleCount - agcStart;

            long sumSquares = 0;
            for (int i = agcStart; i < sampleCount; i++)
            {
                sumSquares += (long)samples[i] * samples[i];
            }
            double rms = agcCount > 0 ? Math.Sqrt((double)sumSquares / agcCount) : 0;

            // 麦克风或低音量输入：RMS 在有效范围内才执行增益，目标 ~3200，上限 8 倍
            if ((_source == AudioRecordSource.Microphone || rms < 2000) && rms > 80 && rms < 3000)
            {
                float gain = Math.Min(8.0f, (float)(3200.0 / rms));
                if (gain > 1.2f)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
                    }
                }
            }

            return samples;
        }
    }

    private static void EnsureWindowsProcessStarted()
    {
        lock (s_windowsLock)
        {
            if (s_windowsProcess != null && !s_windowsProcess.HasExited) return;

            int pid = Environment.ProcessId;
            s_evtLoopback = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\QmTui_Record_Loopback_{pid}");
            s_evtMic = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\QmTui_Record_Mic_{pid}");
            s_evtStop = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\QmTui_Record_Stop_{pid}");

            string exeName = "qmtui-recorder.exe";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(baseDir, exeName);
            if (!File.Exists(exePath)) exePath = exeName;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--idle --parent-pid {pid}",
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            s_windowsProcess = Process.Start(psi);
            if (s_windowsProcess != null && !s_windowsProcess.HasExited)
            {
                new Thread(WindowsReaderLoop) { IsBackground = true, Name = "WindowsAudioReaderThread" }.Start();
                AppLogger.Force("AudioRecording", $"Started persistent WASAPI recorder daemon PID: {s_windowsProcess.Id}");
            }
        }
    }

    private static void TriggerWindowsEvent(EventWaitHandle? evtHandle)
    {
        try
        {
            evtHandle?.Set();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioRecording", $"Failed to trigger recorder event: {ex.Message}");
        }
    }

    private static void WindowsReaderLoop()
    {
        var proc = s_windowsProcess;
        if (proc == null) return;
        byte[] buffer = new byte[2048];
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            while (!proc.HasExited)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                s_activeSession?.OnPcm(buffer, read);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioRecording", $"WindowsReaderLoop ended: {ex.Message}");
        }
    }

    private void OnPcm(byte[] buffer, int count)
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _pcmStream.Write(buffer, 0, count);
            }
        }
    }

    private void StartMacMicrophone()
    {
        try
        {
            var format = new AudioToolboxNative.AudioStreamBasicDescription
            {
                mSampleRate = 16000.0,
                mFormatID = AudioToolboxNative.kAudioFormatLinearPCM,
                mFormatFlags = AudioToolboxNative.kLinearPCMFormatFlagIsSignedInteger | AudioToolboxNative.kLinearPCMFormatFlagIsPacked,
                mBytesPerPacket = 2,
                mFramesPerPacket = 1,
                mBytesPerFrame = 2,
                mChannelsPerFrame = 1,
                mBitsPerChannel = 16,
                mReserved = 0
            };

            s_activeMacSession = this;

            int status;
            unsafe
            {
                status = AudioToolboxNative.AudioQueueNewInput(
                    in format,
                    &MacAudioInputCallback,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    out _macAudioQueue
                );
            }

            if (status != 0 || _macAudioQueue == IntPtr.Zero)
            {
                AppLogger.Warn("AudioRecording", $"AudioQueueNewInput failed with status {status}");
                return;
            }

            const uint bufferSize = 2048;
            for (int i = 0; i < 3; i++)
            {
                if (AudioToolboxNative.AudioQueueAllocateBuffer(_macAudioQueue, bufferSize, out IntPtr buf) == 0 && buf != IntPtr.Zero)
                {
                    _macBuffers.Add(buf);
                    AudioToolboxNative.AudioQueueEnqueueBuffer(_macAudioQueue, buf, 0, IntPtr.Zero);
                }
            }

            AudioToolboxNative.AudioQueueStart(_macAudioQueue, IntPtr.Zero);
            AppLogger.Info("AudioRecording", "Started macOS AudioQueue microphone session (16000Hz mono S16LE).");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AudioRecording", $"Failed to start macOS microphone: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MacAudioInputCallback(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packetCount, IntPtr packetDescs)
    {
        var session = s_activeMacSession;
        if (session == null || session._isDisposed || queue == IntPtr.Zero || buffer == IntPtr.Zero) return;

        try
        {
            var buf = Marshal.PtrToStructure<AudioToolboxNative.AudioQueueBuffer>(buffer);
            if (buf.mAudioDataByteSize > 0 && buf.mAudioData != IntPtr.Zero)
            {
                byte[] data = new byte[buf.mAudioDataByteSize];
                Marshal.Copy(buf.mAudioData, data, 0, (int)buf.mAudioDataByteSize);
                session.OnPcm(data, data.Length);
            }

            if (!session._isDisposed)
            {
                AudioToolboxNative.AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AudioRecording", $"MacAudioInputCallback exception: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (OperatingSystem.IsWindows())
        {
            if (s_activeSession == this) s_activeSession = null;
            if (_source == AudioRecordSource.Microphone)
            {
                TriggerWindowsEvent(s_evtStop);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            if (s_activeMacSession == this) s_activeMacSession = null;
            lock (_lock)
            {
                if (_macAudioQueue != IntPtr.Zero)
                {
                    try
                    {
                        AudioToolboxNative.AudioQueueStop(_macAudioQueue, true);
                        AudioToolboxNative.AudioQueueDispose(_macAudioQueue, true);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("AudioRecording", $"Dispose AudioQueue error: {ex.Message}");
                    }
                    _macAudioQueue = IntPtr.Zero;
                    _macBuffers.Clear();
                }
            }
        }

        _cts.Cancel();
        if (_recordThread?.IsAlive == true)
        {
            _recordThread.Join(500);
        }

        // 若录音线程未启动或已停止，在此做兜底释放
        lock (_lock)
        {
            if (_pulseHandle != IntPtr.Zero && (_recordThread == null || !_recordThread.IsAlive))
            {
                PulseAudioSimpleNative.pa_simple_free(_pulseHandle);
                _pulseHandle = IntPtr.Zero;
            }
        }

        AppLogger.Force("AudioRecording", $"Stopped session. Captured {_pcmStream.Length} PCM bytes.");
        _pcmStream.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 音频录制服务（PulseAudio / PipeWire 原生直连）
/// </summary>
public static class AudioRecordingService
{
    /// <summary>
    /// 启动流式录音会话
    /// </summary>
    /// <param name="source">录音输入源类型（麦克风或系统监听回放）。</param>
    /// <param name="initialPreRollBytes">录音启动前预录缓冲区保留的 PCM 历史字节数组；可为 null。</param>
    /// <returns>音频录制会话管理控制实例。</returns>
    public static AudioRecordingSession StartRecordingSession(AudioRecordSource source, byte[]? initialPreRollBytes = null)
    {
        return new AudioRecordingSession(source, initialPreRollBytes);
    }
}
