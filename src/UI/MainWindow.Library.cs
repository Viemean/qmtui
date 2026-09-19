using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _favoriteSyncCts;

    private async Task LoadFavoriteSongsAsync(bool forceRefresh = false)
    {
        _favoriteSyncCts?.Cancel();
        _favoriteSyncCts?.Dispose();
        _favoriteSyncCts = new CancellationTokenSource();
        var ct = _favoriteSyncCts.Token;

        _currentViewMode = ViewMode.Favorite;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;
        _favoriteCurrentPage = 1;
        _favoriteTotalCount = 0;
        _hasMoreFavorites = false;
        _isLoadingMoreFavorites = false;

        if (!UserSession.Current.IsLoggedIn)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("请按 U 键登录后同步您的“我的喜欢”收藏歌单", "我的喜欢 (未登录)");
            });
            return;
        }

        var uin = UserSession.Current.Uin;
        var cached = forceRefresh ? null : MetadataCacheService.GetFavoriteCache(uin);

        if (cached != null && cached.Songs.Count > 0)
        {
            _favoriteTotalCount = cached.TotalCount;
            _hasMoreFavorites = false;

            lock (_favoriteSongMids)
            {
                foreach (var s in cached.Songs)
                {
                    if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                    if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                }
            }

            var title = $"我的喜欢: 共 {cached.Songs.Count} 首 (按 G 查找)";
            _songListView.SetSongs(cached.Songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
                var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                            (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                _controlBar.SetFavoriteStatus(isFav);
            }
            _songListView.SetFocusToList();
            _controlBar.UpdateStatus($"[快照秒开] 已载入本地喜欢列表（共 {cached.Songs.Count} 首，正在检查更新...）");
        }
        else
        {
            _songListView.SetMessage("正在同步云端“我的喜欢”收藏歌曲...", "我的喜欢 (加载中)");
            _controlBar.UpdateStatus("[正在加载] 正在请求“我的喜欢”收藏歌曲列表...");
        }

        // 后台异步轻量比对与静默对齐
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await MusicApi.GetFavoriteSongsAsync(1, FavoritePageSize, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _currentViewMode != ViewMode.Favorite) return;

                // 若有缓存，比对总数和首曲指纹是否完全一致
                if (cached != null && cached.Songs.Count > 0)
                {
                    bool isConsistent = (result.Total == cached.TotalCount) &&
                        (result.Songs.Count == 0 || result.Songs[0].Mid == cached.FirstSongMid);

                    if (isConsistent)
                    {
                        Application.Invoke(() =>
                        {
                            if (_currentViewMode == ViewMode.Favorite)
                            {
                                _songListView.Title = $"我的喜欢: 共 {cached.Songs.Count} 首 (已是最新，按 G 查找)";
                                _controlBar.UpdateStatus($"[同步完成] 我的喜欢已是最新状态（共 {cached.Songs.Count} 首）");
                            }
                        });
                        return;
                    }
                }

                // 首次无缓存，或比对发现有增删/排序变动：执行静默全量同步
                var allSongs = new List<Song>(result.Total > 0 ? result.Total : result.Songs.Count);
                allSongs.AddRange(result.Songs);

                if (cached == null && result.Songs.Count > 0)
                {
                    Application.Invoke(() =>
                    {
                        if (_currentViewMode == ViewMode.Favorite)
                        {
                            lock (_favoriteSongMids)
                            {
                                foreach (var s in result.Songs)
                                {
                                    if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                                    if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                                }
                            }
                            _songListView.SetSongs(result.Songs, $"我的喜欢: 已载入 {result.Songs.Count}/{result.Total} 首 (正在同步更多...)");
                            if (_activeSong != null) _songListView.SetPlayingSong(_activeSong.Mid);
                            _songListView.SetFocusToList();
                        }
                    });
                }

                int totalPages = result.Total > 0 ? (result.Total + FavoritePageSize - 1) / FavoritePageSize : 1;
                for (int page = 2; page <= totalPages; page++)
                {
                    if (ct.IsCancellationRequested || _currentViewMode != ViewMode.Favorite) return;
                    var pageResult = await MusicApi.GetFavoriteSongsAsync(page, FavoritePageSize, ct).ConfigureAwait(false);
                    if (pageResult.Songs.Count == 0) break;
                    allSongs.AddRange(pageResult.Songs);

                    var currentCount = allSongs.Count;
                    Application.Invoke(() =>
                    {
                        if (_currentViewMode == ViewMode.Favorite)
                        {
                            _songListView.Title = $"我的喜欢: 正在同步更多 ({currentCount}/{result.Total} 首)...";
                            _controlBar.UpdateStatus($"[后台同步] 正在载入我的喜欢更多曲目 ({currentCount}/{result.Total} 首)...");
                        }
                    });
                }

                if (ct.IsCancellationRequested || _currentViewMode != ViewMode.Favorite) return;

                var firstMid = allSongs.Count > 0 ? allSongs[0].Mid : "";
                MetadataCacheService.SaveFavoriteCache(uin, allSongs.Count, firstMid, allSongs);

                Application.Invoke(() =>
                {
                    if (_currentViewMode == ViewMode.Favorite)
                    {
                        lock (_favoriteSongMids)
                        {
                            foreach (var s in allSongs)
                            {
                                if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                                if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                            }
                        }
                        _songListView.SetSongs(allSongs, $"我的喜欢: 共 {allSongs.Count} 首 (已同步最新，按 G 查找)");
                        if (_activeSong != null)
                        {
                            _songListView.SetPlayingSong(_activeSong.Mid);
                            var isFav = (!string.IsNullOrEmpty(_activeSong.Mid) && _favoriteSongMids.Contains(_activeSong.Mid)) ||
                                        (_activeSong.Id > 0 && _favoriteSongIds.Contains(_activeSong.Id));
                            _controlBar.SetFavoriteStatus(isFav);
                        }
                        _controlBar.UpdateStatus($"[同步完成] 我的喜欢已完成全量同步并缓存（共 {allSongs.Count} 首）");
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Error("FavoriteSync", "Background sync failed", ex);
            }
        }, ct);
    }

    private async Task LoadMoreFavoriteSongsAsync()
    {
        if (_isLoadingMoreFavorites || !_hasMoreFavorites) return;

        _isLoadingMoreFavorites = true;
        var nextPage = _favoriteCurrentPage + 1;

        Application.Invoke(() =>
        {
            string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
            _songListView.Title = $"我的喜欢: 已载入 {_songListView.Songs.Count}{totalHint} 首 (正在加载第 {nextPage} 页...)";
            _controlBar.UpdateStatus($"[正在加载] 正在获取我的喜欢更多曲目 (第 {nextPage} 页)...");
        });

        try
        {
            var result = await MusicApi.GetFavoriteSongsAsync(nextPage, FavoritePageSize);
            var moreSongs = result.Songs;

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    _favoriteCurrentPage = nextPage;
                    if (result.Total > 0) _favoriteTotalCount = result.Total;

                    lock (_favoriteSongMids)
                    {
                        foreach (var s in moreSongs)
                        {
                            if (!string.IsNullOrEmpty(s.Mid)) _favoriteSongMids.Add(s.Mid);
                            if (s.Id > 0) _favoriteSongIds.Add(s.Id);
                        }
                    }

                    var newTotalLoaded = _songListView.Songs.Count + moreSongs.Count;
                    _hasMoreFavorites = result.HasMore || (_favoriteTotalCount > newTotalLoaded);

                    string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
                    var title = $"我的喜欢: 已载入 {newTotalLoaded}{totalHint} 首" + (_hasMoreFavorites ? "，向下滚动加载更多" : "，已全部加载");
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    _controlBar.UpdateStatus($"[加载完成] 我的喜欢已载入 {newTotalLoaded}{totalHint} 首");
                }
                else
                {
                    _hasMoreFavorites = false;
                    string totalHint = _favoriteTotalCount > 0 ? $"/{_favoriteTotalCount}" : "";
                    _songListView.Title = $"我的喜欢: 共 {_songListView.Songs.Count}{totalHint} 首，已全部加载";
                    _controlBar.UpdateStatus($"[已全部加载] 我的喜欢共 {_songListView.Songs.Count}{totalHint} 首曲目");
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
            _isLoadingMoreFavorites = false;
        }
    }

    private bool IsSongFavorite(Song song)
    {
        lock (_favoriteSongMids)
        {
            return (!string.IsNullOrEmpty(song.Mid) && _favoriteSongMids.Contains(song.Mid)) ||
                   (song.Id > 0 && _favoriteSongIds.Contains(song.Id));
        }
    }

    private void ApplySongFavoriteState(Song song, bool isFavorite)
    {
        lock (_favoriteSongMids)
        {
            if (isFavorite)
            {
                if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Add(song.Mid);
                if (song.Id > 0) _favoriteSongIds.Add(song.Id);
            }
            else
            {
                if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Remove(song.Mid);
                if (song.Id > 0) _favoriteSongIds.Remove(song.Id);
            }
        }

        if (_activeSong?.Mid == song.Mid)
        {
            Application.Invoke(() => _controlBar.SetFavoriteStatus(isFavorite));
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.IsCurrentSongFavorite = isFavorite;
                _standaloneWebServer.BroadcastState("favorite_change");
            }
        }

        var uin = UserSession.Current.Uin;
        var cache = MetadataCacheService.GetFavoriteCache(uin);
        if (cache != null)
        {
            cache.Songs.RemoveAll(s => s.Mid == song.Mid || (song.Id > 0 && s.Id == song.Id));
            if (isFavorite)
            {
                cache.Songs.Insert(0, song);
            }
            cache.TotalCount = cache.Songs.Count;
            cache.FirstSongMid = cache.Songs.Count > 0 ? cache.Songs[0].Mid : "";
            MetadataCacheService.SaveFavoriteCache(uin, cache.TotalCount, cache.FirstSongMid, cache.Songs);
        }

        if (_currentViewMode == ViewMode.Favorite)
        {
            if (isFavorite)
            {
                Application.Invoke(() => _songListView.InsertSong(0, song, $"我的喜欢: 共 {_songListView.Songs.Count + 1} 首 (按 G 查找)"));
            }
            else
            {
                Application.Invoke(() => _songListView.RemoveSong(song));
            }
        }

        BroadcastConnectPlayerState();
    }

    private async Task ToggleSongFavoriteAsync(Song song)
    {
        if (song.IsLocal || song.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目不支持在线收藏");
            return;
        }

        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后再进行收藏操作");
            return;
        }

        bool isFav = IsSongFavorite(song);

        if (isFav)
        {
            _controlBar.UpdateStatus($"[正在取消收藏] 正在将《{song.Title}》从我的喜欢中移除...");
            var ok = await MusicApi.RemoveSongFromFavoriteAsync(song);
            if (ok)
            {
                ApplySongFavoriteState(song, false);
                _controlBar.UpdateStatus($"[取消收藏成功] 已将《{song.Title}》从我的喜欢中移除");
            }
            else
            {
                _controlBar.UpdateStatus($"[操作失败] 从我的喜欢移除《{song.Title}》失败");
            }
        }
        else
        {
            _controlBar.UpdateStatus($"[正在收藏] 正在将《{song.Title}》添加至我的喜欢...");
            var ok = await MusicApi.AddSongToFavoriteAsync(song);
            if (ok)
            {
                ApplySongFavoriteState(song, true);
                _controlBar.UpdateStatus($"[收藏成功] 已将《{song.Title}》添加至我的喜欢");
            }
            else
            {
                _controlBar.UpdateStatus($"[操作失败] 添加《{song.Title}》至我的喜欢失败");
            }
        }
    }

    private async Task HandleSmartFavoriteAsync()
    {
        var focused = Application.Navigation?.GetFocused();
        int focusedWindow = GetFocusedWindowIndex(focused);

        Song? targetSong;
        if (focusedWindow == 1) // 只有聚焦中间主界面时收藏选中的歌曲
        {
            if (_isViewingPlaylistsList || _isViewingAlbumsList)
            {
                _controlBar.UpdateStatus("[操作提示] 当前在目录视图，请进入歌曲列表后再按 S 收藏");
                return;
            }

            targetSong = _songListView.GetSelectedSong();
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前歌曲列表中未选中任何曲目");
                return;
            }
        }
        else // 其它界面（边栏、歌词、底栏、全屏播放界面等）均收藏当前正在播放的曲目
        {
            targetSong = _activeSong;
            if (targetSong == null)
            {
                _controlBar.UpdateStatus("[操作提示] 当前暂无播放曲目，请先点播歌曲");
                return;
            }
        }

        if (targetSong.IsLocal || targetSong.IsWebDav)
        {
            _controlBar.UpdateStatus("本地/WebDAV 曲目不支持在线收藏");
            return;
        }

        await ToggleSongFavoriteAsync(targetSong);
    }

    private async Task HandleRemoveFromCurrentListAsync()
    {
        if (_currentViewMode == ViewMode.GuessRecommend)
        {
            _controlBar.UpdateStatus("[电台切歌] 不喜欢当前曲目，已切至下一首");
            await PlayNextRadioTrackAsync();
            return;
        }

        if (_isViewingPlaylistsList)
        {
            _controlBar.UpdateStatus("[操作提示] 当前在歌单目录，请在歌曲列表中按 D 移除歌曲");
            return;
        }

        if (_isViewingAlbumsList)
        {
            await HandleRemoveAlbumFromFavoriteAsync();
            return;
        }

        var song = _songListView.GetSelectedSong();
        if (song == null)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选择要移除的歌曲");
            return;
        }

        if (_currentViewMode == ViewMode.RecentPlay)
        {
            RecentPlayHistory.Remove(song);
            _songListView.RemoveSong(song);
            _controlBar.UpdateStatus($"[移除成功] 已从最近播放记录中移除《{song.Title}》");
            return;
        }

        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后操作");
            return;
        }

        var sidebarIdx = _sidebarList.SelectedItem ?? 0;
        bool isFavoriteList = (sidebarIdx == 1 && _currentDrilldownPlaylist == null) || (_currentDrilldownPlaylist?.DirId == 201);

        if (isFavoriteList)
        {
            _controlBar.UpdateStatus($"[正在移除] 正在从我的喜欢中移除《{song.Title}》...");
            var ok = await MusicApi.RemoveSongFromFavoriteAsync(song);
            if (ok)
            {
                lock (_favoriteSongMids)
                {
                    if (!string.IsNullOrEmpty(song.Mid)) _favoriteSongMids.Remove(song.Mid);
                    if (song.Id > 0) _favoriteSongIds.Remove(song.Id);
                }

                if (_activeSong?.Mid == song.Mid)
                {
                    Application.Invoke(() => _controlBar.SetFavoriteStatus(false));
                }

                Application.Invoke(() =>
                {
                    _songListView.RemoveSong(song);
                    _controlBar.UpdateStatus($"[移除成功] 已从我的喜欢中移除《{song.Title}》");
                });
            }
            else
            {
                _controlBar.UpdateStatus($"[移除失败] 从我的喜欢中移除《{song.Title}》失败");
            }
            return;
        }

        if (_currentDrilldownPlaylist != null)
        {
            if (!_currentDrilldownPlaylist.IsCreated)
            {
                _controlBar.UpdateStatus("[无法移除] 收藏的他人的外部歌单不支持单曲删除");
                return;
            }

            _controlBar.UpdateStatus($"[正在移除] 正在从歌单「{_currentDrilldownPlaylist.Title}」移除《{song.Title}》...");
            var ok = await MusicApi.RemoveSongFromPlaylistAsync(_currentDrilldownPlaylist, song);
            if (ok)
            {
                Application.Invoke(() =>
                {
                    _songListView.RemoveSong(song);
                    _controlBar.UpdateStatus($"[移除成功] 已从歌单「{_currentDrilldownPlaylist.Title}」移除《{song.Title}》");
                });
            }
            else
            {
                _controlBar.UpdateStatus($"[移除失败] 从歌单中移除《{song.Title}》失败");
            }
            return;
        }

        _controlBar.UpdateStatus("[操作提示] 只能在“我的喜欢”或自建歌单中移除歌曲");
    }

    private async Task HandleAddToPlaylistAsync()
    {
        if (Interlocked.CompareExchange(ref _isAddToPlaylistOpen, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Song? song = null;

            if (_isNowPlayingViewActive)
            {
                // 按 V 的播放界面里：直接添加当前正在播放的歌曲
                song = _activeSong ?? _controlBar.CurrentSong;
                if (song == null)
                {
                    _controlBar.UpdateStatus("[操作提示] 当前暂无正在播放的歌曲");
                    return;
                }
            }
            else
            {
                var focused = Application.Navigation?.GetFocused();
                int focusedWindow = GetFocusedWindowIndex(focused);

                if (focusedWindow == 1)
                {
                    // 中间区域高亮获焦：选择添加当前高亮选中的歌曲
                    if (_isViewingPlaylistsList)
                    {
                        _controlBar.UpdateStatus("[操作提示] 当前为歌单列表，请进入歌单选择歌曲后再按 A 添加（新建歌单请按 N）");
                        return;
                    }

                    song = _songListView.GetSelectedSong();
                    if (song == null)
                    {
                        _controlBar.UpdateStatus("[操作提示] 当前未选中任何歌曲");
                        return;
                    }
                }
                else
                {
                    // 中间视图以外焦点：添加当前正在播放的歌曲
                    song = _activeSong ?? _controlBar.CurrentSong;
                    if (song == null)
                    {
                        _controlBar.UpdateStatus("[操作提示] 当前暂无正在播放的歌曲");
                        return;
                    }
                }
            }

            if (song.IsLocal || song.IsWebDav)
            {
                _controlBar.UpdateStatus("本地/WebDAV 曲目不支持在线加入歌单");
                return;
            }

            if (!UserSession.Current.IsLoggedIn)
            {
                _controlBar.UpdateStatus("[未登录] 请按 U 登录后再添加至歌单");
                return;
            }

            // 获取或复用可写的自建歌单列表（排除我喜欢）
            List<Playlist> playlists = _cachedPlaylists;
            if (playlists == null || playlists.Count == 0)
            {
                Application.Invoke(() => _controlBar.UpdateStatus("[正在获取] 正在同步自建歌单列表..."));
                playlists = await MusicApi.GetPlaylistsAsync();
                _cachedPlaylists = playlists;
            }

            var writable = playlists.Where(p => p.IsCreated && !p.IsMyFavorite).ToList();

            var dlg = new AddToPlaylistDialog(song, writable, async (targetPlaylist) =>
            {
                Application.Invoke(() => _controlBar.UpdateStatus($"[正在检测] 正在将《{song.Title}》加入「{targetPlaylist.Title}」..."));
                var result = await AddSongToPlaylistWithCheckAsync(targetPlaylist, song);
                Application.Invoke(async () =>
                {
                    if (result == AddToPlaylistResult.Success)
                    {
                        _controlBar.UpdateStatus($"[添加成功] 已将《{song.Title}》加入「{targetPlaylist.Title}」");
                        _cachedPlaylists.Clear();

                        if (_currentViewMode == ViewMode.PlaylistDrilldown &&
                            _currentDrilldownPlaylist != null &&
                            _currentDrilldownPlaylist.DirId == targetPlaylist.DirId)
                        {
                            // 1. 若当前正处于该歌单的页面，立即刷新该歌单曲目
                            await DrilldownPlaylistAsync(_currentDrilldownPlaylist);
                        }
                        else if (_currentViewMode == ViewMode.PlaylistsList || _isViewingPlaylistsList)
                        {
                            // 2. 若当前处于“我的歌单”目录页面，立即刷新歌单列表以更新曲目数量
                            await LoadPlaylistsAsync();
                        }
                    }
                    else if (result == AddToPlaylistResult.AlreadyExists)
                    {
                        _controlBar.UpdateStatus($"[已在歌单] 《{song.Title}》已存在于「{targetPlaylist.Title}」中，未重复添加");
                    }
                    else
                    {
                        _controlBar.UpdateStatus($"[添加失败] 添加至「{targetPlaylist.Title}」失败，请重试");
                    }
                });
            });

            RunModalDialog(dlg);

            // 弹窗关闭后，若当前处于歌单目录视图，自动同步新建或删除歌单的变动
            if (_currentViewMode == ViewMode.PlaylistsList || _isViewingPlaylistsList)
            {
                _cachedPlaylists.Clear();
                _ = LoadPlaylistsAsync();
            }
        }
        finally
        {
            Volatile.Write(ref _isAddToPlaylistOpen, 0);
            Application.Invoke(() => SetNeedsDraw());
        }
    }

    private enum AddToPlaylistResult
    {
        Success,
        AlreadyExists,
        Failed
    }

    private async Task<AddToPlaylistResult> AddSongToPlaylistWithCheckAsync(Playlist targetPlaylist, Song song)
    {
        try
        {
            // 1. 若当前正处于该歌单的页面，直接在已加载曲目中比对
            if (_currentViewMode == ViewMode.PlaylistDrilldown &&
                _currentDrilldownPlaylist != null &&
                _currentDrilldownPlaylist.DirId == targetPlaylist.DirId)
            {
                bool existsInCurrent = _songListView.Songs.Any(s =>
                    (!string.IsNullOrEmpty(s.Mid) && s.Mid == song.Mid) ||
                    (s.Id > 0 && song.Id > 0 && s.Id == song.Id));
                if (existsInCurrent)
                {
                    return AddToPlaylistResult.AlreadyExists;
                }
            }

            // 2. 预先查询目标歌单当前歌曲（支持最大 300 首检查）
            var existingSongs = await MusicApi.GetPlaylistSongsAsync(targetPlaylist, 1, 300).ConfigureAwait(false);
            if (existingSongs.Count > 0)
            {
                bool alreadyInPlaylist = existingSongs.Any(s =>
                    (!string.IsNullOrEmpty(s.Mid) && s.Mid == song.Mid) ||
                    (s.Id > 0 && song.Id > 0 && s.Id == song.Id));
                if (alreadyInPlaylist)
                {
                    return AddToPlaylistResult.AlreadyExists;
                }
            }

            // 3. 执行添加
            var ok = await MusicApi.AddSongToPlaylistAsync(targetPlaylist, song).ConfigureAwait(false);
            return ok ? AddToPlaylistResult.Success : AddToPlaylistResult.Failed;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Library", $"AddSongToPlaylistWithCheckAsync error: {ex.Message}");
            return AddToPlaylistResult.Failed;
        }
    }

    private void HandleCreatePlaylistAsync()
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后再新建歌单");
            return;
        }

        var dlg = new CreatePlaylistDialog(async (name) =>
        {
            _controlBar.UpdateStatus($"[正在创建] 正在创建歌单「{name}」...");
            var result = await MusicApi.CreatePlaylistAsync(name);
            Application.Invoke(async () =>
            {
                if (result.Success)
                {
                    _controlBar.UpdateStatus($"[创建成功] 已创建歌单「{name}」");
                    if (_currentViewMode == ViewMode.PlaylistsList)
                    {
                        await LoadPlaylistsAsync();
                    }
                }
                else
                {
                    _controlBar.UpdateStatus($"[创建失败] 创建歌单「{name}」失败: {result.Message}");
                }
            });
        });

        RunModalDialog(dlg);
    }

    private async Task HandleDeleteSelectedPlaylistAsync()
    {
        if (_currentViewMode != ViewMode.PlaylistsList || _cachedPlaylists == null || _cachedPlaylists.Count == 0)
        {
            return;
        }

        int idx = _songListView.SelectedItem ?? -1;
        if (idx < 0 || idx >= _cachedPlaylists.Count)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选择要删除的歌单");
            return;
        }

        var playlist = _cachedPlaylists[idx];
        if (playlist.IsMyFavorite)
        {
            _controlBar.UpdateStatus("[不可删除] 「我喜欢」歌单不支持删除");
            return;
        }

        bool confirmed = false;
        var dlg = new Dialog
        {
            Title = "删除歌单确认",
            Width = 48,
            Height = 8,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = $"确定要删除歌单「{playlist.Title}」吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        dlg.Add(msg);

        var yesBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1)
        };
        yesBtn.KeyBindings.Remove(Key.Space);
        yesBtn.Accepting += (s, e) =>
        {
            confirmed = true;
            Application.RequestStop(dlg);
        };
        dlg.Add(yesBtn);

        var cancelBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 4,
            Y = Pos.AnchorEnd(1)
        };
        cancelBtn.KeyBindings.Remove(Key.Space);
        cancelBtn.Accepting += (s, e) =>
        {
            confirmed = false;
            Application.RequestStop(dlg);
        };
        dlg.Add(cancelBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        RunModalDialog(dlg);

        if (confirmed)
        {
            _controlBar.UpdateStatus($"[正在删除] 正在删除歌单「{playlist.Title}」...");
            var ok = await MusicApi.DeletePlaylistAsync(playlist);
            if (ok)
            {
                _controlBar.UpdateStatus($"[删除成功] 已删除歌单「{playlist.Title}」");
                await LoadPlaylistsAsync();
            }
            else
            {
                _controlBar.UpdateStatus($"[删除失败] 删除歌单「{playlist.Title}」失败，请重试");
            }
        }
    }

    private async Task HandleRemoveAlbumFromFavoriteAsync()
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            _controlBar.UpdateStatus("[未登录] 请按 U 登录后操作");
            return;
        }

        var idx = _songListView.SelectedItem ?? -1;
        if (idx < 0 || idx >= _cachedAlbums.Count)
        {
            _controlBar.UpdateStatus("[操作提示] 请先选中要取消收藏的专辑");
            return;
        }

        var album = _cachedAlbums[idx];
        _controlBar.UpdateStatus($"[正在取消收藏] 正在取消收藏专辑《{album.Title}》...");

        var ok = await MusicApi.RemoveAlbumFromFavoriteAsync(album.Mid);
        if (ok)
        {
            _cachedAlbums.RemoveAt(idx);
            Application.Invoke(() =>
            {
                if (_cachedAlbums.Count == 0)
                {
                    _songListView.SetMessage("当前账号暂无收藏专辑数据", "收藏专辑: 0 张");
                }
                else
                {
                    var items = new List<string>(_cachedAlbums.Count);
                    for (int i = 0; i < _cachedAlbums.Count; i++)
                    {
                        var a = _cachedAlbums[i];
                        items.Add($"{(i + 1):D2}  {a.Title}  -  {a.Artist}  (共 {a.SongCount} 首)");
                    }

                    _songListView.SetCustomItems(items, $"收藏专辑: 共 {_cachedAlbums.Count} 张 (按 Enter 进入专辑，按 D 取消收藏)", async (newIdx) =>
                    {
                        if (newIdx >= 0 && newIdx < _cachedAlbums.Count)
                        {
                            await DrilldownAlbumAsync(_cachedAlbums[newIdx]);
                        }
                    }, (selectedIdx) =>
                    {
                        if (selectedIdx >= 0 && selectedIdx < _cachedAlbums.Count)
                        {
                            _ = PreviewAlbumDetailAsync(_cachedAlbums[selectedIdx].Mid, _cachedAlbums[selectedIdx].Title, _cachedAlbums[selectedIdx].Artist);
                        }
                    });
                }
                _controlBar.UpdateStatus($"[取消收藏成功] 已取消收藏专辑《{album.Title}》");
            });
        }
        else
        {
            Application.Invoke(() =>
            {
                _controlBar.UpdateStatus($"[操作失败] 取消收藏专辑《{album.Title}》失败，请稍后重试");
            });
        }
    }

    private async Task LoadLocalMusicAsync()
    {
        _currentViewMode = ViewMode.LocalMusic;
        _hasMoreSearchResults = false;
        _isViewingPlaylistsList = false;
        _currentDrilldownPlaylist = null;
        _isViewingAlbumsList = false;
        _currentDrilldownAlbum = null;

        var folders = QmTui.Services.LocalMusicService.GetFolders();
        if (folders.Count == 0)
        {
            Application.Invoke(() =>
            {
                _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                _controlBar.UpdateStatus("[本地音乐] 未配置扫描目录，请按 A 键添加本地音乐目录");
            });
            return;
        }

        var cachedSongs = QmTui.Services.LocalMusicService.GetCachedSongs();
        if (cachedSongs.Count > 0)
        {
            Application.Invoke(() =>
            {
                var title = $"本地音乐: 共 {cachedSongs.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
                _songListView.SetSongs(cachedSongs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                _songListView.SetFocusToList();
                _controlBar.UpdateStatus($"[本地音乐] 已载入 {cachedSongs.Count} 首本地音乐 (共 {folders.Count} 个扫描目录)");
            });
        }
        else
        {
            await RescanLocalMusicAsync();
        }
    }

    private bool _isScanningLocalMusic;

    private async Task RescanLocalMusicAsync()
    {
        if (_isScanningLocalMusic)
        {
            _controlBar.UpdateStatus("[本地音乐] 正在扫描中，请稍候...");
            return;
        }

        _isScanningLocalMusic = true;
        try
        {
            _currentViewMode = ViewMode.LocalMusic;
            var folders = QmTui.Services.LocalMusicService.GetFolders();
            if (folders.Count == 0)
            {
                Application.Invoke(() =>
                {
                    _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                    _controlBar.UpdateStatus("[本地音乐] 未配置扫描目录，请按 A 键添加本地音乐目录");
                });
                return;
            }

            Application.Invoke(() =>
            {
                _songListView.SetMessage("正在扫描本地音乐目录 (递归检索音频文件并跳过隐藏项)...", "本地音乐 (扫描中)");
                _controlBar.UpdateStatus("[正在扫描] 正在深度检索本地音频文件元数据...");
            });

            var songs = await QmTui.Services.LocalMusicService.ScanAllFoldersAsync(progress =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[正在扫描] {progress}");
                });
            });

            Application.Invoke(() =>
            {
                if (songs.Count == 0)
                {
                    _songListView.SetMessage("已配置的目录中未发现音频文件 (按 A 键添加其它文件夹，按 F 管理目录)", "本地音乐: 0 首");
                    _controlBar.UpdateStatus("[扫描完成] 未发现有效音频文件，请确认目录中包含 .flac/.mp3/.m4a 等文件");
                    return;
                }

                var title = $"本地音乐: 共 {songs.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
                _songListView.SetSongs(songs, title);
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                _songListView.SetFocusToList();
                _controlBar.UpdateStatus($"[扫描完成] 已发现并载入 {songs.Count} 首本地音乐 (共 {folders.Count} 个扫描目录)");
            });
        }
        finally
        {
            _isScanningLocalMusic = false;
        }
    }

    private void ShowAddFolderDialog()
    {
        using var dlg = new AddFolderDialog(async folder =>
        {
            var added = QmTui.Services.LocalMusicService.AddFolder(folder);
            if (added)
            {
                _controlBar.UpdateStatus($"[已添加目录] {folder}，正在触发扫描...");
                await RescanLocalMusicAsync();
            }
            else
            {
                _controlBar.UpdateStatus($"[添加失败] 目录不存在或已被添加: {folder}");
            }
        });
        RunModalDialog(dlg);
    }

    private void ShowFolderManageDialog()
    {
        using var dlg = new FolderManageDialog(
            onFoldersChanged: () =>
            {
                var cached = QmTui.Services.LocalMusicService.GetCachedSongs();
                if (_currentViewMode == ViewMode.LocalMusic)
                {
                    if (cached.Count == 0)
                    {
                        _songListView.SetMessage("本地音乐库为空 (请按 A 键添加本地音乐文件夹进行扫描，按 F 管理目录)", "本地音乐: 0 首");
                    }
                    else
                    {
                        var folders = QmTui.Services.LocalMusicService.GetFolders();
                        var title = $"本地音乐: 共 {cached.Count} 首 (按 A 添加目录，按 R 重新扫描，按 F 管理目录)";
                        _songListView.SetSongs(cached, title);
                    }
                }
            },
            onAddRequested: () =>
            {
                ShowAddFolderDialog();
            },
            onRescanRequested: () =>
            {
                _ = RescanLocalMusicAsync();
            }
        );
        RunModalDialog(dlg);
    }


}
