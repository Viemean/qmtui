using QmTui.Models;
using Xunit;

namespace QmTui.Tests;

public class AudioQualityFallbackTests
{
    [Fact]
    public void Master_ShouldFallbackSequentially()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.Master);
        Assert.Contains(AudioQualityTier.HiRes, fallbacks);
        Assert.Contains(AudioQualityTier.SQ, fallbacks);
        Assert.Contains(AudioQualityTier.HQ, fallbacks);
        Assert.Contains(AudioQualityTier.Standard, fallbacks);
    }

    [Fact]
    public void HiRes_ShouldFallbackToSQ_HQ_Standard()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.HiRes);
        Assert.Equal([AudioQualityTier.SQ, AudioQualityTier.HQ, AudioQualityTier.Standard], fallbacks);
    }

    [Fact]
    public void SQ_ShouldFallbackToHQ_Standard()
    {
        var fallbacks = AudioQualityHelper.GetFallbackTiers(AudioQualityTier.SQ);
        Assert.Equal([AudioQualityTier.HQ, AudioQualityTier.Standard], fallbacks);
    }

    [Fact]
    public void QualityOption_DisplayText_UnavailableShouldShowNotice()
    {
        var option = new QualityOption(AudioQualityTier.Master, "Master", "臻品母带", "24bit/192kHz", "", false);
        var text = option.DisplayText(false);
        Assert.Contains("(无音源)", text);
    }

    [Fact]
    public void QualityOption_DisplayText_AvailableShouldShowNormal()
    {
        var option = new QualityOption(AudioQualityTier.SQ, "SQ", "SQ 无损", "16bit/44.1kHz", "850kbps", true);
        var text = option.DisplayText(true);
        Assert.DoesNotContain("无音源", text);
        Assert.Contains("✓", text);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_FlacExtension_ShouldReturnSQ()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("标准 128k", "/music/song.flac");
        Assert.Equal(AudioQualityTier.SQ, tier);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_HiResFlac_ShouldReturnHiRes()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("Hi-Res 24bit", "/music/song.flac");
        Assert.Equal(AudioQualityTier.HiRes, tier);
    }

    [Fact]
    public void DetermineLocalOrWebDavTier_Mp3320_ShouldReturnHQ()
    {
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier("HQ 320k", "/music/song.mp3");
        Assert.Equal(AudioQualityTier.HQ, tier);
    }
}
