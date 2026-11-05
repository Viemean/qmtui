using System.Text;
using Xunit;
using QmTui.UI;
using QmTui.Utils;

namespace QmTui.Tests;

public class DisplayWidthTests
{
    [Fact]
    public void GetVisualWidth_Ascii_ReturnsCharCount()
    {
        var text = "Hello World!";
        var width = text.GetVisualWidth();
        Assert.Equal(12, width);
    }

    [Fact]
    public void GetVisualWidth_Chinese_ReturnsDoubleWidth()
    {
        var text = "七里香";
        var width = text.GetVisualWidth();
        Assert.Equal(6, width);
    }

    [Fact]
    public void GetVisualWidth_Mixed_CalculatesAccurately()
    {
        var text = "周杰伦 - 晴天 (Live)";
        var width = text.GetVisualWidth();
        Assert.Equal(20, width);
    }

    [Fact]
    public void GetVisualWidth_LatinAccented_CountsAsSingleColumn()
    {
        var text = "Café";
        var width = text.GetVisualWidth();
        Assert.Equal(4, width);
    }

    [Fact]
    public void TruncateAndPadWide_ShorterText_PadsToExactWidth()
    {
        var text = "晴天";
        var padded = SongListView.TruncateAndPadWide(text, 10);

        Assert.Equal(10, padded.GetVisualWidth());
        Assert.StartsWith("晴天", padded);
        Assert.EndsWith("      ", padded);
    }

    [Fact]
    public void TruncateAndPadWide_ExactWidth_ReturnsSame()
    {
        var text = "七里香";
        var result = SongListView.TruncateAndPadWide(text, 6);

        Assert.Equal(text, result);
        Assert.Equal(6, result.GetVisualWidth());
    }

    [Fact]
    public void TruncateAndPadWide_LongerText_TruncatesWithEllipsisAndPadsToExactWidth()
    {
        var text = "这是一首非常非常长的歌曲名字";
        var truncated = SongListView.TruncateAndPadWide(text, 12);

        Assert.Equal(12, truncated.GetVisualWidth());
        Assert.Contains("..", truncated);
    }

    [Fact]
    public void MultiColumn_AlignedCells_HaveConsistentWidth()
    {
        var item1 = SongListView.TruncateAndPadWide("短歌单", 30) + "  " + SongListView.TruncateAndPadWide("12 首", 10);
        var item2 = SongListView.TruncateAndPadWide("超长标题歌单名字测试一二三四五六七八九十", 30) + "  " + SongListView.TruncateAndPadWide("176 首", 10);

        Assert.Equal(30 + 2 + 10, item1.GetVisualWidth());
        Assert.Equal(30 + 2 + 10, item2.GetVisualWidth());
    }

    [Fact]
    public void TruncateAndPadWide_OddTargetWidth_PadsTrailingSpaceCorrectly()
    {
        var text = "晴天";
        var result = SongListView.TruncateAndPadWide(text, 5);

        Assert.Equal(5, result.GetVisualWidth());
        Assert.Equal("晴天 ", result);
    }

    [Fact]
    public void TruncateAndPadWide_OddWidthTruncation_MaintainsExactTargetWidth()
    {
        var text = "周杰伦晴天音乐";
        var result = SongListView.TruncateAndPadWide(text, 7);

        Assert.Equal(7, result.GetVisualWidth());
        Assert.EndsWith(" ", result);
        Assert.Contains("..", result);
    }

    [Fact]
    public void TruncateAndPadWide_ZeroOrNegativeWidth_ReturnsEmpty()
    {
        Assert.Equal("", SongListView.TruncateAndPadWide("晴天", 0));
        Assert.Equal("", SongListView.TruncateAndPadWide("晴天", -5));
    }

    [Fact]
    public void TruncateAndPadWide_EmptyText_ReturnsPadding()
    {
        Assert.Equal("     ", SongListView.TruncateAndPadWide("", 5));
        Assert.Equal("     ", SongListView.TruncateAndPadWide(null!, 5));
    }

    [Fact]
    public void TerminalStringExtensions_GetVisualWidth_CalculatesCorrectly()
    {
        Assert.Equal(0, ((string?)null).GetVisualWidth());
        Assert.Equal(0, "".GetVisualWidth());
        Assert.Equal(5, "Hello".GetVisualWidth());
        Assert.Equal(6, "七里香".GetVisualWidth());
        Assert.Equal(4, "Café".GetVisualWidth());
    }

    [Fact]
    public void TerminalStringExtensions_PaddingVisual_CalculatesCorrectly()
    {
        Assert.Equal("七里香    ", "七里香".PadRightVisual(10));
        Assert.Equal("    七里香", "七里香".PadLeftVisual(10));
        Assert.Equal("七里香", "七里香".PadRightVisual(6));
        Assert.Equal("          ", ((string?)null).PadRightVisual(10));
    }

    [Fact]
    public void CharAndRuneExtensions_GetVisualWidth_CalculatesCorrectly()
    {
        Assert.Equal(1, 'A'.GetVisualWidth());
        Assert.Equal(1, ' '.GetVisualWidth());
        Assert.Equal(2, '中'.GetVisualWidth());
        Assert.Equal(1, new Rune('A').GetVisualWidth());
        Assert.Equal(2, new Rune('中').GetVisualWidth());
    }
}
