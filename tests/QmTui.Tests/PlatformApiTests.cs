using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using Xunit;
using Xunit.Abstractions;

namespace QmTui.Tests;

/// <summary>
/// 平台 OpenAPI 联调与维护排查测试用例集
/// 专用于维护期排查协议变动、网关状态、登录鉴权、推荐算法及听歌识曲服务可用性
/// 默认离线模式自动跳过，排查时可通过环境变量 QMTUI_ONLINE_TEST=1 激活：
/// env QMTUI_ONLINE_TEST=1 dotnet test tests/QmTui.Tests/QmTui.Tests.csproj --filter "Category=PlatformApi"
/// </summary>
[Trait("Category", "PlatformApi")]
public class PlatformApiTests
{
    private readonly ITestOutputHelper _output;

    private const string JayChouSingerMid = "0025NhlN2yWrP4"; // 周杰伦
    private const string QingtianSongMid = "0039MnYb0qxYhV";   // 晴天
    private const string YehuiMeiAlbumMid = "000MkMni19ClKG";  // 叶惠美

    private static bool s_sessionInitialized = false;
    private static readonly object s_lock = new();

    public PlatformApiTests(ITestOutputHelper output)
    {
        _output = output;
        EnsureSessionAndConfigInitialized();
    }

    private static bool IsOnlineTestEnabled()
    {
        var env = Environment.GetEnvironmentVariable("QMTUI_ONLINE_TEST");
        return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 初始化测试运行凭证：
    /// 1. 优先读取环境变量 QMTUI_TEST_COOKIE / QMTUI_TEST_UIN；
    /// 2. 若配置 QMTUI_USE_LOCAL_SESSION=1，读取本地会话文件 (~/.config/qmtui/session.json)；
    /// 3. 未配置上述变量时，使用游客模式。
    /// </summary>
    private void EnsureSessionAndConfigInitialized()
    {
        lock (s_lock)
        {
            if (s_sessionInitialized) return;
            s_sessionInitialized = true;

            var envCookie = Environment.GetEnvironmentVariable("QMTUI_TEST_COOKIE")
                            ?? Environment.GetEnvironmentVariable("QMTUI_COOKIE");
            var envUin = Environment.GetEnvironmentVariable("QMTUI_TEST_UIN")
                         ?? Environment.GetEnvironmentVariable("QMTUI_UIN");
            var allowLocalSession = string.Equals(Environment.GetEnvironmentVariable("QMTUI_USE_LOCAL_SESSION"), "1", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(Environment.GetEnvironmentVariable("QMTUI_USE_LOCAL_SESSION"), "true", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(envCookie))
            {
                var cookieDict = ParseCookieStringToDictionary(envCookie);
                UserSession.Current.Cookies = cookieDict;
                if (!string.IsNullOrWhiteSpace(envUin))
                {
                    UserSession.Current.Uin = envUin;
                }
                else if (cookieDict.TryGetValue("uin", out var u) || cookieDict.TryGetValue("qqmusic_uin", out u))
                {
                    UserSession.Current.Uin = u.TrimStart('o');
                }
                _output.WriteLine($"[鉴权初始化] 从环境变量加载 Cookie，Uin: {MaskIdentifier(UserSession.Current.Uin)}");
            }
            else if (allowLocalSession)
            {
                // 读取本地会话
                UserSession.Load();
                if (UserSession.Current.IsLoggedIn)
                {
                    _output.WriteLine($"[鉴权初始化] 加载本地会话，Uin: {MaskIdentifier(UserSession.Current.Uin)}, 昵称: {UserSession.Current.Nick}, VIP: {UserSession.Current.IsVip}");
                }
                else
                {
                    _output.WriteLine("[鉴权初始化] 本地无有效凭据，以游客模式运行");
                }
            }
            else
            {
                _output.WriteLine("[鉴权初始化] 未配置凭据与本地授权环境变量，以游客模式运行");
            }
        }
    }

    private static string MaskIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length <= 4) return "***";
        return string.Concat(raw.AsSpan(0, 3), "****", raw.AsSpan(raw.Length - 2));
    }

