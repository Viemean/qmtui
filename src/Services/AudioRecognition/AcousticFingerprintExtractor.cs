using System.Buffers.Binary;
using QmTui.Utils;

namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 高性能原生声学指纹特征提取器
/// 纯 C# 原生实现，全预计算查表与高密度位流组装，零外部依赖。
/// </summary>
public static class AcousticFingerprintExtractor
{
    public const int SampleRate = 8000;
    public const int WindowSize = 1024;
    public const int HopSize = 128;
    public const int FftSize = 1024;
    public const int ChannelCount = 4;
    public const int TimeWindow = 5;
    public const int FreqWindow = 20;
    public const int MaxPeaksPerFrame = 5;
    public const float MagThreshold = 20000.0f;

    private static readonly float[] s_hammingWindow = InitHammingWindow();
    private static readonly (int i, int j)[] s_bitRevSwaps = InitBitRevSwaps();
    private static readonly (double Re, double Im)[][] s_twiddles = InitTwiddles();

    private static readonly int[] s_modeBitWidths = [1, 2, 3, 4, 5, 6, 7, 9];
    private static readonly int[] s_modeItemCounts = [36, 18, 12, 9, 7, 6, 5, 4];
    private static readonly int[] s_modePaddingBits = [0, 0, 0, 0, 1, 0, 1, 0];
    private static readonly int[] s_maxDiffs = [1, 3, 4, 15, 31, 63, 127, 511];

