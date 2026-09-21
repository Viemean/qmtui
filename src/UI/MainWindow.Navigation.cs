using Terminal.Gui.App;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    private async Task ExecuteSearchAsync()
    {
        if (_isSearching) return;
        _isSearching = true;

        try
        {
            _currentViewMode = ViewMode.Search;
            _isViewingPlaylistsList = false;
            _currentDrilldownPlaylist = null;
            _isViewingAlbumsList = false;
            _currentDrilldownAlbum = null;

            var text = _searchField.Text.ToString()?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _lastSearchQuery = text;
            _searchCurrentPage = 1;
            _hasMoreSearchResults = true;
            _isLoadingMore = false;

            _songListView.SetMessage("正在搜索...", $"正在搜索「{text}」...");

            var songs = await MusicApi.SearchAsync(text, 1, PageSize);

            Application.Invoke(() =>
            {
                if (songs.Count < PageSize)
                {
                    _hasMoreSearchResults = false;
                }

                var title = $"搜索结果: 共 {songs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
                _songListView.SetSongs(songs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                if (songs.Count > 0)
                {
                    _songListView.SetFocusToList();
                }
            });
        }
        catch (Exception ex)
        {
            QmTui.Utils.AppLogger.Error("Search", "ExecuteSearchAsync failed", ex);
        }
        finally
        {
            _isSearching = false;
        }
    }

    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchResults || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        _isLoadingMore = true;
        var nextPage = _searchCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"搜索结果: 共 {_songListView.Songs.Count} 首 (正在加载更多...)";
        });

        var moreSongs = await MusicApi.SearchAsync(_lastSearchQuery, nextPage, PageSize);

        Application.Invoke(() =>
        {
            if (moreSongs.Count > 0)
            {
                _searchCurrentPage = nextPage;
                var currentCount = _songListView.Songs.Count;

                if (moreSongs.Count < PageSize)
                {
                    _hasMoreSearchResults = false;
                }

                var title = $"搜索结果: 共 {currentCount + moreSongs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
                _songListView.AppendSongs(moreSongs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
            }
            else
            {
                _hasMoreSearchResults = false;
                _songListView.Title = $"搜索结果: 共 {_songListView.Songs.Count} 首 (已无更多结果)";
            }

            _isLoadingMore = false;
        });
    }

    private Task LoadDailyRecommendSongsAsync(bool forceRefresh = false) =>
        LoadDailyFeedRecommendPlaylistAsync(
            targetViewMode: ViewMode.DailyRecommend,
            featureName: "每日30首",
            statusTag: "每日推荐",
            loginPrompt: "请按 U 键登录后获取您的每日 30 首个性化推荐歌单",
            getCache: MetadataCacheService.GetDailyRecommend,
            saveCache: MetadataCacheService.SaveDailyRecommend,
            fetchApiAsync: MusicApi.GetDailyRecommendSongsAsync,
            forceRefresh: forceRefresh);

    private Task LoadMillionRecommendSongsAsync(bool forceRefresh = false) =>
        LoadDailyFeedRecommendPlaylistAsync(
            targetViewMode: ViewMode.MillionRecommend,
            featureName: "百万收藏",
            statusTag: "百万收藏",
            loginPrompt: "请按 U 键登录后获取您的官方百万收藏精选歌单",
            getCache: MetadataCacheService.GetMillionRecommend,
            saveCache: MetadataCacheService.SaveMillionRecommend,
            fetchApiAsync: MusicApi.GetMillionRecommendSongsAsync,
            forceRefresh: forceRefresh);

    private async Task LoadDailyFeedRecommendPlaylistAsync(
        ViewMode targetViewMode,
        string featureName,
        string statusTag,
        string loginPrompt,
        Func<string, string, DailyRecommendCache?> getCache,
        Action<string, string, List<Song>> saveCache,
        Func<CancellationToken, Task<List<Song>>> fetchApiAsync,
        bool forceRefresh = false)
    {
        _currentViewMode = targetViewMode;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage(loginPrompt, $"{featureName} (未登录)");
            });
            return;
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var uin = UserSession.Current.Uin;

        if (!forceRefresh)
        {
            var cached = getCache(uin, today);
            if (cached != null && cached.Songs.Count > 0)
            {
                var cachedSongs = cached.Songs;
                Application.Invoke(() =>
                {
                    _songListView.SetSongs(cachedSongs, $"{featureName}: 今日精选 {cachedSongs.Count} 首");
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                        var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                                    (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                        _controlBar.SetFavoriteStatus(isFav);
                    }
                    _songListView.SetFocusToList();
                    _controlBar.UpdateStatus($"[{statusTag}] 今日 {featureName} 已从本地缓存载入（共 {cachedSongs.Count} 首）");
                });
                return;
            }
        }

        _songListView.SetMessage($"正在同步今日推荐歌单（{featureName}）...", $"{featureName} (加载中)");
        _controlBar.UpdateStatus($"[正在加载] 正在请求{featureName}推荐曲目...");

        var songs = await fetchApiAsync(CancellationToken.None);

        if (songs.Count > 0)
        {
            saveCache(uin, today, songs);
        }

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("今日推荐歌单获取为空，请按 U 检查登录状态或稍后重试", $"{featureName}: 0 首");
                _controlBar.UpdateStatus("[加载提示] 未能获取到今日推荐歌单数据");
                return;
            }

            _songListView.SetSongs(songs, $"{featureName}: 今日精选 {songs.Count} 首");
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
                var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                            (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                _controlBar.SetFavoriteStatus(isFav);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[{statusTag}] 今日 {featureName} 已成功载入（共 {songs.Count} 首）");
        });
    }

    private async Task LoadRecentPlaySongsAsync()
    {
        _currentViewMode = ViewMode.RecentPlay;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var songs = RecentPlayHistory.GetSongs();
        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("暂无最近播放记录，快去点播一首歌曲吧！", "最近播放 (0 首)");
                return;
            }

            _songListView.SetSongs(songs, $"最近播放: 共 {songs.Count} 首 (按 D 移除历史)");
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
        });
        _controlBar.UpdateStatus($"[最近播放] 已加载本地播放轨迹共 {songs.Count} 首");
    }

    private async Task LoadPlaylistsAsync()
    {
        _currentViewMode = ViewMode.PlaylistsList;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = true;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后同步您的云端歌单列表", "我的歌单 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步云端歌单列表...", "我的歌单 (加载中)");

        var allPlaylists = await MusicApi.GetPlaylistsAsync();
        var playlists = allPlaylists.Where(p => !p.IsMyFavorite).ToList();
        _cachedPlaylists = playlists;

        Application.Invoke(() =>
        {
            if (playlists.Count == 0)
            {
                _songListView.SetMessage("当前账号暂无自建或收藏歌单数据", "我的歌单: 0 个 (按 N 新建歌单)");
                return;
            }

            var items = new List<string>(playlists.Count);
            for (int i = 0; i < playlists.Count; i++)
            {
                var p = playlists[i];
                var typeStr = p.IsCreated ? "[自建]" : "[收藏]";
                items.Add($"{(i + 1):D2}  {typeStr,-6}  {p.Title}  (共 {p.SongNum} 首)");
            }

            var headerTitle = $"我的歌单: 共 {playlists.Count} 个 (按 N 新建歌单 / 按 D 删除歌单 / 按 Esc 退回)";
            _songListView.SetCustomItems(items, headerTitle, async (idx) =>
            {
                if (idx >= 0 && idx < _cachedPlaylists.Count)
                {
                    await DrilldownPlaylistAsync(_cachedPlaylists[idx]);
                }
            });
            _songListView.SetFocusToList();
        });
    }

    private async Task DrilldownPlaylistAsync(Playlist playlist)
    {
        _currentViewMode = ViewMode.PlaylistDrilldown;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = playlist;
        _playlistCurrentPage = 1;
        _hasMorePlaylistSongs = false;
        _isLoadingMorePlaylistSongs = false;

        _songListView.SetMessage($"正在加载歌单「{playlist.Title}」歌曲...", $"歌单: {playlist.Title} (加载中)");

        var songs = await MusicApi.GetPlaylistSongsAsync(playlist, 1, PlaylistPageSize);

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("该歌单暂无歌曲或已清空", $"歌单: {playlist.Title} (共 0 首，按 Esc 退回)");
                return;
            }

            _hasMorePlaylistSongs = songs.Count >= PlaylistPageSize;
            var title = $"歌单: {playlist.Title} (共 {songs.Count} 首" + (_hasMorePlaylistSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 退回)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
        });
    }

    private async Task LoadMorePlaylistSongsAsync()
    {
        if (_isLoadingMorePlaylistSongs || !_hasMorePlaylistSongs || _currentDrilldownPlaylist == null) return;

        _isLoadingMorePlaylistSongs = true;
        var nextPage = _playlistCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"歌单: {_currentDrilldownPlaylist.Title} (正在加载更多...)";
            _controlBar.UpdateStatus($"[正在加载] 正在获取「{_currentDrilldownPlaylist.Title}」更多曲目 (第 {nextPage} 页)...");
        });

        try
        {
            var moreSongs = await MusicApi.GetPlaylistSongsAsync(_currentDrilldownPlaylist, nextPage, PlaylistPageSize);

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    _playlistCurrentPage = nextPage;
                    var currentCount = _songListView.Songs.Count;
                    if (moreSongs.Count < PlaylistPageSize)
                    {
                        _hasMorePlaylistSongs = false;
                    }

                    var title = $"歌单: {_currentDrilldownPlaylist.Title} (共 {currentCount + moreSongs.Count} 首" + (_hasMorePlaylistSongs ? "，向下滚动加载更多" : "，已全部加载") + "，按 Esc 退回)";
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    _controlBar.UpdateStatus($"[加载完成] 歌单已载入 {currentCount + moreSongs.Count} 首");
                }
                else
                {
                    _hasMorePlaylistSongs = false;
                    _songListView.Title = $"歌单: {_currentDrilldownPlaylist.Title} (共 {_songListView.Songs.Count} 首，已全部加载，按 Esc 退回)";
                    _controlBar.UpdateStatus($"[已全部加载] 歌单共 {_songListView.Songs.Count} 首曲目");
                }
            });
        }
        catch (Exception ex)
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[加载失败] 获取更多曲目异常: {ex.Message}");
            });
        }
        finally
        {
            _isLoadingMorePlaylistSongs = false;
        }
    }

    private async Task LoadFavoriteAlbumsAsync()
    {
        _currentViewMode = ViewMode.FavoriteAlbums;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = true;
        _currentDrilldownAlbum = null;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后同步您的云端收藏专辑", "收藏专辑 (未登录)");
            });
            return;
        }

        _songListView.SetMessage("正在同步云端收藏专辑列表...", "收藏专辑 (加载中)");
        _controlBar.UpdateStatus("[正在加载] 正在获取收藏专辑列表...");

        var albums = await MusicApi.GetFavoriteAlbumsAsync();
        _cachedAlbums = albums;

        Application.Invoke(() =>
        {
            if (albums.Count == 0)
            {
                _songListView.SetMessage("当前账号暂无收藏专辑数据", "收藏专辑: 0 张");
                _controlBar.UpdateStatus("[加载提示] 暂无收藏专辑，快去探索发现好音乐吧！");
                return;
            }

            var items = new List<string>(albums.Count);
            for (int i = 0; i < albums.Count; i++)
            {
                var a = albums[i];
                items.Add($"{(i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
            }

            _songListView.SetCustomItems(items, $"收藏专辑: 共 {albums.Count} 张 (按 Enter 进入专辑，按 D 取消收藏)", async (idx) =>
            {
                if (idx >= 0 && idx < _cachedAlbums.Count)
                {
                    await DrilldownAlbumAsync(_cachedAlbums[idx]);
                }
            }, (selectedIdx) =>
            {
                if (selectedIdx >= 0 && selectedIdx < _cachedAlbums.Count)
                {
                    _ = PreviewAlbumDetailAsync(_cachedAlbums[selectedIdx].Mid, _cachedAlbums[selectedIdx].Title, _cachedAlbums[selectedIdx].Artist);
                }
            });

            if (albums.Count > 0)
            {
                _ = PreviewAlbumDetailAsync(albums[0].Mid, albums[0].Title, albums[0].Artist);
            }

            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[同步完成] 已载入收藏专辑共 {albums.Count} 张");
        });
    }

    private async Task DrilldownAlbumAsync(Album album)
    {
        _currentViewMode = ViewMode.AlbumDrilldown;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = album;

        _songListView.SetMessage($"正在加载专辑「{album.Title}」歌曲...", $"专辑: {album.Title} (加载中)");
        _controlBar.UpdateStatus($"[正在加载] 正在获取专辑「{album.Title}」曲目...");

        var songs = await MusicApi.GetAlbumSongsAsync(album.Mid);

        Application.Invoke(() =>
        {
            if (songs.Count == 0)
            {
                _songListView.SetMessage("该专辑暂无可用歌曲", $"专辑: {album.Title} (共 0 首，按 Esc 退回)");
                _controlBar.UpdateStatus($"[加载完成] 专辑「{album.Title}」无可用曲目");
                return;
            }

            var title = $"专辑: {album.Title} - {album.Artist} (共 {songs.Count} 首，按 Esc 退回)";
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[加载完成] 专辑「{album.Title}」共载入 {songs.Count} 首曲目");
        });
    }

    /// <summary>
    /// 普通歌单列表：播放下一首（基于独立播放队列推演）
    /// </summary>
    private async Task PlayNextInCurrentListAsync(bool isAutoPlayback = false)
    {
        var queue = PlaybackQueueService.Instance;
        queue.Mode = _currentPlaybackMode;
        var nextSong = queue.GetNextSong(isAutoPlayback);
        if (nextSong != null)
        {
            await PlaySongAsync(nextSong);
        }
    }

    /// <summary>
    /// 普通歌单列表：播放上一首（基于独立播放队列推演）
    /// </summary>
    private async Task PlayPrevInCurrentListAsync()
    {
        var queue = PlaybackQueueService.Instance;
        queue.Mode = _currentPlaybackMode;
        var prevSong = queue.GetPrevSong();
        if (prevSong != null)
        {
            await PlaySongAsync(prevSong);
        }
    }
}
