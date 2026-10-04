using System;
using QmTui.Api;
using QmTui.Models;
using Xunit;

namespace QmTui.Tests;

public class SongCommentTests
{
    [Fact]
    public void DecodeHtmlEntities_NormalEntities_DecodesCorrectly()
    {
        var input = "你好&nbsp;世界&quot;&apos;&lt;&gt;&amp;&#10;测试";
        var output = MusicApi.DecodeHtmlEntities(input);

        Assert.Equal("你好 世界\"'<>&" + "\n测试", output);
    }

    [Fact]
    public void DecodeHtmlEntities_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", MusicApi.DecodeHtmlEntities(""));
        Assert.Equal("", MusicApi.DecodeHtmlEntities("   "));
    }

    [Fact]
    public void SongComment_CreationAndProperties_MatchExpected()
    {
        var comment = new SongComment(
            CommentId: "12345",
            Nick: "测试用户",
            AvatarUrl: "https://example.com/avatar.jpg",
            Content: "歌曲非常好听",
            TimeSec: 1700000000L,
            PraiseNum: 42,
            IsHot: true,
            PicUrl: "https://example.com/pic.jpg",
            PicSize: "100x100",
            Location: "上海",
            SeqNo: "seq_1"
        );

        Assert.Equal("12345", comment.CommentId);
        Assert.Equal("测试用户", comment.Nick);
        Assert.True(comment.IsHot);
        Assert.Equal(42, comment.PraiseNum);
        Assert.Equal("上海", comment.Location);
    }
}
