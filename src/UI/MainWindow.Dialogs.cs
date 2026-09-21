using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;
using QmTui.Player;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public sealed partial class MainWindow
{
    private bool _isLoginDialogOpen;

    private void ShowLoginDialog()
    {
        if (_isLoginDialogOpen) return;
        _isLoginDialogOpen = true;
        try
        {
            var dlg = new LoginDialog(() =>
            {
                Application.Invoke(UpdateTopRightButtonsLayout);
            });
            RunModalDialog(dlg);
            UpdateTopRightButtonsLayout();
        }
        finally
        {
            _isLoginDialogOpen = false;
        }
    }

    private void ShowQualityDialog()
    {
        if (_activeSong != null && (_activeSong.IsLocal || _activeSong.IsWebDav))
        {
            ShowLocalOrWebDavAudioInfoDialog(_activeSong);
            return;
        }

        var dlg = new QualityDialog(_activeSong, _preferredQualityTier, (newTier, option) =>
        {
            _preferredQualityTier = newTier;
            UserSession.Current.PreferredQuality = AudioQualityHelper.GetBadge(newTier);
            UserSession.Current.Save();

            if (_activeSong == null)
            {
                _actualQualityTier = newTier;
                Application.Invoke(() => _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(newTier)));
                return;
            }

            var targetSong = _activeSong;

            // 无论当前是播放还是暂停中，都不打断当前曲目与进度，在后台异步预加载新音质
            _ = Task.Run(async () =>
            {
                Application.Invoke(() =>
                {
                    _controlBar.UpdateStatus($"[正在加载] 正在获取 [{AudioQualityHelper.GetBadge(newTier)}] 音质...");
                });

                string? playUrl = option?.PlayUrl;
                string qualityName = option?.Badge ?? AudioQualityHelper.GetBadge(newTier);
                AudioQualityTier actualTier = option?.Tier ?? newTier;

                // 优先检索本地磁盘是否已缓存目标音质
                if (string.IsNullOrEmpty(playUrl))
                {
                    var localCached = AudioCacheService.GetCachedAudioPath(targetSong.Mid, newTier);
                    if (!string.IsNullOrEmpty(localCached) && File.Exists(localCached))
                    {
                        playUrl = localCached;
                        qualityName = AudioQualityHelper.GetBadge(newTier);
                        actualTier = newTier;
                    }
                }

                if (string.IsNullOrEmpty(playUrl))
                {
                    var res = await MusicApi.GetPlayUrlForTierAsync(targetSong.Mid, targetSong.EffectiveMediaMid, newTier);
                    playUrl = res.Url;
                    qualityName = res.Quality;
                    actualTier = res.Tier;
                }

                // 若指定档位依然未获取到直链，则优先探测本地降级缓存，再走网络梯度降级
                if (string.IsNullOrEmpty(playUrl))
                {
                    foreach (var fbTier in AudioQualityHelper.GetFallbackTiers(newTier))
                    {
                        var localFallback = AudioCacheService.GetCachedAudioPath(targetSong.Mid, fbTier);
                        if (!string.IsNullOrEmpty(localFallback) && File.Exists(localFallback))
                        {
                            playUrl = localFallback;
                            qualityName = AudioQualityHelper.GetBadge(fbTier);
                            actualTier = fbTier;
                            break;
                        }

                        var res = await MusicApi.GetPlayUrlForTierAsync(targetSong.Mid, targetSong.EffectiveMediaMid, fbTier);
                        if (!string.IsNullOrEmpty(res.Url))
                        {
                            playUrl = res.Url;
                            qualityName = res.Quality;
                            actualTier = res.Tier;
                            break;
                        }
                    }
                }

                // 若完全无法获取任何可用音源，保持当前播放不受影响
                if (string.IsNullOrEmpty(playUrl))
                {
                    Application.Invoke(() =>
                    {
                        _controlBar.UpdateStatus($"[保持播放] 未能获取 [{AudioQualityHelper.GetBadge(newTier)}] 可用音源，保持当前音质播放");
                    });
                    return;
                }

                // 准备切换：检查当前播放曲目是否已被用户切换
                if (_activeSong == null || _activeSong.Mid != targetSong.Mid)
                {
                    AppLogger.Info("QualityChange", "Song changed during quality background load, switch aborted.");
                    return;
                }

                // 在即将切入的瞬间精确采样瞬时进度与播放状态，实现平滑过渡
                double currentPos = _player.CurrentPositionSeconds;
                bool wasPlaying = _player.IsPlaying;

                targetSong.Quality = qualityName;
                _actualQualityTier = actualTier;

                if (!_isTuiAudioDisabled)
                {
                    await _player.PlayAsync(playUrl, targetSong.Duration, currentPos);
                    if (!wasPlaying)
                    {
                        await _player.TogglePauseAsync();
                    }
                }

                _lastResolvedPlayUrl = playUrl;

                // 检查切换后的新音质是否已在本地持久化缓存，若未缓存则重置门限并在满足条件时立即补录
                var existingCache = AudioCacheService.GetCachedAudioPath(targetSong.Mid, actualTier);
                if (!string.IsNullOrEmpty(existingCache) && File.Exists(existingCache))
                {
                    _hasTriggeredCacheForCurrentSong = true;
                }
                else
                {
                    _hasTriggeredCacheForCurrentSong = false;
                    if (_accumulatedPlaySeconds >= 30.0 || (targetSong.Duration > 0 && targetSong.Duration < 30.0 && _accumulatedPlaySeconds >= targetSong.Duration * 0.8))
                    {
                        _hasTriggeredCacheForCurrentSong = true;
                        TriggerBackgroundCacheForSong(targetSong, _currentCacheSessionId, _cachingCts?.Token ?? CancellationToken.None);
                    }
                }

                Application.Invoke(() =>
                {
                    _controlBar.UpdateQuality(AudioQualityHelper.GetBadge(actualTier));
                    UpdatePlayerStatus();

                    if (actualTier == newTier)
                    {
                        _controlBar.UpdateStatus($"[音质切换] 已切换至 [{qualityName}]");
                    }
                    else
                    {
                        _controlBar.UpdateStatus($"[音质降级] 已切换至 [{qualityName}]");
                    }

                    if (_currentViewMode == ViewMode.GuessRecommend && _activeSong != null)
                    {
                        _songListView.SetRadioCard(_activeSong, AudioQualityHelper.GetBadge(actualTier), _radioService.PlayedCount);
                    }
                });

                if (_standaloneWebServer != null && _standaloneWebServer.IsRunning)
                {
                    _standaloneWebServer.CurrentPlayUrl = playUrl;
                    _standaloneWebServer.ActualQualityTier = actualTier;
                    _standaloneWebServer.PreferredQualityTier = _preferredQualityTier;
                    _standaloneWebServer.CurrentPositionSeconds = currentPos;
                    _standaloneWebServer.BroadcastState(wasPlaying ? "play" : "pause");
                }
            });
        });
        RunModalDialog(dlg);
    }

    private void ShowDownloadDialog()
    {
        var targetSong = _activeSong ?? _songListView.GetSelectedSong();
        if (targetSong == null)
        {
            _controlBar.UpdateStatus("[转存提示] 当前暂无播放或选中的曲目");
            return;
        }

        if (targetSong.IsLocal)
        {
            _controlBar.UpdateStatus("[本地音乐] 当前曲目已在本地磁盘，无需转存");
            return;
        }

        var dlg = new QualityDialog(targetSong, _preferredQualityTier, (newTier, option) =>
        {
            _ = Task.Run(() => DownloadSongAsync(targetSong, newTier, option));
        }, customTitle: "缓存转存");
        RunModalDialog(dlg);
    }

    private async Task DownloadSongAsync(Song song, AudioQualityTier tier, QualityOption? option)
    {
        try
        {
            string tierBadge = option?.Badge ?? AudioQualityHelper.GetBadge(tier);
            Application.Invoke(() => _controlBar.UpdateStatus($"[正在解析] 正在解析《{song.Title}》的 {tierBadge} 音频链接..."));

            string? playUrl = option?.PlayUrl;
            AudioQualityTier actualTier = option?.Tier ?? tier;

            if (string.IsNullOrEmpty(playUrl))
            {
                var res = await MusicApi.GetPlayUrlForTierAsync(song.Mid, song.EffectiveMediaMid, tier);
                playUrl = res.Url;
                actualTier = res.Tier;
            }

            if (string.IsNullOrEmpty(playUrl))
            {
                Application.Invoke(() => _controlBar.UpdateStatus($"[转存失败] 《{song.Title}》({tierBadge}) 未能获取有效链接 (可能需要 VIP)"));
                return;
            }

            string musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(musicDir) || !Directory.Exists(musicDir))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                musicDir = Path.Combine(home, "Music");
                if (!Directory.Exists(musicDir))
                {
                    string zhMusic = Path.Combine(home, "音乐");
                    musicDir = Directory.Exists(zhMusic) ? zhMusic : Path.Combine(home, "Music");
                }
            }
            string targetDir = Path.Combine(musicDir, "qmtui_export");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string ext = AudioQualityHelper.GetExtension(actualTier);
            string safeArtist = string.Join("_", song.Artist.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string safeTitle = string.Join("_", song.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            string fileName = $"{safeArtist} - {safeTitle}{ext}";
            string targetPath = Path.Combine(targetDir, fileName);

            Application.Invoke(() => _controlBar.UpdateStatus($"[正在转存] {fileName} ..."));

            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");
            client.DefaultRequestHeaders.Add("Referer", "https://y.qq.com/");

            using var resp = await client.GetAsync(playUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            if (resp.IsSuccessStatusCode)
            {
                await using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                Application.Invoke(() => _controlBar.UpdateStatus($"[正在注入] 正在为《{song.Title}》写入封面与歌词..."));
                await AudioExportService.InjectMetadataAndAssetsAsync(targetPath, song).ConfigureAwait(false);

                Application.Invoke(() => _controlBar.UpdateStatus($"[转存完成] 已保存至: {fileName} (含内嵌封面与歌词)"));
            }
            else
            {
                Application.Invoke(() => _controlBar.UpdateStatus($"[转存失败] HTTP 响应码: {resp.StatusCode}"));
            }
        }
        catch (Exception ex)
        {
            Application.Invoke(() => _controlBar.UpdateStatus($"[转存异常] {ex.Message}"));
        }
    }

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Terminal.Gui.Drawing.Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, Terminal.Gui.Drawing.Color.None)
    };

    private void ShowExitConfirmDialog()
    {
        bool confirmed = false;
        int dlgW = 46;
        int dlgH = 8;
        var dlg = new Dialog
        {
            Title = "退出确认",
            Width = dlgW,
            Height = dlgH,
            X = Pos.Center(),
            Y = Pos.Center()
        };

        dlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = "确定要退出播放器吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(TransparentDialogScheme);
        dlg.Add(msg);

        // 底部居中对称摆放按钮，参考多歌手选择弹窗规范，拉开充裕间距防止字符挤压
        var yesBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        yesBtn.SetScheme(TransparentDialogScheme);
        yesBtn.Accepting += (s, e) =>
        {
            confirmed = true;
            Application.RequestStop(dlg);
        };

        var noBtn = new Button
        {
            Text = "取消 (Esc)",
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        noBtn.SetScheme(TransparentDialogScheme);
        noBtn.Accepting += (s, e) =>
        {
            Application.RequestStop(dlg);
        };

        dlg.Add(yesBtn, noBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Y || k.AsRune.Value == 'y' || k.AsRune.Value == 'Y' ||
                k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                confirmed = true;
                Application.RequestStop(dlg);
            }
            else if (k == Key.N || k.AsRune.Value == 'n' || k.AsRune.Value == 'N' ||
                     k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        MikuTheme.ApplyTo(dlg, TransparentDialogScheme);
        yesBtn.SetFocus();

        RunModalDialog(dlg);

        if (confirmed)
        {
            UserSession.Current.Save();
            try { _connectMdns?.Dispose(); _connectMdns = null; } catch {}
            try { _connectServer?.Dispose(); _connectServer = null; } catch {}
            _mprisService.Dispose();
            _player.Dispose();
            _standaloneWebServer?.Dispose();
            Application.RequestStop();
        }
    }

    private bool _isAudioRecognitionActive;

    private void ShowAudioRecognitionDialog()
    {
        if (_isAudioRecognitionActive) return;
        _isAudioRecognitionActive = true;

        try
        {
            Song? selectedSong = null;
            using var dlg = new AudioRecognitionDialog(song =>
            {
                selectedSong = song;
            }, inLyricArea: _isNowPlayingViewActive);

            RunModalDialog(dlg);

            // 等待 Dialog 完全退栈并从界面销毁后再触发播放与切换沉浸界面
            if (selectedSong != null)
            {
                _ = Task.Run(async () =>
                {
                    await PlaySongAsync(selectedSong, 0);
                    Application.Invoke(OpenNowPlayingView);
                });
            }
        }
        finally
        {
            _isAudioRecognitionActive = false;
        }
    }

    private bool _isWebDialogOpen;

    private void ShowWebStatusDialog(string url)
    {
        if (_isWebDialogOpen) return;
        _isWebDialogOpen = true;
        try
        {
            var dlg = new Dialog
            {
                Title = "Web 协同服务配置",
                Width = 64,
                Height = 11
            };

        string GetAddressText(int port)
        {
            var lanIp = WebPlaybackServer.GetLocalLanIp();
            return lanIp != null
                ? $"服务地址: http://0.0.0.0:{port}/ ({lanIp})"
                : $"服务地址: http://0.0.0.0:{port}/";
        }

        var addrLabel = new Label
        {
            Text = GetAddressText(_webServerPort),
            X = Pos.Center(),
            Y = 1,
            TextAlignment = Alignment.Center
        };

        var portLabel = new Label
        {
            Text = "服务端口:",
            X = 3,
            Y = 3
        };

        var portField = new TextField
        {
            Text = _webServerPort.ToString(),
            X = 13,
            Y = 3,
            Width = 9
        };
        portField.SetScheme(TransparentDialogScheme);
        portField.EnableMiddleClickPaste();

        var portHintLabel = new Label
        {
            Text = "(按回车直接应用)",
            X = 24,
            Y = 3
        };

        var tuiAudioLabel = new Label
        {
            Text = "TUI音频:",
            X = 3,
            Y = 5
        };

        if (!Utils.AudioDeviceHelper.HasAudioOutputDevice() && !_isTuiAudioDisabled)
        {
            _ = SetTuiAudioDisabledAsync(true);
        }

        var tuiAudioBtn = new Button
        {
            Text = _isTuiAudioDisabled ? "已禁用 (清理进程，仅Web播放) [T]" : "已开启 (本地硬件输出) [T]",
            X = 13,
            Y = 5,
            ShadowStyle = ShadowStyles.None
        };
        tuiAudioBtn.SetScheme(TransparentDialogScheme);

        void UpdateTuiAudioBtn()
        {
            tuiAudioBtn.Text = _isTuiAudioDisabled ? "已禁用 (清理进程，仅Web播放) [T]" : "已开启 (本地硬件输出) [T]";
        }

        void DoToggleTuiAudio()
        {
            if (_isTuiAudioDisabled && !Utils.AudioDeviceHelper.HasAudioOutputDevice())
            {
                _controlBar.UpdateStatus("未检测到本地可用音频输出通道，无法开启本地硬件播放");
                return;
            }
            _ = SetTuiAudioDisabledAsync(!_isTuiAudioDisabled);
            UpdateTuiAudioBtn();
        }

        tuiAudioBtn.Accepting += (s, e) => DoToggleTuiAudio();

        void DoApplyPort()
        {
            var portStr = portField.Text?.ToString()?.Trim();
            if (int.TryParse(portStr, out int p) && p >= 1024 && p <= 65535)
            {
                if (p != _webServerPort)
                {
                    _webServerPort = p;
                    RestartStandaloneWebServer(p);
                    addrLabel.Text = GetAddressText(_webServerPort);
                    _controlBar.UpdateStatus($"Web服务已迁移至端口: {_webServerPort}");
                }
            }
            else
            {
                _controlBar.UpdateStatus("端口无效，请输入 1024~65535 范围端口");
            }
        }

        portField.Accepting += (s, e) => DoApplyPort();
        portField.KeyDown += (s, k) =>
        {
            if (k == Key.Enter || k.AsRune.Value == '\r' || k.AsRune.Value == '\n')
            {
                k.Handled = true;
                DoApplyPort();
            }
        };

        var openBtn = new Button
        {
            Text = "浏览器打开 (B)",
            X = Pos.Center() - 19,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        openBtn.SetScheme(TransparentDialogScheme);

        void DoOpenBrowser()
        {
            try
            {
                string openTarget = $"http://127.0.0.1:{_webServerPort}/";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = openTarget,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch {}
            Application.RequestStop(dlg);
        }

        openBtn.Accepting += (s, e) => DoOpenBrowser();

        var stopBtn = new Button
        {
            Text = "关闭服务 (S)",
            X = Pos.Center() - 1,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        stopBtn.SetScheme(TransparentDialogScheme);
        if (_player is WebPlayer)
        {
            stopBtn.Enabled = false;
        }

        void DoStopServer()
        {
            if (_player is WebPlayer)
            {
                _controlBar.UpdateStatus("当前运行在 WebPlayer 模式，无法单独关闭服务");
                return;
            }
            StopStandaloneWebServer();
            Application.RequestStop(dlg);
        }

        stopBtn.Accepting += (s, e) => DoStopServer();

        var closeBtn = new Button
        {
            Text = "返回 (Esc)",
            X = Pos.Center() + 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.Accepting += (s, e) => Application.RequestStop(dlg);

        dlg.Add(addrLabel, portLabel, portField, portHintLabel, tuiAudioLabel, tuiAudioBtn, openBtn, stopBtn, closeBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.B || k.AsRune.Value == 'b' || k.AsRune.Value == 'B')
            {
                k.Handled = true;
                DoOpenBrowser();
            }
            else if (k == Key.S || k.AsRune.Value == 's' || k.AsRune.Value == 'S')
            {
                k.Handled = true;
                DoStopServer();
            }
            else if (k == Key.T || k.AsRune.Value == 't' || k.AsRune.Value == 'T')
            {
                k.Handled = true;
                DoToggleTuiAudio();
            }
            else if (k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        MikuTheme.ApplyTo(dlg, TransparentDialogScheme);
        closeBtn.SetFocus();
        RunModalDialog(dlg);
        }
        finally
        {
            _isWebDialogOpen = false;
        }
    }

    private bool _isQueueDrawerOpen;

    private void ShowQueueDrawerDialog()
    {
        if (_isQueueDrawerOpen) return;
        _isQueueDrawerOpen = true;
        try
        {
            using var dlg = new QueueDrawerDialog();
            RunModalDialog(dlg);
            if (dlg.SelectedSongToPlay != null)
            {
                var target = dlg.SelectedSongToPlay;
                _ = Task.Run(async () =>
                {
                    await PlaySongAsync(target);
                });
            }
        }
        finally
        {
            _isQueueDrawerOpen = false;
        }
    }

    private void ShowLocalOrWebDavAudioInfoDialog(Song song)
    {
        var prefix = song.IsWebDav ? "WebDAV" : "本地音乐";
        var tier = AudioQualityHelper.DetermineLocalOrWebDavTier(song.Quality, song.LocalFilePath ?? song.WebDavHref);
        var tierBadge = AudioQualityHelper.GetBadge(tier);

        // 尝试解析音频实际参数
        string? targetPath = song.LocalFilePath;
        if (song.IsWebDav)
        {
            var server = WebDavService.GetActiveServer();
            if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
            {
                var localCache = WebDavService.GetLocalCachePath(server, song.WebDavHref);
                if (File.Exists(localCache) && new FileInfo(localCache).Length > 4096)
                {
                    targetPath = localCache;
                }
            }
        }

        string format = !string.IsNullOrEmpty(targetPath) && Path.HasExtension(targetPath)
            ? Path.GetExtension(targetPath).TrimStart('.').ToUpperInvariant()
            : "音频流";
        string bitrate = "";
        string sampleRate = "";
        string bitDepth = "";
        string channels = "";

        if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
        {
            try
            {
                var track = new ATL.Track(targetPath);
                int resolvedBitrate = track.Bitrate;

                if (song.IsWebDav)
                {
                    var server = WebDavService.GetActiveServer();
                    long serverFileSize = 0;
                    int songDur = song.Duration > 0 ? song.Duration : track.Duration;
                    if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
                    {
                        var sc = WebDavService.FindSongCache(server, song.WebDavHref);
                        if (sc != null && sc.FileSize > 0)
                        {
                            serverFileSize = sc.FileSize;
                            if (songDur <= 0 && sc.Duration > 0) songDur = sc.Duration;
                        }
                    }

                    long localLen = new FileInfo(targetPath).Length;
                    if ((resolvedBitrate < 64 || resolvedBitrate > 10000 || (serverFileSize > 0 && localLen < serverFileSize * 0.9)) && songDur > 0 && serverFileSize > 0)
                    {
                        resolvedBitrate = (int)Math.Round((serverFileSize * 8.0) / songDur / 1000.0);
                    }
                    else if (resolvedBitrate < 64 || resolvedBitrate > 10000)
                    {
                        var extFile = Path.GetExtension(targetPath).ToLowerInvariant();
                        if (extFile is ".flac" or ".ape" or ".wav")
                        {
                            double sr = track.SampleRate > 0 ? track.SampleRate : 44100;
                            int bd = track.BitDepth > 0 ? track.BitDepth : 16;
                            int ch = track.ChannelsArrangement?.NbChannels ?? 2;
                            resolvedBitrate = (int)Math.Round(sr * bd * ch * 0.65 / 1000.0);
                        }
                    }
                }

                if (resolvedBitrate > 0) bitrate = $"{resolvedBitrate} kbps";
                if (track.SampleRate > 0) sampleRate = $"{track.SampleRate / 1000.0:0.#} kHz";
                if (track.BitDepth > 0) bitDepth = $"{track.BitDepth} bit";
                if (track.ChannelsArrangement != null) channels = $"{track.ChannelsArrangement.NbChannels} 声道";
                if (!string.IsNullOrEmpty(track.AudioFormat?.Name)) format = track.AudioFormat.Name;

                var ext = Path.GetExtension(targetPath).ToLowerInvariant();
                string resolvedQuality;
                if (track.BitDepth > 16 || track.SampleRate > 48000 || resolvedBitrate > 2000)
                {
                    resolvedQuality = "Hi-Res";
                    tier = AudioQualityTier.HiRes;
                }
                else if (ext is ".flac" or ".ape" or ".wav" || (track.AudioFormat != null && track.AudioFormat.Name.Contains("Lossless", StringComparison.OrdinalIgnoreCase)))
                {
                    resolvedQuality = "SQ 无损";
                    tier = AudioQualityTier.SQ;
                }
                else if (resolvedBitrate >= 300 || ext is ".m4a" or ".aac" or ".ogg" or ".opus")
                {
                    resolvedQuality = "HQ 高品质";
                    tier = AudioQualityTier.HQ;
                }
                else
                {
                    resolvedQuality = !string.IsNullOrEmpty(song.Quality) ? song.Quality : "标准 128k";
                }

                tierBadge = AudioQualityHelper.GetBadge(tier);
                song.Quality = resolvedQuality;
                _actualQualityTier = tier;
                _controlBar.UpdateQuality(tierBadge);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AudioInfoDialog", $"Failed to read ATL track metadata: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(bitrate))
        {
            bitrate = !string.IsNullOrEmpty(song.Quality) ? song.Quality : "动态码率";
        }

        var statusSummary = string.IsNullOrEmpty(sampleRate)
            ? $"[{prefix}] 档次: {tierBadge} | 规格: {bitrate} (原档不可切换)"
            : $"[{prefix}] 档次: {tierBadge} | {format} | {bitrate} | {sampleRate} (原档不可切换)";
        _controlBar.UpdateStatus(statusSummary);

        int dlgW = 60;
        int dlgH = 12;
        var dlg = new Dialog
        {
            Title = $"{prefix} 音频规格详情",
            Width = dlgW,
            Height = dlgH,
            X = Pos.Center(),
            Y = Pos.Center()
        };

        dlg.SetScheme(TransparentDialogScheme);

        var titleLabel = new Label
        {
            Text = $"曲目: {song.Title} - {song.Artist}",
            X = 2,
            Y = 1,
            Width = Dim.Fill(2)
        };
        titleLabel.SetScheme(TransparentDialogScheme);

        var tierLabel = new Label
        {
            Text = $"音质档次: {FormatTierDisplay(tierBadge, song.Quality)}",
            X = 2,
            Y = 3,
            Width = Dim.Fill(2)
        };
        tierLabel.SetScheme(TransparentDialogScheme);

        var formatLabel = new Label
        {
            Text = $"编码格式: {format}    码率: {bitrate}",
            X = 2,
            Y = 5,
            Width = Dim.Fill(2)
        };
        formatLabel.SetScheme(TransparentDialogScheme);

        var specLabel = new Label
        {
            Text = $"采样配置: {(string.IsNullOrEmpty(sampleRate) ? "原文件采样" : $"{sampleRate} / {bitDepth}")}    {(string.IsNullOrEmpty(channels) ? "" : channels)}",
            X = 2,
            Y = 7,
            Width = Dim.Fill(2)
        };
        specLabel.SetScheme(TransparentDialogScheme);

        // 若为 WebDAV 且未完全缓存，异步读取文件头部解析规格并刷新界面
        if (song.IsWebDav && string.IsNullOrEmpty(targetPath))
        {
            var server = WebDavService.GetActiveServer();
            if (server != null && !string.IsNullOrEmpty(song.WebDavHref))
            {
                _ = Task.Run(async () =>
                {
                    var meta = await WebDavService.TryFetchAudioMetadataInfoAsync(server, song.WebDavHref);
                    if (meta != null)
                    {
                        Application.Invoke(() =>
                        {
                            var newTier = AudioQualityHelper.DetermineLocalOrWebDavTier(meta.QualityBadge, song.WebDavHref);
                            var newBadge = AudioQualityHelper.GetBadge(newTier);
                            song.Quality = meta.QualityBadge;
                            _actualQualityTier = newTier;
                            _controlBar.UpdateQuality(newBadge);

                            tierLabel.Text = $"音质档次: {FormatTierDisplay(newBadge, meta.QualityBadge)}";
                            var bStr = meta.Bitrate > 0 ? $"{meta.Bitrate} kbps" : meta.QualityBadge;
                            formatLabel.Text = $"编码格式: {meta.Format}    码率: {bStr}";
                            var srStr = meta.SampleRate > 0 ? $"{meta.SampleRate / 1000.0:0.#} kHz" : "原文件采样";
                            var bdStr = meta.BitDepth > 0 ? $"{meta.BitDepth} bit" : "";
                            var chStr = meta.Channels > 0 ? $"{meta.Channels} 声道" : "";
                            specLabel.Text = $"采样配置: {(string.IsNullOrEmpty(bdStr) ? srStr : $"{srStr} / {bdStr}")}    {chStr}";
                            _controlBar.UpdateStatus($"[{prefix}] 档次: {newBadge} | {meta.Format} | {bStr} | {srStr} (原档不可切换)");
                            dlg.SetNeedsLayout();
                        });
                    }
                });
            }
        }

        var closeBtn = new Button
        {
            Text = "关闭 (Esc/Enter)",
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.KeyBindings.Remove(Key.Space);
        closeBtn.KeyBindings.Remove(Key.Esc);
        closeBtn.Accepting += (s, e) => Application.RequestStop(dlg);

        dlg.Add(titleLabel, tierLabel, formatLabel, specLabel, closeBtn);

        dlg.KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k == Key.Enter || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                Application.RequestStop(dlg);
            }
        };

        closeBtn.SetFocus();
        RunModalDialog(dlg);
    }

    private static string FormatTierDisplay(string tierBadge, string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
        {
            return $"[{tierBadge}]";
        }

        var clean = quality.Trim();
        if (clean.StartsWith(tierBadge, StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[tierBadge.Length..].TrimStart(' ', '-', '—', '_', '(', ')');
        }
        else if (clean.StartsWith($"[{tierBadge}]", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[($"[{tierBadge}]").Length..].TrimStart(' ', '-', '—', '_', '(', ')');
        }

        return string.IsNullOrWhiteSpace(clean)
            ? $"[{tierBadge}]"
            : $"[{tierBadge}] ({clean})";
    }
}

