using QmTui.Models;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

public class AudioCacheDeduplicationTests
{
    [Theory]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.HiRes, true)]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.SQ, true)]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.HQ, true)]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.Standard, true)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.SQ, true)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.HQ, true)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.Standard, true)]
    [InlineData(AudioQualityTier.SQ, AudioQualityTier.HQ, true)]
    [InlineData(AudioQualityTier.SQ, AudioQualityTier.Standard, true)]
    [InlineData(AudioQualityTier.HQ, AudioQualityTier.Standard, true)]
    public void ShouldPrune_StereoTrack_HigherQualityShouldPruneLowerQuality(
        AudioQualityTier currentTier, AudioQualityTier cachedTier, bool expected)
    {
        Assert.Equal(expected, AudioCacheService.ShouldPrune(currentTier, cachedTier));
    }

    [Theory]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.Standard, false)]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.HQ, false)]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.SQ, false)]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.HiRes, false)]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.Master, false)]
    [InlineData(AudioQualityTier.HQ, AudioQualityTier.HQ, false)]
    [InlineData(AudioQualityTier.HQ, AudioQualityTier.SQ, false)]
    [InlineData(AudioQualityTier.SQ, AudioQualityTier.SQ, false)]
    [InlineData(AudioQualityTier.SQ, AudioQualityTier.HiRes, false)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.HiRes, false)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.Master, false)]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.Master, false)]
    public void ShouldPrune_StereoTrack_LowerOrEqualShouldNotPrune(
        AudioQualityTier currentTier, AudioQualityTier cachedTier, bool expected)
    {
        Assert.Equal(expected, AudioCacheService.ShouldPrune(currentTier, cachedTier));
    }

    [Theory]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.Premium, true)]
    [InlineData(AudioQualityTier.Dolby, AudioQualityTier.Premium, true)]
    public void ShouldPrune_SpatialTrack_HigherQualityShouldPruneLowerQuality(
        AudioQualityTier currentTier, AudioQualityTier cachedTier, bool expected)
    {
        Assert.Equal(expected, AudioCacheService.ShouldPrune(currentTier, cachedTier));
    }

    [Theory]
    [InlineData(AudioQualityTier.Premium, AudioQualityTier.Premium, false)]
    [InlineData(AudioQualityTier.Premium, AudioQualityTier.Atmos, false)]
    [InlineData(AudioQualityTier.Premium, AudioQualityTier.Dolby, false)]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.Atmos, false)]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.Dolby, false)]
    [InlineData(AudioQualityTier.Dolby, AudioQualityTier.Atmos, false)]
    [InlineData(AudioQualityTier.Dolby, AudioQualityTier.Dolby, false)]
    public void ShouldPrune_SpatialTrack_LowerOrEqualShouldNotPrune(
        AudioQualityTier currentTier, AudioQualityTier cachedTier, bool expected)
    {
        Assert.Equal(expected, AudioCacheService.ShouldPrune(currentTier, cachedTier));
    }

    [Theory]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.Atmos, false)]
    [InlineData(AudioQualityTier.Master, AudioQualityTier.Dolby, false)]
    [InlineData(AudioQualityTier.HiRes, AudioQualityTier.Premium, false)]
    [InlineData(AudioQualityTier.SQ, AudioQualityTier.Atmos, false)]
    [InlineData(AudioQualityTier.Standard, AudioQualityTier.Premium, false)]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.Master, false)]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.HiRes, false)]
    [InlineData(AudioQualityTier.Atmos, AudioQualityTier.SQ, false)]
    [InlineData(AudioQualityTier.Dolby, AudioQualityTier.HQ, false)]
    [InlineData(AudioQualityTier.Premium, AudioQualityTier.Standard, false)]
    public void ShouldPrune_CrossTrack_ShouldNeverPrune(
        AudioQualityTier currentTier, AudioQualityTier cachedTier, bool expected)
    {
        Assert.Equal(expected, AudioCacheService.ShouldPrune(currentTier, cachedTier));
    }
}
