using Terminal.Gui.App;
using QmTui.Api;
using QmTui.Models;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    internal async Task SwitchSearchCategoryAsync(SearchCategory category)
    {
        if (_searchCategory == category) return;
        _searchCategory = category;
        UpdateSearchCategoryButtons();

        var text = _searchField.Text.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = _lastSearchQuery;
        }

        if (!string.IsNullOrEmpty(text))
        {
            await ExecuteSearchAsync();
        }
    }

    private async Task ExecuteSearchAsync()
    {
        if (_isSearching) return;
        _isSearching = true;

        try
        {
            _currentViewMode = ViewMode.Search;
            _currentDrilldownPlaylist = null;
            _currentDrilldownAlbum = null;

            var text = _searchField.Text.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                text = _lastSearchQuery;
            }
            if (string.IsNullOrEmpty(text)) return;

            if (!string.Equals(_lastSearchQuery, text, StringComparison.OrdinalIgnoreCase))
            {
                _cachedSearchSongs.Clear();
                _cachedSearchPlaylists.Clear();
                _cachedSearchAlbums.Clear();
            }

            _lastSearchQuery = text;

            switch (_searchCategory)
            {
                case SearchCategory.Songs:
                    _searchCurrentPage = 1;
                    _hasMoreSearchResults = true;
                    await ExecuteSearchSongsAsync(text);
                    break;
                case SearchCategory.Playlists:
                    _searchPlaylistsCurrentPage = 1;
                    _hasMoreSearchPlaylists = true;
                    await ExecuteSearchPlaylistsAsync(text);
                    break;
                case SearchCategory.Albums:
                    _searchAlbumsCurrentPage = 1;
                    _hasMoreSearchAlbums = true;
                    await ExecuteSearchAlbumsAsync(text);
                    break;
            }
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

    private async Task ExecuteSearchSongsAsync(string text)
    {
        _songListView.SetMessage("正在搜索单曲...", $"搜索「{text}」单曲中...");

        if (_cachedSearchSongs.TryGetValue(text, out var cached) && cached.Count > 0)
        {
            Application.Invoke(() =>
            {
                _hasMoreSearchResults = cached.Count >= 20;
                _songListView.SetSongs(cached, $"搜索单曲: 共 {cached.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)"));
                if (_activeSong != null)
                {
                    _songListView.SetPlayingSong(_activeSong.Mid);
                }
                UpdateSearchCategoryVisibility(true);
            });
            return;
        }

        var songs = await MusicApi.SearchAsync(text, 1, PageSize);
        _cachedSearchSongs[text] = [.. songs];

        Application.Invoke(() =>
        {
            _hasMoreSearchResults = songs.Count >= 20;
            var title = $"搜索单曲: 共 {songs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
            _songListView.SetSongs(songs, title);
            if (_activeSong != null)
            {
                _songListView.SetPlayingSong(_activeSong.Mid);
            }
            if (songs.Count == 0)
            {
                _songListView.SetMessage("未找到相关单曲", title);
            }
            UpdateSearchCategoryVisibility(true);
        });
    }

    private async Task ExecuteSearchPlaylistsAsync(string text)
    {
        _songListView.SetMessage("正在搜索歌单...", $"搜索「{text}」歌单中...");

        if (_cachedSearchPlaylists.TryGetValue(text, out var cached))
        {
            Application.Invoke(() => RenderSearchPlaylists(cached));
            return;
        }

        var playlists = await MusicApi.SearchPlaylistsAsync(text, 1, 50);
        _cachedSearchPlaylists[text] = [.. playlists];

        Application.Invoke(() => RenderSearchPlaylists(playlists));
    }

    private void RenderSearchPlaylists(List<Playlist> playlists)
    {
        if (playlists.Count == 0)
        {
            _hasMoreSearchPlaylists = false;
            _songListView.SetMessage("未找到相关歌单", "搜索歌单: 共 0 个 (已全部加载)");
            UpdateSearchCategoryVisibility(true);
            return;
        }

        _cachedPlaylists.Clear();
        _cachedPlaylists.AddRange(playlists);

        _hasMoreSearchPlaylists = playlists.Count >= 20;
        var headerTitle = $"搜索歌单: 共 {playlists.Count} 个" + (_hasMoreSearchPlaylists ? " (向下滚动加载更多)" : " (已全部加载)");
        _songListView.SetPlaylists(playlists, headerTitle, async (idx) =>
        {
            if (idx >= 0 && idx < _cachedPlaylists.Count)
            {
                await DrilldownPlaylistAsync(_cachedPlaylists[idx]);
            }
        });

        _songListView.SetFocusToList();
        UpdateSearchCategoryVisibility(true);
    }

    private async Task ExecuteSearchAlbumsAsync(string text)
    {
        _songListView.SetMessage("正在搜索专辑...", $"搜索「{text}」专辑中...");

        if (_cachedSearchAlbums.TryGetValue(text, out var cached))
        {
            Application.Invoke(() => RenderSearchAlbums(cached));
            return;
        }

        var albums = await MusicApi.SearchAlbumsAsync(text, 1, PageSize);
        _cachedSearchAlbums[text] = [.. albums];

        Application.Invoke(() => RenderSearchAlbums(albums));
    }

    private void RenderSearchAlbums(List<Album> albums)
    {
        if (albums.Count == 0)
        {
            _hasMoreSearchAlbums = false;
            _songListView.SetMessage("未找到相关专辑", "搜索专辑: 共 0 张 (已全部加载)");
            UpdateSearchCategoryVisibility(true);
            return;
        }

        _cachedAlbums.Clear();
        _cachedAlbums.AddRange(albums);

        _hasMoreSearchAlbums = albums.Count >= 20;
        var headerTitle = $"搜索专辑: 共 {albums.Count} 张" + (_hasMoreSearchAlbums ? " (向下滚动加载更多)" : " (已全部加载)");
        _songListView.SetAlbums(albums, headerTitle, async (idx) =>
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
        UpdateSearchCategoryVisibility(true);
    }

    private async Task LoadMoreSearchResultsAsync()
    {
        if (_isLoadingMore || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        switch (_searchCategory)
        {
            case SearchCategory.Songs:
                await LoadMoreSearchSongsAsync();
                break;
            case SearchCategory.Playlists:
                await LoadMoreSearchPlaylistsAsync();
                break;
            case SearchCategory.Albums:
                await LoadMoreSearchAlbumsAsync();
                break;
        }
    }

    private async Task LoadMoreSearchSongsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchResults || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        _isLoadingMore = true;
        var nextPage = _searchCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"搜索单曲: 共 {_songListView.Songs.Count} 首 (正在加载更多...)";
        });

        try
        {
            var moreSongs = await MusicApi.SearchAsync(_lastSearchQuery, nextPage, PageSize);

            Application.Invoke(() =>
            {
                if (moreSongs.Count > 0)
                {
                    _searchCurrentPage = nextPage;
                    var currentCount = _songListView.Songs.Count;

                    if (moreSongs.Count < 10)
                    {
                        _hasMoreSearchResults = false;
                    }

                    var title = $"搜索单曲: 共 {currentCount + moreSongs.Count} 首" + (_hasMoreSearchResults ? " (向下滚动加载更多)" : " (已全部加载)");
                    _songListView.AppendSongs(moreSongs, title);
                    if (_activeSong != null)
                    {
                        _songListView.SetPlayingSong(_activeSong.Mid);
                    }
                    if (_cachedSearchSongs.TryGetValue(_lastSearchQuery, out var list))
                    {
                        list.AddRange(moreSongs);
                    }
                }
                else
                {
                    _hasMoreSearchResults = false;
                    _songListView.Title = $"搜索单曲: 共 {_songListView.Songs.Count} 首 (已全部加载)";
                }
            });
        }
        catch (Exception ex)
        {
            QmTui.Utils.AppLogger.Error("Search", "LoadMoreSearchSongsAsync failed", ex);
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private async Task LoadMoreSearchPlaylistsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchPlaylists || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        _isLoadingMore = true;
        var nextPage = _searchPlaylistsCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"搜索歌单: 共 {_cachedPlaylists.Count} 个 (正在加载更多...)";
        });

        try
        {
            var morePlaylists = await MusicApi.SearchPlaylistsAsync(_lastSearchQuery, nextPage, 50);

            Application.Invoke(() =>
            {
                var existingIds = new HashSet<long>(_cachedPlaylists.Select(p => p.DirId));
                var newPlaylists = morePlaylists.Where(p => existingIds.Add(p.DirId)).ToList();

                if (newPlaylists.Count > 0)
                {
                    _searchPlaylistsCurrentPage = nextPage;
                    _cachedPlaylists.AddRange(newPlaylists);

                    if (morePlaylists.Count < 10)
                    {
                        _hasMoreSearchPlaylists = false;
                    }

                    var title = $"搜索歌单: 共 {_cachedPlaylists.Count} 个" + (_hasMoreSearchPlaylists ? " (向下滚动加载更多)" : " (已全部加载)");
                    _songListView.AppendPlaylists(newPlaylists, title);

                    if (_cachedSearchPlaylists.TryGetValue(_lastSearchQuery, out var list))
                    {
                        list.AddRange(newPlaylists);
                    }
                }
                else
                {
                    _hasMoreSearchPlaylists = false;
                    _songListView.Title = $"搜索歌单: 共 {_cachedPlaylists.Count} 个 (已全部加载)";
                }
            });
        }
        catch (Exception ex)
        {
            QmTui.Utils.AppLogger.Error("Search", "LoadMoreSearchPlaylistsAsync failed", ex);
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private async Task LoadMoreSearchAlbumsAsync()
    {
        if (_isLoadingMore || !_hasMoreSearchAlbums || string.IsNullOrEmpty(_lastSearchQuery))
        {
            return;
        }

        _isLoadingMore = true;
        var nextPage = _searchAlbumsCurrentPage + 1;

        Application.Invoke(() =>
        {
            _songListView.Title = $"搜索专辑: 共 {_cachedAlbums.Count} 张 (正在加载更多...)";
        });

        try
        {
            var moreAlbums = await MusicApi.SearchAlbumsAsync(_lastSearchQuery, nextPage, PageSize);

            Application.Invoke(() =>
            {
                var existingMids = new HashSet<string>(_cachedAlbums.Select(a => a.Mid));
                var newAlbums = moreAlbums.Where(a => existingMids.Add(a.Mid)).ToList();

                if (newAlbums.Count > 0)
                {
                    _searchAlbumsCurrentPage = nextPage;
                    _cachedAlbums.AddRange(newAlbums);

                    if (moreAlbums.Count < 10)
                    {
                        _hasMoreSearchAlbums = false;
                    }

                    var title = $"搜索专辑: 共 {_cachedAlbums.Count} 张" + (_hasMoreSearchAlbums ? " (向下滚动加载更多)" : " (已全部加载)");
                    _songListView.AppendAlbums(newAlbums, title);

                    if (_cachedSearchAlbums.TryGetValue(_lastSearchQuery, out var list))
                    {
                        list.AddRange(newAlbums);
                    }
                }
                else
                {
                    _hasMoreSearchAlbums = false;
                    _songListView.Title = $"搜索专辑: 共 {_cachedAlbums.Count} 张 (已全部加载)";
                }
            });
        }
        catch (Exception ex)
        {
            QmTui.Utils.AppLogger.Error("Search", "LoadMoreSearchAlbumsAsync failed", ex);
        }
        finally
        {
            _isLoadingMore = false;
        }
    }
}
