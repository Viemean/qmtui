using Xunit;
using QmTui.Utils;

namespace QmTui.Tests;

public class LyricParserTests
{
    [Fact]
    public void ParseLrc_StandardTimestamps_ParsesCorrectly()
    {
        var lrc = "[00:12.34]第一行歌词\n[01:05.678]第二行歌词";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Equal(2, result.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 12, 340), result[0].Timestamp);
        Assert.Equal("第一行歌词", result[0].Text);

        Assert.Equal(new TimeSpan(0, 0, 1, 5, 678), result[1].Timestamp);
        Assert.Equal("第二行歌词", result[1].Text);
    }

    [Fact]
    public void ParseLrc_WithoutMilliseconds_ParsesCorrectly()
    {
        var lrc = "[01:30]没有毫秒的歌词行";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal(new TimeSpan(0, 0, 1, 30, 0), result[0].Timestamp);
        Assert.Equal("没有毫秒的歌词行", result[0].Text);
    }

    [Fact]
    public void ParseLrc_MultipleTimestamps_SplitsCorrectly()
    {
        var lrc = "[00:10.00][00:20.00]副歌重复段落";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Equal(2, result.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 10, 0), result[0].Timestamp);
        Assert.Equal("副歌重复段落", result[0].Text);

        Assert.Equal(new TimeSpan(0, 0, 0, 20, 0), result[1].Timestamp);
        Assert.Equal("副歌重复段落", result[1].Text);
    }

    [Fact]
    public void ParseLrc_HtmlEntities_ReplacesApos()
    {
        var lrc = "[00:05.00]Don&apos;t go away";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("Don’t go away", result[0].Text);
    }

    [Fact]
    public void ParseLrc_EmptyOrComment_Skips()
    {
        var lrc = "\n[ti:Test Title]\n//\n   \n[00:01.00]Valid";
        var result = LyricParser.ParseLrc(lrc);

        Assert.Single(result);
        Assert.Equal("Valid", result[0].Text);
    }

    [Fact]
    public void DecodeBase64_ValidBase64_DecodesSuccessfully()
    {
        var plain = "qmtui 终端播放器";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
        var decoded = LyricParser.DecodeBase64(b64);

        Assert.Equal(plain, decoded);
    }

    [Fact]
    public void DecodeBase64_InvalidBase64_ReturnsEmptyString()
    {
        var decoded = LyricParser.DecodeBase64("This is not base64!!!");
        Assert.Equal("", decoded);
    }

    [Fact]
    public void IsPlaceholderLyrics_CorrectlyIdentifiesPlaceholders()
    {
        var instrumental = LyricParser.ParseSingleLrc("[00:00.00]此歌曲为没有填词的纯音乐，请您欣赏");
        Assert.True(LyricParser.IsPlaceholderLyrics(instrumental));

        var noLyrics = LyricParser.ParseSingleLrc("[00:00.00]暂无歌词");
        Assert.True(LyricParser.IsPlaceholderLyrics(noLyrics));

        List<QmTui.Models.LyricLine> emptyList = [];
        Assert.True(LyricParser.IsPlaceholderLyrics(emptyList));

        var normalLrc = LyricParser.ParseSingleLrc("[00:01.00]Hello world\n[00:05.00]Second line of song");
        Assert.False(LyricParser.IsPlaceholderLyrics(normalLrc));
    }

    [Fact]
    public void ExtractQrcLyricContent_HandlesUnescapedQuotesAndEntities()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QrcInfos>
            <LyricInfo LyricCount="1">
            <Lyric_1 LyricType="1" LyricContent="[ti:ぶれないアイで (坚定的eye) (&quot;初音未来xROHTO Digital Eye&quot;合作曲)]
            [ar:Mitchie M]
            [931,4869]ぶ(931,497)れ(1428,34)な(1462,33)い(1495,32)
            "/>
            </LyricInfo>
            </QrcInfos>
            """;

        var extracted = LyricParser.ExtractQrcLyricContent(xml);
        Assert.Contains("[931,4869]ぶ(931,497)れ", extracted);

        var lines = LyricParser.ParseQrcLyrics(xml);
        Assert.Single(lines);
        Assert.Equal("ぶれない", lines[0].Text);
        Assert.NotNull(lines[0].Words);
        Assert.Equal(4, lines[0].Words!.Count);
        Assert.Equal("ぶ", lines[0].Words![0].Word);
        Assert.Equal(0, lines[0].Words![0].OffsetMs);
        Assert.Equal(497, lines[0].Words![0].DurationMs);
    }

    [Fact]
    public void ParseQrcLine_LeadingTagStyle_ParsesWordsCorrectly()
    {
        var line = "[00:10.00](0,300)旋(300,200)律(500,400)家";
        var parsed = LyricParser.ParseQrcLine(line);

        Assert.NotNull(parsed);
        Assert.Equal("旋律家", parsed.Text);
        Assert.Equal(new TimeSpan(0, 0, 10), parsed.Timestamp);
        Assert.NotNull(parsed.Words);
        Assert.Equal(3, parsed.Words!.Count);
        Assert.Equal("旋", parsed.Words![0].Word);
        Assert.Equal(0, parsed.Words![0].OffsetMs);
        Assert.Equal(300, parsed.Words![0].DurationMs);
    }

    [Fact]
    public void MergeLyrics_QrcWithTranslation_MergesCorrectly()
    {
        var qrc = """
            [0,2000]你(0,500)好(500,500)
            [2000,3000]世(2000,1000)界(3000,1000)
            """;
        var trans = """
            [00:00.00]Hello
            [00:02.00]World
            """;

        var merged = LyricParser.MergeLyrics(qrc, trans);
        Assert.Equal(2, merged.Count);
        Assert.Equal("你好", merged[0].Text);
        Assert.Equal("Hello", merged[0].Trans);
        Assert.NotNull(merged[0].Words);
        Assert.Equal(2, merged[0].Words!.Count);

        Assert.Equal("世界", merged[1].Text);
        Assert.Equal("World", merged[1].Trans);
        Assert.NotNull(merged[1].Words);
        Assert.Equal(2, merged[1].Words!.Count);
    }

    [Fact]
    public void QrcDecoder_InvalidHex_ReturnsEmptyString()
    {
        Assert.Equal("", QrcDecoder.DecodeQrc(null));
        Assert.Equal("", QrcDecoder.DecodeQrc(""));
        Assert.Equal("", QrcDecoder.DecodeQrc("123")); // 奇数长度
        Assert.Equal("", QrcDecoder.DecodeQrc("ZZZZ")); // 非十六进制
    }

    [Fact]
    public void HasWordLevelSync_DetectsPresenceCorrectly()
    {
        var lineLevel = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "Hello World")
        };
        Assert.False(LyricParser.HasWordLevelSync(lineLevel));

        var wordSynced = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "Hello World", "", [new QmTui.Models.WordSpan("Hello", 0, 500)])
        };
        Assert.True(LyricParser.HasWordLevelSync(wordSynced));
    }

    [Fact]
    public void LyricListDataSource_ConstructsWithWordSyncedTimesCorrectly()
    {
        var lyrics = new List<QmTui.Models.LyricLine>
        {
            new(TimeSpan.FromSeconds(1), "你好世界", "Hello World", [
                new QmTui.Models.WordSpan("你好", 0, 400),
                new QmTui.Models.WordSpan("世界", 400, 500)
            ])
        };

        Dictionary<int, int> lineMap = [];
        List<int> itemMap = [];

        var ds = new QmTui.UI.LyricListDataSource(lyrics, showTranslation: true, usableWidth: 30, padLines: 2, lineMap, itemMap);
        Assert.True(ds.Count > 0);
        Assert.True(lineMap.ContainsKey(0));
        Assert.Equal(ds.Count, itemMap.Count);
    }

    [Fact]
    public void ParseLrc_EnhancedLrc_StripsAngleBracketsForPlainText()
    {
        var enhancedLrc = "[00:10.50]<00:10.50>Hello <00:11.00>world<00:11.80>";
        var parsed = LyricParser.ParseLrc(enhancedLrc);

        Assert.Single(parsed);
        Assert.Equal("Hello world", parsed[0].Text);
    }

    [Fact]
    public void ParseSingleLrc_EnhancedLrc_ParsesWordSpansCorrectly()
    {
        var enhancedLrc = "[00:10.50]<00:10.50>Hello <00:11.00>world<00:11.80>";
        var lines = LyricParser.ParseSingleLrc(enhancedLrc);

        Assert.Single(lines);
        Assert.Equal("Hello world", lines[0].Text);
        Assert.NotNull(lines[0].Words);
        Assert.Equal(2, lines[0].Words!.Count);

        Assert.Equal("Hello ", lines[0].Words![0].Word);
        Assert.Equal(0, lines[0].Words![0].OffsetMs);
        Assert.Equal(500, lines[0].Words![0].DurationMs);

        Assert.Equal("world", lines[0].Words![1].Word);
        Assert.Equal(500, lines[0].Words![1].OffsetMs);
        Assert.Equal(800, lines[0].Words![1].DurationMs);

        Assert.True(LyricParser.HasWordLevelSync(lines));
    }

    [Fact]
    public void ParseSingleLrc_EnhancedLrcWithTranslation_MergesCorrectly()
    {
        var enhancedLrc = """
            [00:10.50]<00:10.50>你好 <00:11.00>世界<00:11.80>
            [00:10.50]Hello world
            """;
        var lines = LyricParser.ParseSingleLrc(enhancedLrc);

        Assert.Single(lines);
        Assert.Equal("你好 世界", lines[0].Text);
        Assert.Equal("Hello world", lines[0].Trans);
        Assert.NotNull(lines[0].Words);
        Assert.Equal(2, lines[0].Words!.Count);
        Assert.True(LyricParser.HasWordLevelSync(lines));
    }

    [Fact]
    public void MergeLyrics_MetaStaffLinesWithEmptyTranslationPlaceholder_DoesNotMisalignSubsequentLyrics()
    {
        var rawLyric = """
            [00:01.05]谱务 Scoring：markmilian/auburn
            [00:01.13]出品 Produced by：测试工作室
            [00:01.28]I once wished to disappear
            """;
        var rawTrans = """
            [00:01.05]//
            [00:01.13]//
            [00:01.28]我以前许愿消失于世上
            """;

        var merged = LyricParser.MergeLyrics(rawLyric, rawTrans);

        Assert.Equal(3, merged.Count);
        Assert.Equal("谱务 Scoring：markmilian/auburn", merged[0].Text);
        Assert.Equal("", merged[0].Trans);

        Assert.Equal("出品 Produced by：测试工作室", merged[1].Text);
        Assert.Equal("", merged[1].Trans);

        Assert.Equal("I once wished to disappear", merged[2].Text);
        Assert.Equal("我以前许愿消失于世上", merged[2].Trans);
    }

    [Fact]
    public void MergeLyrics_QrcStaffLineWithNearbyTranslation_DoesNotStealNextLineTranslation()
    {
        var qrcXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <QrcInfos>
            <LyricInfo>
            <Lyric_1 LyricType="1" LyricContent="[1057,79]谱务 Scoring：markmilian/auburn&#10;[1137,147]出品 Produced by：测试工作室&#10;[1285,6492](1285,100)I (1385,100)once (1485,200)wished" />
            </LyricInfo>
            </QrcInfos>
            """;
        var rawTrans = """
            [00:01.05]//
            [00:01.13]//
            [00:01.28]我以前许愿消失于世上
            """;

        var merged = LyricParser.MergeLyrics(qrcXml, rawTrans);

        Assert.Equal(3, merged.Count);
        Assert.Equal("谱务 Scoring：markmilian/auburn", merged[0].Text);
        Assert.Equal("", merged[0].Trans);

        Assert.Equal("出品 Produced by：测试工作室", merged[1].Text);
        Assert.Equal("", merged[1].Trans);

        Assert.Equal("I once wished", merged[2].Text);
        Assert.Equal("我以前许愿消失于世上", merged[2].Trans);
    }
}


