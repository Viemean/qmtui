using Terminal.Gui.Drawing;
using QmTui.UI;
using Xunit;

namespace QmTui.Tests;

/// <summary>
/// 亮色/暗色/自动主题模式切换单元测试。
/// </summary>
public class ThemeModeTests
{
    [Fact]
    public void MikuTheme_ApplyTheme_SwitchToLight()
    {
        MikuTheme.ApplyTheme("light");

        Assert.True(MikuTheme.IsLightMode);
        Assert.Equal("light", MikuTheme.CurrentThemeMode);

        var textWhite = MikuTheme.MikuTextWhite;
        Assert.Equal(0x1F, textWhite.R);
        Assert.Equal(0x23, textWhite.G);
        Assert.Equal(0x28, textWhite.B);

        var green = MikuTheme.QqGreenPrimary;
        Assert.Equal(0x0F, green.R);
        Assert.Equal(0x82, green.G);
        Assert.Equal(0x49, green.B);

        Assert.Equal(textWhite, MikuTheme.Base.Normal.Foreground);
        Assert.Equal(Color.None, MikuTheme.Base.Normal.Background);

        Assert.Equal(textWhite, MikuTheme.TransparentDialog.Normal.Foreground);

        var playingFocus = MikuTheme.QqPlayingFocusText;
        Assert.Equal(0xA8, playingFocus.R);
        Assert.Equal(0xFF, playingFocus.G);
        Assert.Equal(0xD4, playingFocus.B);

        var lyricDim = MikuTheme.QqTextLyricDim;
        Assert.Equal(0x38, lyricDim.R);
        Assert.Equal(0x42, lyricDim.G);
        Assert.Equal(0x48, lyricDim.B);
    }

    [Fact]
    public void MikuTheme_ApplyTheme_SwitchToDark()
    {
        MikuTheme.ApplyTheme("dark");

        Assert.False(MikuTheme.IsLightMode);
        Assert.Equal("dark", MikuTheme.CurrentThemeMode);

        var textWhite = MikuTheme.MikuTextWhite;
        Assert.Equal(0xED, textWhite.R);
        Assert.Equal(0xF6, textWhite.G);
        Assert.Equal(0xF6, textWhite.B);

        var green = MikuTheme.QqGreenPrimary;
        Assert.Equal(0x31, green.R);
        Assert.Equal(0xC2, green.G);
        Assert.Equal(0x7C, green.B);

        Assert.Equal(textWhite, MikuTheme.Base.Normal.Foreground);
        Assert.Equal(textWhite, MikuTheme.TransparentDialog.Normal.Foreground);

        var playingFocus = MikuTheme.QqPlayingFocusText;
        Assert.Equal(0x5F, playingFocus.R);
        Assert.Equal(0xE3, playingFocus.G);
        Assert.Equal(0xA1, playingFocus.B);
    }

    [Fact]
    public void MikuTheme_ApplyTheme_AutoModeResolvesSuccessfully()
    {
        MikuTheme.ApplyTheme("auto");
        Assert.Equal("auto", MikuTheme.CurrentThemeMode);
        Assert.NotNull(MikuTheme.Base);
        Assert.NotNull(MikuTheme.Dialog);
        Assert.NotNull(MikuTheme.PlayerBar);
        Assert.NotNull(MikuTheme.Lyric);
    }
}