    private static float[] InitHammingWindow()
    {
        var win = new float[WindowSize];
        for (int n = 0; n < WindowSize; n++)
        {
            win[n] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * n / (WindowSize - 1)));
        }
        return win;
    }

    private static (int, int)[] InitBitRevSwaps()
    {
        var swaps = new List<(int, int)>();
        int j = 0;
        for (int i = 0; i < FftSize - 1; i++)
        {
            if (i < j)
            {
                swaps.Add((i, j));
            }
            int k = FftSize >> 1;
            while (k <= j)
            {
                j -= k;
                k >>= 1;
            }
            j += k;
        }
        return swaps.ToArray();
    }

    private static (double Re, double Im)[][] InitTwiddles()
    {
        var tw = new (double Re, double Im)[10][];
        int stage = 0;
        for (int l = 1; l < FftSize; l <<= 1)
        {
            tw[stage] = new (double, double)[l];
            double angle = -Math.PI / l;
            double wRe = Math.Cos(angle);
            double wIm = Math.Sin(angle);
            double curRe = 1.0;
            double curIm = 0.0;
            for (int k = 0; k < l; k++)
            {
                tw[stage][k] = (curRe, curIm);
                double nextRe = curRe * wRe - curIm * wIm;
                double nextIm = curRe * wIm + curIm * wRe;
                curRe = nextRe;
                curIm = nextIm;
            }
            stage++;
        }
        return tw;
    }

    public readonly record struct Landmark(int Time, int Freq);

    /// <summary>
    /// 从 8000Hz 16-bit 单声道 PCM 样本直接提取声学特征实体
    /// </summary>
    public static AcousticFeature? Extract(ReadOnlySpan<short> samples)
    {
        if (samples.Length < WindowSize + HopSize * (TimeWindow * ChannelCount))
        {
            return null;
        }

        var landmarks = ExtractLandmarks(samples);
        byte[] featBytes = PackLandmarks(landmarks);
        if (featBytes.Length == 0)
        {
            return null;
        }

        float duration = (float)samples.Length / SampleRate;
        return new AcousticFeature(featBytes, duration, 0, 0.0f);
    }

    /// <summary>
    /// 从 8000Hz 16-bit 单声道 PCM 原始字节流提取四通道特征点坐标
    /// </summary>
    public static List<Landmark>[] ExtractLandmarks(ReadOnlySpan<byte> pcmBytes)
    {
        int totalSamples = pcmBytes.Length / 2;
        var samples = new short[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes[(i * 2)..]);
        }
        return ExtractLandmarks(samples.AsSpan());
    }

    /// <summary>
    /// 从 8000Hz 16-bit 单声道 PCM 提取四通道特征点坐标
    /// </summary>
    public static List<Landmark>[] ExtractLandmarks(ReadOnlySpan<short> samples)
    {
        int totalSamples = samples.Length;
        int numFrames = (totalSamples - WindowSize) / HopSize + 1;
        if (numFrames <= TimeWindow * ChannelCount)
        {
            return [[], [], [], []];
        }

        var spectrogram = new float[numFrames][];
        var fftRe = new double[FftSize];
        var fftIm = new double[FftSize];

        for (int i = 0; i < numFrames; i++)
        {
            int stStart = i * HopSize;
            for (int n = 0; n < WindowSize; n++)
            {
                fftRe[n] = samples[stStart + n] * s_hammingWindow[n];
                fftIm[n] = 0.0;
            }

            ComputeFft(fftRe, fftIm);

            var mag = new float[FftSize / 2 + 1];
            for (int f = 0; f <= FftSize / 2; f++)
            {
                mag[f] = (float)Math.Sqrt(fftRe[f] * fftRe[f] + fftIm[f] * fftIm[f]);
            }
            spectrogram[i] = mag;
        }

        // 4 相时域交织分流
        var channelSpecs = new List<float[]>[ChannelCount];
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            channelSpecs[ch] = new List<float[]>(numFrames / ChannelCount + 1);
        }

        for (int i = 0; i < numFrames; i++)
        {
            channelSpecs[i % ChannelCount].Add(spectrogram[i]);
        }

        var result = new List<Landmark>[ChannelCount];

        // 各通道执行 2D NMS
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            var spec = channelSpecs[ch];
            int numT = spec.Count;
            result[ch] = new List<Landmark>();

            int targetEnd = Math.Max(0, numT - TimeWindow);
            for (int t = 0; t < targetEnd; t++)
            {
                var cands = new List<(int f, float val)>();
                for (int f = 3; f <= 510; f++)
                {
                    float val = spec[t][f];
                    if (val < MagThreshold) continue;

                    int tStart = Math.Max(0, t - TimeWindow);
                    int tEnd = t + TimeWindow;
                    int fStart = Math.Max(3, f - FreqWindow);
                    int fEnd = Math.Min(510, f + FreqWindow);

                    bool isLocalMax = true;
                    for (int tPrime = tStart; tPrime <= tEnd && isLocalMax; tPrime++)
                    {
                        var row = spec[tPrime];
                        for (int fPrime = fStart; fPrime <= fEnd; fPrime++)
                        {
                            if (row[fPrime] > val)
                            {
                                isLocalMax = false;
                                break;
                            }
                        }
                    }

                    if (isLocalMax)
                    {
                        cands.Add((f, val));
                    }
                }

                cands.Sort((a, b) => b.val.CompareTo(a.val));
                int take = Math.Min(MaxPeaksPerFrame, cands.Count);
                for (int k = 0; k < take; k++)
                {
                    result[ch].Add(new Landmark(t, cands[k].f));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 时间帧一阶差分自适应分块熵编码容器 (Part 1)
    /// </summary>
    public static byte[] PackPart1(List<int> times)
    {
        int n = times.Count;
        if (n == 0) return [0, 0, 0, 0];

        var blocks = new List<(int t0, int mode, List<int> diffs)>();
        int currIdx = 0;
        while (currIdx < n)
        {
            int t0 = times[currIdx];
            int chosenMode = 7;
            for (int m = 0; m < 8; m++)
            {
                int cnt = s_modeItemCounts[m];
                int maxVal = s_maxDiffs[m];
                bool valid = true;
                for (int k = 0; k < cnt; k++)
                {
                    int idx2 = currIdx + 1 + k;
                    int diff = (idx2 < n) ? (times[idx2] - times[idx2 - 1]) : 0;
                    if (diff > maxVal)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                {
                    chosenMode = m;
                    break;
                }
            }

            int blockCnt = s_modeItemCounts[chosenMode];
            var blockDiffs = new List<int>(blockCnt);
            for (int k = 0; k < blockCnt; k++)
            {
                int idx2 = currIdx + 1 + k;
                int diff = (idx2 < n) ? (times[idx2] - times[idx2 - 1]) : 0;
                blockDiffs.Add(diff);
            }

            blocks.Add((t0, chosenMode, blockDiffs));
            currIdx += 1 + blockCnt;
        }

        var bw = new BitWriter();
        bw.WriteBits(n, 16);
        bw.WriteBits(blocks.Count, 16);

        foreach (var (t0, mode, diffs) in blocks)
        {
            bw.WriteBits(t0 & 0x1FF, 9);
            bw.WriteBits(mode & 7, 3);
            int w = s_modeBitWidths[mode];
            foreach (var d in diffs)
            {
                bw.WriteBits(d, w);
            }
            if (s_modePaddingBits[mode] > 0)
            {
                bw.WriteBits(0, 1);
            }
        }

        return bw.ToBytes();
    }

    /// <summary>
    /// 频点 9-bit 定长紧凑流序列化 (Part 2)
    /// </summary>
    public static byte[] PackPart2(List<int> freqs)
    {
        var bw = new BitWriter();
        bw.WriteBits(freqs.Count, 16);
        foreach (var f in freqs)
        {
            bw.WriteBits(f & 0x1FF, 9);
        }
        return bw.ToBytes();
    }

    /// <summary>
    /// 将提取的四通道极大值坐标组装为通用动态变长二进制指纹
    /// </summary>
    public static byte[] PackLandmarks(List<Landmark>[] channelPeaks)
    {
        using var ms = new MemoryStream();

        for (int b = 0; b < ChannelCount; b++)
        {
            var peaks = b < channelPeaks.Length ? channelPeaks[b] : [];
            var times = new List<int>(peaks.Count);
            var freqs = new List<int>(peaks.Count);
            for (int p = 0; p < peaks.Count; p++)
            {
                times.Add(peaks[p].Time);
                freqs.Add(peaks[p].Freq);
            }

            var p1 = PackPart1(times);
            var p2 = PackPart2(freqs);

            ms.Write(p1);
            ms.Write(p2);
        }

        return ms.ToArray();
    }

    private static void ComputeFft(double[] re, double[] im)
    {
        for (int idx = 0; idx < s_bitRevSwaps.Length; idx++)
        {
            var (i, j) = s_bitRevSwaps[idx];
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        int stage = 0;
        for (int l = 1; l < FftSize; l <<= 1)
        {
            var stageTwiddles = s_twiddles[stage++];
            int step = l << 1;
            for (int m = 0; m < FftSize; m += step)
            {
                for (int k = 0; k < l; k++)
                {
                    int p = m + k;
                    int q = p + l;

                    var (curRe, curIm) = stageTwiddles[k];
                    double tRe = curRe * re[q] - curIm * im[q];
                    double tIm = curRe * im[q] + curIm * re[q];

                    re[q] = re[p] - tRe;
                    im[q] = im[p] - tIm;
                    re[p] += tRe;
                    im[p] += tIm;
                }
            }
        }
    }

    private sealed class BitWriter
    {
        private byte[] _buffer = new byte[256];
        private int _bitCount;

        public void WriteBits(int val, int numBits)
        {
            for (int i = numBits - 1; i >= 0; i--)
            {
                int bit = (val >> i) & 1;
                int byteIdx = _bitCount >> 3;
                int bitPos = 7 - (_bitCount & 7);

                if (byteIdx >= _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                if (bit != 0)
                {
                    _buffer[byteIdx] |= (byte)(1 << bitPos);
                }

                _bitCount++;
            }
        }

        public byte[] ToBytes()
        {
            int totalBytes = (_bitCount + 7) >> 3;
            var outBytes = new byte[totalBytes];
            Array.Copy(_buffer, outBytes, totalBytes);
            return outBytes;
        }
    }
}
