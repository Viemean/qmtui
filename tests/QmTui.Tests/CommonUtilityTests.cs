using Xunit;
using QmTui.Utils;

namespace QmTui.Tests;

public class CommonUtilityTests
{
    [Fact]
    public void JsonHelper_EscapeString_HandlesNullAndSpecialChars()
    {
        Assert.Equal("", JsonHelper.EscapeString(null));
        Assert.Equal("", JsonHelper.EscapeString(""));
        Assert.Equal("hello", JsonHelper.EscapeString("hello"));
        Assert.Equal("hello\\\"world\\\"", JsonHelper.EscapeString("hello\"world\""));
        Assert.Equal("a\\\\b", JsonHelper.EscapeString("a\\b"));
        Assert.Equal("line1\\nline2", JsonHelper.EscapeString("line1\nline2"));
        Assert.Equal("col1\\tcol2", JsonHelper.EscapeString("col1\tcol2"));
        // CJK should not be HTML-encoded
        Assert.Equal("晴天 - 周杰伦", JsonHelper.EscapeString("晴天 - 周杰伦"));
    }

    [Fact]
    public void CryptoHelper_ComputeMd5Hex_CalculatesAccurateLowercaseHash()
    {
        Assert.Equal("", CryptoHelper.ComputeMd5Hex(null));
        Assert.Equal("", CryptoHelper.ComputeMd5Hex(""));
        Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", CryptoHelper.ComputeMd5Hex("hello world"));
    }

    [Fact]
    public void CryptoHelper_ComputeMd5Bytes_Returns16Bytes()
    {
        Assert.Empty(CryptoHelper.ComputeMd5Bytes(null));
        Assert.Empty(CryptoHelper.ComputeMd5Bytes(""));
        var bytes = CryptoHelper.ComputeMd5Bytes("hello world");
        Assert.Equal(16, bytes.Length);
    }

    [Fact]
    public void FormatHelper_FormatFileSize_FormatsStandardTiers()
    {
        Assert.Equal("", FormatHelper.FormatFileSize(0));
        Assert.Equal("", FormatHelper.FormatFileSize(-100));
        Assert.Equal("500B", FormatHelper.FormatFileSize(500));
        Assert.Equal("1.0KB", FormatHelper.FormatFileSize(1024));
        Assert.Equal("2.5MB", FormatHelper.FormatFileSize((long)(2.5 * 1024 * 1024)));
        Assert.Equal("1.2GB", FormatHelper.FormatFileSize((long)(1.2 * 1024 * 1024 * 1024)));
    }

    [Theory]
    [InlineData("01. 晴天", "晴天")]
    [InlineData("02 - 七里香", "七里香")]
    [InlineData("03 夜曲", "夜曲")]
    [InlineData("[04] 稻香", "稻香")]
    [InlineData("普通歌曲", "普通歌曲")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void MusicTitleHelper_CleanTrackNumberPrefix_StripsIndexAccurately(string? input, string? expected)
    {
        Assert.Equal(expected, MusicTitleHelper.CleanTrackNumberPrefix(input!));
    }
}
