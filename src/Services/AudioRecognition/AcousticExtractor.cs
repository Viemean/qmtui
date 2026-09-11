namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 声学指纹提取器门面
/// </summary>
public static class AcousticExtractor
{
    public static AcousticFeature? Extract(ReadOnlySpan<short> samples) => AcousticFingerprintExtractor.Extract(samples);
    public static List<AcousticFingerprintExtractor.Landmark>[] ExtractLandmarks(ReadOnlySpan<byte> pcmBytes) => AcousticFingerprintExtractor.ExtractLandmarks(pcmBytes);
    public static List<AcousticFingerprintExtractor.Landmark>[] ExtractLandmarks(ReadOnlySpan<short> samples) => AcousticFingerprintExtractor.ExtractLandmarks(samples);
    public static byte[] PackPart1(List<int> times) => AcousticFingerprintExtractor.PackPart1(times);
    public static byte[] PackPart2(List<int> freqs) => AcousticFingerprintExtractor.PackPart2(freqs);
    public static byte[] PackLandmarks(List<AcousticFingerprintExtractor.Landmark>[] channelPeaks) => AcousticFingerprintExtractor.PackLandmarks(channelPeaks);
}
