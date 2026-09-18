using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rectangle = System.Drawing.Rectangle;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;
using QmTui.Player;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    private void ToggleTranslation()
    {
        if (!_hasTranslation) return;
        _showTranslation = !_showTranslation;
        UpdateTranslationButtonHighlight();
        RefreshLyricListView();
        _nowPlayingView.SetTranslationState(_showTranslation);
        _nowPlayingView.SetLyrics(_currentLyrics, _showTranslation);
    }

    private void UpdateTranslationButtonHighlight()
    {
        if (_lyricTransBtn == null) return;
        _lyricTransBtn.Visible = _hasTranslation;
        var color = (_showTranslation && _hasTranslation) ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        var attr = new Terminal.Gui.Drawing.Attribute(color, Color.None);
        _lyricTransBtn.SetScheme(new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Highlight = attr,
            Disabled = attr
        });
        _lyricTransBtn.SetNeedsDraw();
    }

    private void RefreshLyricListView()
    {
        int viewW = _lyricListView.Viewport.Width > 0 ? _lyricListView.Viewport.Width : 40;
        int usableWidth = Math.Max(10, viewW - 2);

        if (_currentLyrics.Count == 0)
        {
            var emptyMsg = CenterLyricText("暂无歌词", usableWidth);
            _lyricListView.SetSource(new ObservableCollection<string> { emptyMsg });
            _lyricItemToLineIndex.Clear();
            _lyricLineToFirstItemIndex.Clear();
            return;
        }

        var showTrans = _showTranslation;
        var displayLines = new List<string>();
        _lyricItemToLineIndex.Clear();
        _lyricLineToFirstItemIndex.Clear();

        int viewH = _lyricListView.Viewport.Height > 0 ? _lyricListView.Viewport.Height : 15;
        int padLines = Math.Max(2, (viewH / 2) - 1);

        // 1. 顶部预留视口半高空行留白，确保第一句歌词也能从容滚动至屏幕正中央
        for (int p = 0; p < padLines; p++)
        {
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            var l = _currentLyrics[i];
            _lyricLineToFirstItemIndex[i] = displayLines.Count;

            // 2. 原文行（水平对称居中，智能断行对齐）
            var origWrapped = WrapLyricText(l.Text, usableWidth);
            foreach (var oLine in origWrapped)
            {
                displayLines.Add(CenterLyricText(oLine, usableWidth));
                _lyricItemToLineIndex.Add(i);
            }

            // 3. 翻译行（若启用且非空，紧贴原文下方，同样水平对称居中与智能断行）
            if (showTrans && !string.IsNullOrWhiteSpace(l.Trans))
            {
                var transWrapped = WrapLyricText(l.Trans, usableWidth);
                foreach (var tLine in transWrapped)
                {
                    displayLines.Add(CenterLyricText(tLine, usableWidth));
                    _lyricItemToLineIndex.Add(i);
                }
            }

            // 4. 句落之间插入单个空行，恢复自然舒适的一行呼吸间隔
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        // 5. 底部预留视口半高空行留白，确保最后一句歌词也能从容滚动至屏幕正中央
        for (int p = 0; p < padLines; p++)
        {
            displayLines.Add("");
            _lyricItemToLineIndex.Add(-1);
        }

        var prevIdx = _lyricListView.SelectedItem ?? 0;
        _lyricListView.SetSource(new ObservableCollection<string>(displayLines));
        if (prevIdx >= 0 && prevIdx < displayLines.Count)
        {
            try
            {
                _lyricListView.SelectedItem = prevIdx;
                int targetTop = Math.Max(0, prevIdx - (viewH / 2));
                _lyricListView.Viewport = new Rectangle(
                    _lyricListView.Viewport.X,
                    targetTop,
                    _lyricListView.Viewport.Width,
                    _lyricListView.Viewport.Height
                );
            }
            catch {}
        }
        _lyricScrollBar?.UpdateMetrics(displayLines.Count, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
    }

    public static int GetDisplayWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = 0;
        foreach (var ch in text)
        {
            w += ch > 127 ? 2 : 1;
        }
        return w;
    }

    public static string CenterLyricText(string text, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int w = GetDisplayWidth(text);
        if (w >= targetWidth) return text;
        int pad = Math.Max(0, (targetWidth - w) / 2);
        return new string(' ', pad) + text;
    }

    public static List<string> WrapLyricText(string text, int maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        if (maxWidth <= 4)
        {
            lines.Add(text);
            return lines;
        }

        if (GetDisplayWidth(text) <= maxWidth)
        {
            lines.Add(text);
            return lines;
        }

        var curLine = new StringBuilder();
        int curWidth = 0;
        var tokens = TokenizeText(text);

        foreach (var token in tokens)
        {
            int tokenWidth = GetDisplayWidth(token);
            if (curWidth + tokenWidth <= maxWidth)
            {
                curLine.Append(token);
                curWidth += tokenWidth;
            }
            else
            {
                if (curLine.Length > 0)
                {
                    var lineStr = curLine.ToString().Trim();
                    if (!string.IsNullOrEmpty(lineStr))
                    {
                        lines.Add(lineStr);
                    }
                    curLine.Clear();
                    curWidth = 0;
                }

                if (tokenWidth > maxWidth)
                {
                    foreach (var ch in token)
                    {
                        int chW = ch > 127 ? 2 : 1;
                        if (curWidth + chW > maxWidth)
                        {
                            var s = curLine.ToString().Trim();
                            if (!string.IsNullOrEmpty(s)) lines.Add(s);
                            curLine.Clear();
                            curWidth = 0;
                        }
                        curLine.Append(ch);
                        curWidth += chW;
                    }
                }
                else
                {
                    var trimmedToken = token.TrimStart();
                    if (!string.IsNullOrEmpty(trimmedToken))
                    {
                        curLine.Append(trimmedToken);
                        curWidth += GetDisplayWidth(trimmedToken);
                    }
                }
            }
        }

        if (curLine.Length > 0)
        {
            var lineStr = curLine.ToString().Trim();
            if (!string.IsNullOrEmpty(lineStr)) lines.Add(lineStr);
        }

        return lines.Count > 0 ? lines : new List<string> { text };
    }

    private static List<string> TokenizeText(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c > 127)
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                tokens.Add(" ");
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
                i++;
            }
            else
            {
                int start = i;
                while (i < text.Length && text[i] <= 127 && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
                tokens.Add(text.Substring(start, i - start));
            }
        }
        return tokens;
    }


    private void UpdateLyrics(double currentSec)
    {
        if (_isAodMode) return; // AOD 模式跳过歌词计算与渲染
        if (_nowPlayingView.Visible)
        {
            _nowPlayingView.UpdatePlaybackTime(currentSec);
        }
        if (_currentLyrics.Count == 0) return;

        var currentTs = TimeSpan.FromSeconds(currentSec);
        int activeIndex = -1;

        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            if (_currentLyrics[i].Timestamp <= currentTs)
            {
                activeIndex = i;
            }
            else
            {
                break;
            }
        }

        if (activeIndex != _currentActiveLyricIndex)
        {
            _currentActiveLyricIndex = activeIndex;
            _lyricListView.SetNeedsDraw();
        }

        if (activeIndex >= 0 && _lyricLineToFirstItemIndex.TryGetValue(activeIndex, out int targetListItemIdx))
        {
            var sourceCount = _lyricListView.Source?.Count ?? 0;
            if (targetListItemIdx >= 0 && targetListItemIdx < sourceCount)
            {
                // 仅在用户未手动翻阅浏览且未获焦歌词视窗时，自动居中滚动
                bool isUserBrowsing = _lyricListView.HasFocus || (Environment.TickCount64 - _lastUserLyricScrollTick < 3000);
                if (!isUserBrowsing)
                {
                    try
                    {
                        if (_lyricListView.SelectedItem != targetListItemIdx)
                        {
                            _lyricListView.SelectedItem = targetListItemIdx;
                        }

                        int viewH = _lyricListView.Viewport.Height;
                        if (viewH > 0)
                        {
                            int targetTop = Math.Max(0, targetListItemIdx - (viewH / 2));
                            if (_lyricListView.Viewport.Y != targetTop)
                            {
                                _lyricListView.Viewport = new Rectangle(
                                    _lyricListView.Viewport.X,
                                    targetTop,
                                    _lyricListView.Viewport.Width,
                                    _lyricListView.Viewport.Height
                                );
                            }
                        }
                    }
                    catch
                    {
                        // 忽略切歌过渡期的瞬态索引竞争
                    }
                }
                _lyricScrollBar?.UpdateMetrics(sourceCount, _lyricListView.Viewport.Height, _lyricListView.Viewport.Y);
            }
        }
    }


    public void ScrollLyricToLine(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _currentLyrics.Count)
        {
            _nowPlayingView.ScrollToLine(lineIndex);
        }
    }

    private static readonly HashSet<string> s_matchedOnlineSongKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<LyricLine>> s_originalLyricsBackup = new(StringComparer.OrdinalIgnoreCase);

    private static string GetSongIdentityKey(Song song)
    {
        if (!string.IsNullOrEmpty(song.LocalFilePath)) return "LOCAL:" + song.LocalFilePath;
        if (!string.IsNullOrEmpty(song.WebDavHref)) return "WEBDAV:" + song.WebDavHref;
        return "MID:" + song.Mid;
    }

    private static bool IsCurrentSongLyricMatched(Song? song)
    {
        if (song == null) return false;
        return s_matchedOnlineSongKeys.Contains(GetSongIdentityKey(song));
    }

    /// <summary>
    /// 将匹配得到的歌词文本持久化写回本地或落盘缓存的 .lrc 文件（包含双语翻译）
    /// </summary>
    private static void SaveMatchedLrcToDisk(Song song, string? playUrl, List<LyricLine> lyrics)
    {
        string? lrcSavePath = null;
        if (song.IsLocal && !string.IsNullOrEmpty(song.LocalFilePath))
        {
            lrcSavePath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
        }
        else if (song.IsWebDav)
        {
            var server = WebDavService.GetActiveServer();
            if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
            {
                lrcSavePath = WebDavService.GetLocalLrcCachePath(server, song.WebDavHref);
            }
            else if (!string.IsNullOrEmpty(playUrl) && File.Exists(playUrl))
            {
                lrcSavePath = Path.ChangeExtension(playUrl, ".lrc");
            }
            else if (!string.IsNullOrEmpty(song.LocalFilePath))
            {
                lrcSavePath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
            }
        }

        if (!string.IsNullOrEmpty(lrcSavePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(lrcSavePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder();
                foreach (var line in lyrics)
                {
                    var ts = line.Timestamp;
                    var timeStr = $"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]";
                    sb.AppendLine($"{timeStr}{line.Text}");
                    if (!string.IsNullOrWhiteSpace(line.Trans))
                    {
                        sb.AppendLine($"{timeStr}{line.Trans}");
                    }
                }
                File.WriteAllText(lrcSavePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MainWindow.Playback", $"Failed to write matched .lrc to disk: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 后台自动尝试对本地或 WebDAV 歌曲进行声学切片识别匹配
    /// </summary>
    private async Task AutoMatchLyricAsync(Song song, string? playUrl, bool needLyrics)
    {
        var songKey = GetSongIdentityKey(song);
        try
        {
            // 始终采用声学指纹切片识别 (ACR) 与本地私有缓存
            var acrResult = await LocalLyricAutoMatcher.MatchLyricsAsync(song, playUrl, _currentLyrics, forceMatch: false);
            if (!acrResult.HasValue) return;

            var (bestMatch, onlineLyrics) = acrResult.Value;
            if (onlineLyrics == null || onlineLyrics.Count == 0 || (onlineLyrics.Count == 1 && onlineLyrics[0].Text == "暂无歌词"))
            {
                return;
            }

            // 若原先已有歌词（只是缺少翻译），则必须要求在线歌词包含有效翻译才进行升级替换
            if (!needLyrics && !LyricParser.HasTranslation(onlineLyrics))
            {
                return;
            }

            // 备份原歌词支持按 Y 撤销
            lock (s_originalLyricsBackup)
            {
                s_originalLyricsBackup[songKey] = _currentLyrics.ToList();
            }
            s_matchedOnlineSongKeys.Add(songKey);

            // 持久化到磁盘同名 .lrc（含双语翻译）
            SaveMatchedLrcToDisk(song, playUrl, onlineLyrics);

            // 若在匹配期间用户已经切换了歌曲，则不修改当前界面的展示
            if (_activeSong == null || GetSongIdentityKey(_activeSong) != songKey)
            {
                return;
            }

            _currentLyrics.Clear();
            _currentLyrics.AddRange(onlineLyrics);
            _player.UpdateCurrentLyrics(onlineLyrics);
            if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
            {
                _standaloneWebServer.CurrentLyrics = onlineLyrics;
                _standaloneWebServer.BroadcastState("lyrics_change");
            }

            var hasTrans = LyricParser.HasTranslation(onlineLyrics) && LyricParser.NeedsTranslation(onlineLyrics);
            _showTranslation = hasTrans;
            Application.Invoke(() =>
            {
                _hasTranslation = hasTrans;
                _nowPlayingView.SetLyrics(onlineLyrics, _showTranslation);
                _nowPlayingView.SetTranslationState(_showTranslation);
                _nowPlayingView.SetLyricMatchedState(true);
                UpdateTranslationButtonHighlight();
                UpdateLyricMatchButtonHighlight();
                RefreshLyricListView();
                _controlBar?.UpdateTranslationAvailability(hasTrans);
                string reason = needLyrics ? "歌词" : "双语翻译";
                _controlBar?.UpdateStatus($"[歌词] 已自动匹配在线{reason}: {bestMatch.Title} - {bestMatch.Artist}");
            });

            // 回填匹配曲目的 AlbumMid
            if (!string.IsNullOrWhiteSpace(bestMatch.AlbumMid))
            {
                song.AlbumMid = bestMatch.AlbumMid;
            }

            // 若当前无有效封面且匹配曲目拥有 AlbumMid，自动拉取超清封面并广播
            if (string.IsNullOrEmpty(_currentCoverFilePath) && !string.IsNullOrWhiteSpace(bestMatch.AlbumMid))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cov = await TerminalImageHelper.EnsureAlbumCoverAsync(bestMatch.AlbumMid).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(cov) && _activeSong != null && GetSongIdentityKey(_activeSong) == songKey)
                        {
                            _currentCoverFilePath = cov;
                            _mprisService.UpdateCover(cov);
                            Application.Invoke(() =>
                            {
                                _nowPlayingView.UpdateCover(cov);
                            });
                        }
                    }
                    catch { }
                });
            }

            // 若 WebDAV 歌曲当前歌手未知，同步补全歌手信息
            if (song.IsWebDav && (string.IsNullOrWhiteSpace(song.Artist) || song.Artist == "未知歌手") && !string.IsNullOrWhiteSpace(bestMatch.Artist))
            {
                var server = WebDavService.GetActiveServer();
                if (server != null && !string.IsNullOrEmpty(song.WebDavHref) && !string.IsNullOrEmpty(playUrl))
                {
                    WebDavService.TryUpdateCacheMetadata(server, song.WebDavHref, playUrl, song.Title, bestMatch.Artist, bestMatch.Album);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Playback", $"AutoMatchLyricAsync error for {song.Title}: {ex.Message}");
        }
    }

    /// <summary>
    /// [Feat-03] 本地与 WebDAV 在线歌词智能匹配与升级（Y 键/大界面按钮触发，支持可逆撤销恢复）
    /// </summary>
    private async Task MatchOrRestoreLyricAsync()
    {
        var song = _activeSong;
        if (song == null || (!song.IsLocal && !song.IsWebDav))
        {
            _controlBar?.UpdateStatus("[歌词] 仅支持对本地音乐或 WebDAV 私有云歌曲匹配在线歌词");
            return;
        }

        var songKey = GetSongIdentityKey(song);

        // 1. 若当前已处于在线匹配状态，则执行撤销操作：恢复内嵌原始歌词并删除本地生成的 .lrc
        if (s_matchedOnlineSongKeys.Remove(songKey))
        {
            // 删除本次在线匹配生成的 .lrc 覆盖文件
            string? lrcPath = null;
            if (song.IsLocal && !string.IsNullOrEmpty(song.LocalFilePath))
            {
                lrcPath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
            }
            else if (song.IsWebDav)
            {
                var server = WebDavService.GetActiveServer();
                if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
                {
                    lrcPath = WebDavService.GetLocalLrcCachePath(server, song.WebDavHref);
                }
                else if (!string.IsNullOrEmpty(_currentPlayUrl) && File.Exists(_currentPlayUrl))
                {
                    lrcPath = Path.ChangeExtension(_currentPlayUrl, ".lrc");
                }
                else if (!string.IsNullOrEmpty(song.LocalFilePath))
                {
                    lrcPath = Path.ChangeExtension(song.LocalFilePath, ".lrc");
                }
            }

            if (!string.IsNullOrEmpty(lrcPath) && File.Exists(lrcPath))
            {
                try
                {
                    File.Delete(lrcPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainWindow.Playback", $"Failed to delete lrc file on rollback: {ex.Message}");
                }
            }

            // 恢复歌词：优先从内存备份恢复，其次尝试提取原始音频内嵌歌词
            List<LyricLine> restoredLyrics = [];
            lock (s_originalLyricsBackup)
            {
                if (s_originalLyricsBackup.TryGetValue(songKey, out var backup) && backup.Count > 0)
                {
                    restoredLyrics = backup;
                }
            }

            if (restoredLyrics.Count == 0)
            {
                restoredLyrics = await LocalMusicService.GetEmbeddedLyricsAsync(song, _currentPlayUrl);
            }

            _currentLyrics.Clear();
            _currentLyrics.AddRange(restoredLyrics);
            _player.UpdateCurrentLyrics(restoredLyrics);
            var hasTrans = LyricParser.HasTranslation(restoredLyrics) && LyricParser.NeedsTranslation(restoredLyrics);
            _showTranslation = hasTrans;

            Application.Invoke(() =>
            {
                _hasTranslation = hasTrans;
                _nowPlayingView.SetLyrics(restoredLyrics, _showTranslation);
                _nowPlayingView.SetTranslationState(_showTranslation);
                _nowPlayingView.SetLyricMatchedState(false);
                UpdateTranslationButtonHighlight();
                UpdateLyricMatchButtonHighlight();
                RefreshLyricListView();
                _controlBar?.UpdateTranslationAvailability(hasTrans);
                _controlBar?.UpdateStatus($"[歌词] 已撤销在线匹配，恢复内嵌原始歌词 ({restoredLyrics.Count} 行)");
            });
            return;
        }

        // 2. 首次按 Y 发起在线检索匹配
        lock (s_originalLyricsBackup)
        {
            s_originalLyricsBackup[songKey] = _currentLyrics.ToList();
        }

        _controlBar?.UpdateStatus($"[歌词] 正在通过声学指纹切片匹配官方歌词...");

        try
        {
            var matchResult = await LocalLyricAutoMatcher.MatchLyricsAsync(song, _currentPlayUrl, _currentLyrics, forceMatch: true);
            if (!matchResult.HasValue)
            {
                _controlBar?.UpdateStatus($"[歌词] 匹配失败: 声学指纹未命中官方曲目");
                return;
            }

            var (bestMatch, onlineLyrics) = matchResult.Value;

            if (!string.IsNullOrWhiteSpace(bestMatch.AlbumMid))
            {
                song.AlbumMid = bestMatch.AlbumMid;
            }

            SaveMatchedLrcToDisk(song, _currentPlayUrl, onlineLyrics);

            s_matchedOnlineSongKeys.Add(songKey);
            _currentLyrics.Clear();
            _currentLyrics.AddRange(onlineLyrics);
            _player.UpdateCurrentLyrics(onlineLyrics);

            var hasTrans = LyricParser.HasTranslation(onlineLyrics) && LyricParser.NeedsTranslation(onlineLyrics);
            _showTranslation = hasTrans;
            Application.Invoke(() =>
            {
                _hasTranslation = hasTrans;
                _nowPlayingView.SetLyrics(onlineLyrics, _showTranslation);
                _nowPlayingView.SetTranslationState(_showTranslation);
                _nowPlayingView.SetLyricMatchedState(true);
                UpdateTranslationButtonHighlight();
                UpdateLyricMatchButtonHighlight();
                RefreshLyricListView();
                _controlBar?.UpdateTranslationAvailability(hasTrans);

                _controlBar?.UpdateStatus($"[歌词] 匹配成功: {bestMatch.Title} - {bestMatch.Artist}，再次按 Y 可撤销");
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow.Playback", $"Match lyric failed: {ex.Message}");
            _controlBar?.UpdateStatus($"[歌词] 声学匹配异常: {ex.Message}");
        }
    }
}
