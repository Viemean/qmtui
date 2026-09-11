namespace QmTui.Services.AudioRecognition;

/// <summary>
/// 原生算法提取器桥接门面
/// </summary>
public static class NativeRunnerBridge
{
    public static bool IsAvailable => true;
    public static string? RunnerPath => null;
    public static string? SysrootPath => null;
    public static string? ModelPath => null;

    public static AcousticFeature? Extract(short[] pcm8k)
    {
        return AcousticFingerprintExtractor.Extract(pcm8k);
    }
}
