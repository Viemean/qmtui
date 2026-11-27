using QmTui.Models;
using QmTui.Utils;
using Xunit;

namespace QmTui.Tests;

public class QQMusicLinkParserTests
{
    [Theory]
    [InlineData("https://y.qq.com/n/ryqq/playlist/8277217796")]
    [InlineData("https://y.qq.com/n/yqq/playlist/8277217796.html")]
    [InlineData("https://y.qq.com/n/ryqq/taoge/8277217796")]
    [InlineData("https://i.y.qq.com/n2/m/share/details/taoge.html?id=8277217796")]
    [InlineData("https://y.qq.com/share?disstid=8277217796")]
    [InlineData("disstid=8277217796")]
    public void Classify_PlaylistLink_ReturnsId(string input)
    {
        var kind = QQMusicLinkParser.Classify(input, out var value);

        Assert.Equal(SearchInputKind.PlaylistLink, kind);
        Assert.Equal(8277217796L, value);
    }

    [Fact]
    public void Classify_RawDigits_ReturnsNumericId()
    {
        var kind = QQMusicLinkParser.Classify("123456", out var value);

        Assert.Equal(SearchInputKind.NumericId, kind);
        Assert.Equal(123456L, value);
    }

    [Theory]
    [InlineData("周杰伦")]
    [InlineData("123abc")]
    [InlineData("https://example.com/123")]
    public void Classify_OtherText_ReturnsNickname(string input)
    {
        var kind = QQMusicLinkParser.Classify(input, out var value);

        Assert.Equal(SearchInputKind.Nickname, kind);
        Assert.Equal(0L, value);
    }

    [Fact]
    public void Classify_ShortShareLink_IsDetected()
    {
        var kind = QQMusicLinkParser.Classify("https://c6.y.qq.com/base/fcgi-bin/u?__=abc", out var value);

        Assert.Equal(SearchInputKind.ShortShareLink, kind);
        Assert.Equal(0L, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_EmptyInput_ReturnsNone(string input)
    {
        var kind = QQMusicLinkParser.Classify(input, out var value);

        Assert.Equal(SearchInputKind.None, kind);
        Assert.Equal(0L, value);
    }

    [Fact]
    public void Classify_ZeroId_IsNotTreatedAsPlaylist()
    {
        var kind = QQMusicLinkParser.Classify("disstid=0", out var value);

        Assert.NotEqual(SearchInputKind.PlaylistLink, kind);
        Assert.Equal(0L, value);
    }

    [Theory]
    [InlineData(3, true, NumericTargetKind.Both)]
    [InlineData(3, false, NumericTargetKind.UserPlaylists)]
    [InlineData(0, true, NumericTargetKind.PublicPlaylist)]
    [InlineData(0, false, NumericTargetKind.None)]
    public void ChooseNumericTarget_ResolvesBothKindsOfNumber(int userPlaylistCount, bool hasPublicPlaylist, NumericTargetKind expected)
    {
        Assert.Equal(expected, QQMusicLinkParser.ChooseNumericTarget(userPlaylistCount, hasPublicPlaylist));
    }

    [Fact]
    public void Playlist_OtherOwner_IsNotFavoriteOrCreated()
    {
        var playlist = new Playlist(201, "我喜欢", 10, OwnerUin: 42);

        Assert.True(playlist.IsOwnedByOther);
        Assert.False(playlist.IsMyFavorite);
        Assert.False(playlist.IsCreated);
    }

    [Fact]
    public void Playlist_OwnFavorite_KeepsLegacySemantics()
    {
        var playlist = new Playlist(201, "我喜欢", 10);

        Assert.True(playlist.IsMyFavorite);
        Assert.True(playlist.IsCreated);
    }
}