    private static Dictionary<string, string> ParseCookieStringToDictionary(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
            {
                var key = part[..idx].Trim();
                var val = part[(idx + 1)..].Trim();
                dict[key] = val;
            }
        }
        return dict;
    }

    #region 1. 基础模块 - 搜索

    [Fact]
    public async Task SearchAsync_OfficialApi_ReturnsValidSongList()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 基础搜索] 正在测试 MusicApi.SearchAsync (关键词: '周杰伦 晴天')...");
        var songs = await MusicApi.SearchAsync("周杰伦 晴天", page: 1, pageSize: 10);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);

        var firstSong = songs[0];
        _output.WriteLine($"[模块: 基础搜索] 成功拉取 {songs.Count} 首结果，首曲: '{firstSong.Title}' - '{firstSong.Artist}' (Mid: {firstSong.Mid}, 时长: {firstSong.FormattedDuration})");

        Assert.False(string.IsNullOrWhiteSpace(firstSong.Mid));
        Assert.Contains("晴天", firstSong.Title);
        Assert.Contains("周杰伦", firstSong.Artist);
        Assert.True(firstSong.Duration > 0);
    }

    #endregion

    #region 2. 播放模块 - 音质与播放流

    [Fact]
    public async Task ProbeSongQualitiesAsync_OfficialApi_DetectsAvailableTiers()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 播放探测] 正在测试 MusicApi.ProbeSongQualitiesAsync (SongMid: {QingtianSongMid})...");
        var options = await MusicApi.ProbeSongQualitiesAsync(QingtianSongMid);

        Assert.NotNull(options);
        Assert.NotEmpty(options);
        foreach (var opt in options)
        {
            _output.WriteLine($"[模块: 播放探测] 检测到音质: {opt.Name} ({opt.Badge}), 档位: {opt.Tier}, 规格: {opt.Spec}, 可用: {opt.Available}");
        }

        Assert.Contains(options, opt => opt.Tier == AudioQualityTier.Standard || opt.Tier == AudioQualityTier.HQ || opt.Tier == AudioQualityTier.SQ);
    }

    [Fact]
    public async Task GetPlayUrlForTierAsync_OfficialApi_ResolvesPlayableStream()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 播放地址] 正在测试 MusicApi.GetPlayUrlForTierAsync (SongMid: {QingtianSongMid})...");
        var (url, quality, tier) = await MusicApi.GetPlayUrlForTierAsync(QingtianSongMid);

        _output.WriteLine($"[模块: 播放地址] 接口解析结果 -> 档位: {tier}, 描述: {quality}, 播放流 URL: {(string.IsNullOrEmpty(url) ? "(受版权限制或需VIP)" : url)}");
        Assert.NotNull(quality);
        if (!string.IsNullOrEmpty(url))
        {
            Assert.StartsWith("http", url, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region 3. 歌词模块 - 同步歌词与时间轴

    [Fact]
    public async Task GetLyricsAsync_OfficialApi_ParsesTimestampsAndText()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌词同步] 正在测试 MusicApi.GetLyricsAsync (SongMid: {QingtianSongMid})...");
        var lyrics = await MusicApi.GetLyricsAsync(QingtianSongMid);

        Assert.NotNull(lyrics);
        Assert.NotEmpty(lyrics);

        var firstLine = lyrics.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Text));
        _output.WriteLine($"[模块: 歌词同步] 成功解析歌词行数: {lyrics.Count}, 首句时间轴: {firstLine?.Timestamp} => '{firstLine?.Text}'");

        Assert.Contains(lyrics, line => line.Text.Contains("故事的小黄花") || line.Text.Contains("晴天"));
    }

    #endregion

    #region 4. 用户交互模块 - 歌曲收藏与取消收藏闭环

    [Fact]
    public async Task FavoriteSongLifecycle_OfficialApi_AddsAndRemovesFavorite()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 歌曲收藏] 正在检查当前用户登录状态...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 歌曲收藏] 当前未处于登录态，跳过收藏/取消收藏网络测试");
            return;
        }

        // 先通过搜索获取标准歌曲对象
        var searchResults = await MusicApi.SearchAsync("周杰伦 晴天", 1, 1);
        Assert.NotEmpty(searchResults);
        var testSong = searchResults[0];

        _output.WriteLine($"[模块: 歌曲收藏] 1. 执行添加收藏 -> '{testSong.Title}' (Id: {testSong.Id}, Mid: {testSong.Mid})...");
        bool added = false;
        try
        {
            var addResult = await MusicApi.AddSongToFavoriteAsync(testSong);
            _output.WriteLine($"[模块: 歌曲收藏] 添加收藏结果: {addResult}");
            Assert.True(addResult, "添加歌曲到收藏夹应返回成功");
            added = true;
        }
        finally
        {
            if (added)
            {
                // 确保即使断言失败也必定执行清理还原，绝不污染用户收藏夹
                await Task.Delay(500);
                _output.WriteLine($"[模块: 歌曲收藏] 2. 执行取消收藏 (异常安全清理) -> '{testSong.Title}'...");
                var removeResult = await MusicApi.RemoveSongFromFavoriteAsync(testSong);
                _output.WriteLine($"[模块: 歌曲收藏] 取消收藏结果: {removeResult}");
                Assert.True(removeResult, "从收藏夹移除歌曲应返回成功");
            }
        }
    }

    #endregion

    #region 5. 个性化推荐模块 - 每日推荐与猜你喜欢

    [Fact]
    public async Task GetDailyRecommendSongsAsync_OfficialApi_ReturnsDailyPlaylist()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 个性化推荐] 正在测试 MusicApi.GetDailyRecommendSongsAsync (每日30首)...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 个性化推荐] 当前未登录，验证未登录状态安全防护");
            var guestList = await MusicApi.GetDailyRecommendSongsAsync();
            Assert.Empty(guestList);
            return;
        }

        var dailySongs = await MusicApi.GetDailyRecommendSongsAsync();
        _output.WriteLine($"[模块: 个性化推荐] 成功拉取今日每日推荐曲目数: {dailySongs.Count}");
        Assert.NotNull(dailySongs);
        Assert.NotEmpty(dailySongs);
        var sample = dailySongs[0];
        _output.WriteLine($"[模块: 个性化推荐] 每日推荐示例曲目: '{sample.Title}' - '{sample.Artist}'");
        Assert.False(string.IsNullOrWhiteSpace(sample.Mid));
    }

    [Fact]
    public async Task GetGuessRecommendSongsAsync_OfficialApi_ReturnsRadioTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine("[模块: 个性化推荐] 正在测试 MusicApi.GetGuessRecommendSongsAsync (猜你喜欢电台)...");
        if (!UserSession.Current.IsLoggedIn)
        {
            _output.WriteLine("[模块: 个性化推荐] 当前未登录，验证未登录状态安全防护");
            var guestList = await MusicApi.GetGuessRecommendSongsAsync(count: 10);
            Assert.Empty(guestList);
            return;
        }

        var guessSongs = await MusicApi.GetGuessRecommendSongsAsync(count: 10);
        _output.WriteLine($"[模块: 个性化推荐] 成功拉取猜你喜欢曲目数: {guessSongs.Count}");
        Assert.NotNull(guessSongs);
        Assert.NotEmpty(guessSongs);
        var sample = guessSongs[0];
        _output.WriteLine($"[模块: 个性化推荐] 猜你喜欢示例曲目: '{sample.Title}' - '{sample.Artist}'");
        Assert.False(string.IsNullOrWhiteSpace(sample.Mid));
    }

    #endregion



    #region 7. 元数据与归档模块 - 歌手与专辑详情

    [Fact]
    public async Task GetSingerDetailAsync_OfficialApi_FetchesArtistInfo()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌手元数据] 正在测试 MusicApi.GetSingerDetailAsync (SingerMid: {JayChouSingerMid})...");
        var detail = await MusicApi.GetSingerDetailAsync(JayChouSingerMid, singerName: "周杰伦");

        Assert.NotNull(detail);
        _output.WriteLine($"[模块: 歌手元数据] 歌手名称: {detail.Name}, 曲目数: {detail.Songs.Count}, 简介长度: {detail.Brief.Length} 字符");

        Assert.Equal("周杰伦", detail.Name);
        Assert.NotEmpty(detail.Songs);
        Assert.Contains(detail.Songs, s => s.Artist.Contains("周杰伦"));
    }

    [Fact]
    public async Task GetSingerSongListAsync_OfficialApi_ReturnsTopTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 歌手曲目] 正在测试 MusicApi.GetSingerSongListAsync (SingerMid: {JayChouSingerMid})...");
        var (songs, total) = await MusicApi.GetSingerSongListAsync(JayChouSingerMid, begin: 0, pageSize: 20);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);
        Assert.True(total > 0);
        _output.WriteLine($"[模块: 歌手曲目] 周杰伦已发行歌曲总数: {total}, 本页拉取: {songs.Count}");

        Assert.All(songs, song => Assert.False(string.IsNullOrEmpty(song.Mid)));
    }

    [Fact]
    public async Task GetAlbumDetailInfoAsync_OfficialApi_FetchesAlbumMetadata()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 专辑元数据] 正在测试 MusicApi.GetAlbumDetailInfoAsync (AlbumMid: {YehuiMeiAlbumMid})...");
        var album = await MusicApi.GetAlbumDetailInfoAsync(YehuiMeiAlbumMid);

        Assert.NotNull(album);
        _output.WriteLine($"[模块: 专辑元数据] 专辑名: '{album.Name}', 发行日期: '{album.PublishDate}', 唱片公司: '{album.Company}'");

        Assert.Equal(YehuiMeiAlbumMid, album.Mid);
        Assert.Contains("叶惠美", album.Name);
        Assert.Contains("周杰伦", album.Artist);
        Assert.NotEmpty(album.Songs);
    }

    [Fact]
    public async Task GetAlbumSongsAsync_OfficialApi_FetchesAlbumTracks()
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 专辑曲目] 正在测试 MusicApi.GetAlbumSongsAsync (AlbumMid: {YehuiMeiAlbumMid})...");
        var songs = await MusicApi.GetAlbumSongsAsync(YehuiMeiAlbumMid);

        Assert.NotNull(songs);
        Assert.NotEmpty(songs);
        _output.WriteLine($"[模块: 专辑曲目] 成功拉取专辑《叶惠美》曲目数: {songs.Count}, 包含: {string.Join(", ", songs.Take(3).Select(s => s.Title))} 等");

        Assert.Contains(songs, s => s.Title.Contains("以父之名") || s.Title.Contains("晴天") || s.Title.Contains("东风破"));
    }

    #endregion

    #region 8. 账号网关模块 - 二维码登录

    [Theory]
    [InlineData(LoginService.QrLoginType.Qq, "image/png")]
    [InlineData(LoginService.QrLoginType.WeChat, "image/jpeg")]
    [InlineData(LoginService.QrLoginType.OfficialApp, "image/png")]
    public async Task FetchQrCodeAsync_OfficialGateways_ReturnValidQrData(LoginService.QrLoginType type, string mimeType)
    {
        if (!IsOnlineTestEnabled()) return;

        _output.WriteLine($"[模块: 登录网关] 正在获取 {LoginService.GetLoginTypeName(type)} 登录二维码...");
        var qr = await LoginService.FetchQrCodeAsync(type);

        Assert.NotNull(qr);
        Assert.NotEmpty(qr.ImageBytes);
        Assert.Equal(mimeType, qr.MimeType);
        Assert.Equal(type, qr.Type);
        Assert.False(string.IsNullOrEmpty(qr.Identifier));
        Assert.DoesNotContain(qr.AsciiLines, line => line.StartsWith("二维码渲染失败", StringComparison.Ordinal));

        _output.WriteLine($"[模块: 登录网关] {LoginService.GetLoginTypeName(type)} 二维码大小: {qr.ImageBytes.Length} bytes");
    }

    #endregion

    #region 9. 歌单管理模块 - 创建与删除歌单

    [Fact]
    public async Task CreatePlaylistAsync_WhenNotLoggedInOrEmpty_ReturnsFalse()
    {
        var (ok1, _, _) = await MusicApi.CreatePlaylistAsync("");
        Assert.False(ok1);

        var (ok2, _, _) = await MusicApi.CreatePlaylistAsync("   ");
        Assert.False(ok2);
    }

    [Fact]
    public async Task DeletePlaylistAsync_WhenMyFavorite_ReturnsFalse()
    {
        var myFav = new Playlist(201, "我喜欢", 10, 0, IsFav: false);
        var result = await MusicApi.DeletePlaylistAsync(myFav);
        Assert.False(result);
    }

    [Fact]
    public async Task CreateAndDeletePlaylistAsync_WhenOnline_Succeeds()
    {
        if (!IsOnlineTestEnabled()) return;
        var (ok, dissId, msg) = await MusicApi.CreatePlaylistAsync("qmtui自动化测试歌单");
        Assert.True(ok, msg);
        Assert.True(dissId > 0);
        _output.WriteLine($"创建歌单成功: dissId={dissId}");

        var playlist = new Playlist(dissId, "qmtui自动化测试歌单", 0, dissId, IsFav: false);
        // 测试添加歌曲 (周杰伦-晴天, id: 97773, mid: 0039MnYb0qxYaq)
        var addSongOk = await MusicApi.AddSongToPlaylistAsync(dissId, 97773);
        _output.WriteLine($"添加歌曲到歌单结果: {addSongOk}");
        Assert.True(addSongOk);

        var delOk = await MusicApi.DeletePlaylistAsync(playlist);
        Assert.True(delOk);
        _output.WriteLine($"删除歌单成功: dissId={dissId}");
    }

    [Fact]
    public async Task GetSingerDetailAsync_WhenMidEmpty_ResolvesArtistByNameAndReturnsDetail()
    {
        if (!IsOnlineTestEnabled()) return;

        var (mid, id) = await MusicApi.ResolveArtistAsync("Void");
        Assert.False(string.IsNullOrWhiteSpace(mid));
        Assert.True(id > 0);

        var detail = await MusicApi.GetSingerDetailAsync("", 0, "Void");
        Assert.NotNull(detail);
        Assert.Equal(mid, detail.Mid);
        Assert.True(detail.Songs.Count > 0);
    }

    #endregion
}
