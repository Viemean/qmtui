using System;
using QmTui.Models;
using QmTui.Services;
using QmTui.UI;
using Xunit;

namespace QmTui.Tests;

public class NotificationAndCoverTests
{
    [Fact]
    public void HasSessionBusAddress_Respects_NoNotifyEnv()
    {
        var oldEnv = Environment.GetEnvironmentVariable("QMTUI_NO_NOTIFY");
        try
        {
            Environment.SetEnvironmentVariable("QMTUI_NO_NOTIFY", "1");
            Assert.False(DesktopNotificationService.HasSessionBusAddress());
        }
        finally
        {
            Environment.SetEnvironmentVariable("QMTUI_NO_NOTIFY", oldEnv);
        }
    }

    [Fact]
    public async Task EnsureCoverAsync_NullOrEmptyFilePath_ReturnsNull()
    {
        var emptySong = new Song("test", "Test Title", "Test Artist", "Test Album", 120) { LocalFilePath = "" };
        var cover = await LocalMusicService.EnsureCoverAsync(emptySong);
        Assert.Null(cover);

        var nonExistentSong = new Song("test2", "Test Title", "Test Artist", "Test Album", 120) 
        { 
            LocalFilePath = Path.Combine(Path.GetTempPath(), "non_existent_music_file_xyz.mp3") 
        };
        var cover2 = await LocalMusicService.EnsureCoverAsync(nonExistentSong);
        Assert.Null(cover2);
    }

    [Fact]
    public void GetPngDimensions_And_CoverVersion_WorksCorrectly()
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"test_header_{Guid.NewGuid():N}.png");
        try
        {
            // 构造合法的 24 字节 PNG IHDR 头部: 宽 800 (0x0320), 高 600 (0x0258)
            byte[] header =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D,
                0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x03, 0x20, // 800
                0x00, 0x00, 0x02, 0x58  // 600
            ];
            File.WriteAllBytes(tempPng, header);

            var dims = TerminalImageHelper.GetPngDimensions(tempPng);
            Assert.NotNull(dims);
            Assert.Equal(800, dims.Value.width);
            Assert.Equal(600, dims.Value.height);

            // 测试版本追踪机制
            var testKey = $"test_album_{Guid.NewGuid():N}";
            Assert.Equal(0, TerminalImageHelper.GetCoverVersion(testKey));

            TerminalImageHelper.RecordCoverVersion(testKey, TerminalImageHelper.CurrentCoverVersion);
            Assert.Equal(TerminalImageHelper.CurrentCoverVersion, TerminalImageHelper.GetCoverVersion(testKey));
        }
        finally
        {
            try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch {}
        }
    }

    [Fact]
    public void DecodeImageRgba_WebP_DecodesSuccessfully()
    {
        byte[] webpBytes =
        [
            82, 73, 70, 70, 60, 0, 0, 0, 87, 69, 66, 80, 86, 80, 56, 32,
            48, 0, 0, 0, 208, 1, 0, 157, 1, 42, 2, 0, 2, 0, 1, 64,
            38, 37, 160, 2, 116, 186, 1, 248, 0, 3, 176, 0, 254, 242, 235, 127,
            252, 216, 21, 205, 115, 239, 247, 255, 210, 224, 253, 46, 15, 210, 224, 255,
            210, 144, 0, 0
        ];

        var result = TerminalImageHelper.DecodeImageRgba(webpBytes, isWebp: true);
        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(result);
            Assert.Equal(2, result.Value.width);
            Assert.Equal(2, result.Value.height);
            Assert.Equal(2 * 2 * 4, result.Value.pixelData.Length);
        }
    }
}
