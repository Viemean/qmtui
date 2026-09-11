using System.Threading.Tasks;
using Terminal.Gui.App;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    /// <summary>
    /// 进入“猜你喜欢”视窗：若当前已经在播放电台流，则平滑恢复电台卡片展示而不打断播放；否则启动全新电台流
    /// </summary>
    private async Task ResumeOrStartGuessRadioAsync()
    {
        _currentViewMode = ViewMode.GuessRecommend;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后体验“猜你喜欢”个性化音乐电台", "猜你喜欢 (未登录)");
                _controlBar.UpdateStatus("[猜你喜欢] 请先按 U 登录账号以获取个性化推荐");
            });
            return;
        }

        // 如果电台队列已就绪，且当前播放曲目属于电台队列，直接恢复电台并打开播放界面
        if (_radioService.IsCurrentSongInRadio(_activeSong))
        {
            Application.Invoke(() =>
            {
                _songListView.SetRadioCard(_activeSong!, AudioQualityHelper.GetBadge(_actualQualityTier), _radioService.PlayedCount);
                OpenNowPlayingView();
            });
            return;
        }

        // 否则重新启动电台流
        await StartGuessRadioAsync();
    }

    /// <summary>
    /// 启动“猜你喜欢”个性化音乐电台
    /// </summary>
    private async Task StartGuessRadioAsync()
    {
        _currentViewMode = ViewMode.GuessRecommend;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后体验“猜你喜欢”个性化音乐电台", "猜你喜欢 (未登录)");
                _controlBar.UpdateStatus("[猜你喜欢] 请先按 U 登录账号以获取个性化推荐");
            });
            return;
        }

        Application.Invoke(() =>
        {
            _songListView.SetMessage("正在根据您的音乐品味连接个性化电台...", "猜你喜欢 (连接中)");
            _controlBar.UpdateStatus("[个性电台] 正在连接个性化推荐流...");
        });

        var firstSong = await _radioService.StartRadioAsync(5);
        if (firstSong == null)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("未能获取到电台推荐歌曲，请按 U 检查登录状态或按 R 重试", "猜你喜欢: 0 首");
                _controlBar.UpdateStatus("[电台提示] 未能获取到推荐曲目，可按 R 重新连接");
            });
            return;
        }

        // 播放首曲并打开播放界面
        await PlaySongAsync(firstSong);
        Application.Invoke(() =>
        {
            _songListView.SetRadioCard(firstSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioService.PlayedCount);
            _controlBar.UpdateStatus($"[电台启播] 猜你喜欢第 01 首: 《{firstSong.Title}》 - {firstSong.Artist}");
            OpenNowPlayingView();
        });
    }

    /// <summary>
    /// 电台模式：跳至下一首（单曲播完或按 ] / N / D 触发）
    /// </summary>
    private async Task PlayNextRadioTrackAsync()
    {
        var nextSong = await _radioService.GetNextRadioTrackAsync();
        if (nextSong == null)
        {
            await StartGuessRadioAsync();
            return;
        }

        await PlaySongAsync(nextSong);
        Application.Invoke(() =>
        {
            _songListView.SetRadioCard(nextSong, AudioQualityHelper.GetBadge(_actualQualityTier), _radioService.PlayedCount);
            _controlBar.UpdateStatus($"[电台切歌] 猜你喜欢第 {_radioService.PlayedCount:D2} 首: 《{nextSong.Title}》 - {nextSong.Artist}");
        });
    }
}
