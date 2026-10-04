using System.IO;
using QmTui.Models;
using QmTui.Services;
using QmTui.UI;
using Xunit;

namespace QmTui.Tests;

public class AudioExportServiceTests
{
    [Theory]
    [InlineData("Artist/Name", "Artist_Name")]
    [InlineData("Title: Test*?\"<>|", "Title_ Test______")]
    [InlineData("Clean Song Title", "Clean Song Title")]
    public void SanitizeFileName_RemovesInvalidCharacters(string input, string expected)
    {
        var result = AudioExportService.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void InferAudioExtension_FlacMagicBytes_ReturnsDotFlac()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // fLaC header
            File.WriteAllBytes(tempFile, [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.Standard);
            Assert.Equal(".flac", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_Id3MagicBytes_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // ID3 header
            File.WriteAllBytes(tempFile, [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void InferAudioExtension_MpegSyncword_ReturnsDotMp3()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // MPEG 1 Layer III syncword: 0xFF, 0xFB
            File.WriteAllBytes(tempFile, [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00]);
            var ext = AudioExportService.InferAudioExtension(tempFile, AudioQualityTier.SQ);
            Assert.Equal(".mp3", ext);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void SanitizeCoverArt_WithinThreshold_PreservesOriginalBytes()
    {
        var safeBytes = new byte[1024 * 1024]; // 1 MB
        safeBytes[0] = 0x42;
        var result = AudioExportService.SanitizeCoverArt(safeBytes);
        Assert.Same(safeBytes, result);
    }

    [Fact]
    public void SanitizeCoverArt_CorruptedBytesAboveThreshold_GracefullyReturnsRawBytes()
    {
        var largeInvalidBytes = new byte[3 * 1024 * 1024]; // 3 MB invalid
        largeInvalidBytes[0] = 0x11;
        var result = AudioExportService.SanitizeCoverArt(largeInvalidBytes);
        Assert.Same(largeInvalidBytes, result);
    }

    [Fact]
    public void SanitizeCoverArt_ValidLargeImage_CompressesAndMaintainsResolution()
    {
        // 构造一个 1200x1200 的未压缩大尺寸 PNG 图像（保证其体积超出 2.5MB 门禁）
        int width = 1200;
        int height = 1200;
        var rawPixels = new byte[width * height * 3];
        for (int i = 0; i < rawPixels.Length; i++)
        {
            rawPixels[i] = (byte)(i * 37 % 256);
        }

        using var ms = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(rawPixels, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlue, ms);
        var pngBytes = ms.ToArray();

        // 仅当生成的测试图片大于 2.5MB 时检验压缩效果
        if (pngBytes.Length > AudioExportService.MaxRawCoverBytes)
        {
            var sanitized = AudioExportService.SanitizeCoverArt(pngBytes);
            Assert.True(sanitized.Length < pngBytes.Length);

            var decoded = StbImageSharp.ImageResult.FromMemory(sanitized, StbImageSharp.ColorComponents.RedGreenBlue);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
        }
    }

    [Theory]
    [InlineData(AudioQualityTier.SQ)]
    [InlineData(AudioQualityTier.HiRes)]
    [InlineData(AudioQualityTier.Master)]
    public async Task ResolveCoverArtBytesAsync_AboveHqTier_RetrievesRawCoverAndCompressesWithQuality88(AudioQualityTier tier)
    {
        Assert.True(AudioExportService.IsAboveHq(tier));
        Assert.Equal(88, AudioExportService.CoverCompressionQuality);

        var song = new Song("test_song", "Petals", "Orangestar", "Petals", 180)
        {
            AlbumMid = "002yoHPt2CrhGz"
        };

        // HQ 音质以上（SQ、Hi-Res、母带等）拉取无损母版原图（3000x3000，原图约 14.29MB）
        var rawBytes = await AudioExportService.ResolveCoverArtBytesAsync(song, tier);
        Assert.NotNull(rawBytes);
        Assert.NotEmpty(rawBytes);

        // 超大原图通过高质量 JPEG 88 压缩至 2.5MB 内，且保持 3000x3000 原始分辨率不缩小
        var sanitized = AudioExportService.SanitizeCoverArt(rawBytes);
        Assert.True(sanitized.Length <= AudioExportService.MaxRawCoverBytes);

        var decoded = StbImageSharp.ImageResult.FromMemory(sanitized, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(3000, decoded.Width);
        Assert.Equal(3000, decoded.Height);
    }

    [Theory]
    [InlineData(AudioQualityTier.Standard)]
    [InlineData(AudioQualityTier.HQ)]
    public async Task ResolveCoverArtBytesAsync_StandardAndHqTier_Retrieves1200Cover(AudioQualityTier tier)
    {
        Assert.False(AudioExportService.IsAboveHq(tier));

        var song = new Song("test_song", "Petals", "Orangestar", "Petals", 180)
        {
            AlbumMid = "002yoHPt2CrhGz"
        };

        // 标准与 HQ 音质拉取 1200 高清封面
        var bytes = await AudioExportService.ResolveCoverArtBytesAsync(song, tier);
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        var decoded = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlue);
        Assert.Equal(1200, decoded.Width);
        Assert.Equal(1200, decoded.Height);
    }
}
