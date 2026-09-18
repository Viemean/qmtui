using System.Collections.ObjectModel;
using System.Diagnostics;
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

public sealed class LoginDialog : Dialog
{
    private readonly Action _onLoginSuccess;
    private readonly CancellationTokenSource _cts = new();
    private LoginHttpServer? _httpServer;
    private LoginService.QrLoginType _loginType = LoginService.QrLoginType.Qq;
    private object? _uiTimerToken;

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Color.White, Color.None)
    };

    // 1. 网页登录容器 (默认首选)
    private readonly View _webContainer;
    private readonly Label _webLanUrlLabel;
    private readonly Label _webLocalUrlLabel;
    private readonly Label _webStatusLabel;
    private readonly Button _openBrowserBtn;

    // 2. 终端字符扫码容器
    private readonly View _qrContainer;
    private readonly Label _qrStatusLabel;
    private readonly Label _qrTipLabel;
    private readonly Label _providerLabel;
    private readonly ListView _qrView;

    public LoginDialog(Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;

        Title = "账号扫码登录";
        Width = 74;
        Height = 32;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        // 顶部切换按钮
        var webTabBtn = new Button
        {
            Text = "网页登录 (W)",
            X = 2,
            Y = 0
        };

        var qrTabBtn = new Button
        {
            Text = "终端扫码 (T)",
            X = Pos.Right(webTabBtn) + 2,
            Y = 0
        };

        var qqBtn = new Button
        {
            Text = "QQ (1)",
            X = 2,
            Y = 2
        };

        var weChatBtn = new Button
        {
            Text = "微信 (2)",
            X = Pos.Right(qqBtn) + 2,
            Y = 2
        };

        var appLoginBtn = new Button
        {
            Text = "移动端 (3)",
            X = Pos.Right(weChatBtn) + 2,
            Y = 2
        };

        _providerLabel = new Label
        {
            Text = "当前方式: QQ",
            X = Pos.Right(appLoginBtn) + 2,
            Y = 2
        };

        var logoutBtn = new Button
        {
            Text = "退出账号",
            X = Pos.Right(qrTabBtn) + 2,
            Y = 0
        };

        var closeBtn = new Button
        {
            Text = "关闭 (Esc)",
            X = Pos.AnchorEnd(14),
            Y = 0
        };

        Add(webTabBtn, qrTabBtn, logoutBtn, closeBtn, qqBtn, weChatBtn, appLoginBtn, _providerLabel);

        // ==================== 1. 网页登录容器 (默认激活) ====================
        _webContainer = new View
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = true
        };

        var webTitleLabel = new Label
        {
            Text = "网页扫码登录",
            X = 2,
            Y = 1
        };
        _webContainer.Add(webTitleLabel);

        var webDescLabel = new Label
        {
            Text = "在浏览器中打开以下任一地址完成扫码授权：",
            X = 2,
            Y = 3
        };
        _webContainer.Add(webDescLabel);

        _webLanUrlLabel = new Label
        {
            Text = "局域网地址: 正在获取...",
            X = 2,
            Y = 5
        };
        _webContainer.Add(_webLanUrlLabel);

        _webLocalUrlLabel = new Label
        {
            Text = "本机地址: 正在获取...",
            X = 2,
            Y = 7
        };
        _webContainer.Add(_webLocalUrlLabel);

        _webStatusLabel = new Label
        {
            Text = "状态: 正在初始化二维码...",
            X = 2,
            Y = 10
        };
        _webContainer.Add(_webStatusLabel);

        _openBrowserBtn = new Button
        {
            Text = "打开本机浏览器 (B)",
            X = 2,
            Y = 13
        };
        _openBrowserBtn.Accepting += (s, e) =>
        {
            if (_httpServer != null && _httpServer.IsRunning)
            {
                TryOpenBrowser(_httpServer.LocalUrl);
            }
        };
        _webContainer.Add(_openBrowserBtn);

        var webHelpLabel = new Label
        {
            Text = "提示: 手机或外部设备在浏览器中打开上述地址完成扫码即可，终端会自动同步登录状态。\n如需直接在终端显示二维码，可切换至 [终端扫码]。",
            X = 2,
            Y = 16,
            Width = Dim.Fill(2)
        };
        _webContainer.Add(webHelpLabel);

        Add(_webContainer);

        // ==================== 2. 终端字符扫码容器 ====================
        _qrContainer = new View
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false
        };

        _qrStatusLabel = new Label
        {
            Text = "状态: 正在初始化二维码...",
            X = 2,
            Y = 0
        };
        _qrContainer.Add(_qrStatusLabel);

        _qrView = new ListView
        {
            X = Pos.Center(),
            Y = 1,
            Width = 45,
            Height = 23,
            CanFocus = false
        };
        _qrView.SetScheme(MikuTheme.QrCode);
        _qrContainer.Add(_qrView);

        _qrTipLabel = new Label
        {
            Text = "提示: 手机扫码授权后将自动同步",
            X = Pos.Center(),
            Y = Pos.Bottom(_qrView) + 1,
            Width = Dim.Fill(2),
            TextAlignment = Alignment.Center
        };
        _qrTipLabel.SetScheme(TransparentDialogScheme);
        _qrContainer.Add(_qrTipLabel);

        Add(_qrContainer);

        // ==================== 事件交互处理 ====================
        void SwitchToWebTab()
        {
            _webContainer.Visible = true;
            _qrContainer.Visible = false;
            webTabBtn.SetFocus();
            SetNeedsDraw();
        }

        void SwitchToQrTab()
        {
            _webContainer.Visible = false;
            _qrContainer.Visible = true;
            qrTabBtn.SetFocus();
            SetNeedsDraw();
        }

        webTabBtn.Accepting += (s, e) => SwitchToWebTab();
        qrTabBtn.Accepting += (s, e) => SwitchToQrTab();

        void SelectProvider(LoginService.QrLoginType type)
        {
            if (_loginType == type) return;
            _loginType = type;
            var name = LoginService.GetLoginTypeName(type);
            _providerLabel.Text = $"当前方式: {name}";
            _httpServer?.UpdateLoginType(name);
            _webStatusLabel.Text = $"状态: 正在切换到{name}登录...";
            _qrStatusLabel.Text = $"状态: 正在切换到{name}登录...";
            RequestRefresh();
            SetNeedsDraw();
        }

        qqBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.Qq);
        weChatBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.WeChat);
        appLoginBtn.Accepting += (s, e) => SelectProvider(LoginService.QrLoginType.OfficialApp);

        logoutBtn.Accepting += (s, e) =>
        {
            LoginService.Logout();
            _webStatusLabel.Text = "状态: 已退出登录";
            _qrStatusLabel.Text = "状态: 已退出登录";
            _httpServer?.UpdateStatus("已退出登录");
            _onLoginSuccess?.Invoke();
        };

        closeBtn.Accepting += (s, e) => CloseSelf();

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k.AsRune.Value == 'q' || k.AsRune.Value == 'Q')
            {
                k.Handled = true;
                CloseSelf();
                return;
            }

            char c = char.ToUpperInvariant((char)k.AsRune.Value);
            if (c == 'W')
            {
                k.Handled = true;
                SwitchToWebTab();
                return;
            }
            if (c == 'T')
            {
                k.Handled = true;
                SwitchToQrTab();
                return;
            }
            if (c == '1')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.Qq);
                return;
            }
            if (c == '2')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.WeChat);
                return;
            }
            if (c == '3')
            {
                k.Handled = true;
                SelectProvider(LoginService.QrLoginType.OfficialApp);
                return;
            }
            if (c == 'R')
            {
                k.Handled = true;
                RequestRefresh();
                return;
            }
            if (c == 'B' && _httpServer != null && _httpServer.IsRunning)
            {
                k.Handled = true;
                TryOpenBrowser(_httpServer.LocalUrl);
                return;
            }
        };

        // 启动网络扫码服务与轮询
        StartQrLoginFlow();
    }

    private volatile bool _refreshRequested;

    private void RequestRefresh()
    {
        _refreshRequested = true;
    }

    private static void PostToMainThread(Action action)
    {
        _ = Task.Run(() =>
        {
            try { Application.Invoke(action); } catch { }
        });
    }

    private void StartQrLoginFlow()
    {
        _httpServer ??= new LoginHttpServer();
        _httpServer.RefreshRequested += () =>
        {
            RequestRefresh();
            return Task.CompletedTask;
        };
        _httpServer.Start(null);

        // 主线程同步更新地址文本，避免依赖主事件循环排队延迟
        _webLanUrlLabel.Text = $"局域网地址: {_httpServer.LanUrl}";
        _webLocalUrlLabel.Text = $"本机地址: {_httpServer.LocalUrl}";

        // 注册轻量定时器驱动模态循环唤醒，保证后台状态和二维码平滑重绘
        _uiTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            if (_cts.IsCancellationRequested) return false;
            SetNeedsDraw();
            return true;
        });

        Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    _refreshRequested = false;

                    var selectedType = _loginType;
                    var typeName = LoginService.GetLoginTypeName(selectedType);

                    _httpServer?.UpdateLoginType(typeName);
                    _httpServer?.UpdateStatus("正在获取二维码...");

                    PostToMainThread(() =>
                    {
                        _webStatusLabel.Text = "状态: 正在拉取二维码...";
                        _qrStatusLabel.Text = "状态: 正在拉取二维码...";
                    });

                    var qr = await LoginService.FetchQrCodeAsync(selectedType, _cts.Token).ConfigureAwait(false);
                    if (qr == null)
                    {
                        _httpServer?.UpdateStatus("二维码生成失败，请点击刷新重试");
                        PostToMainThread(() =>
                        {
                            _webStatusLabel.Text = "状态: 二维码生成失败，按 R 重试";
                            _qrStatusLabel.Text = "状态: 二维码生成失败，按 R 重试";
                        });

                        for (int i = 0; i < 15 && !_refreshRequested && !_cts.Token.IsCancellationRequested; i++)
                        {
                            try { await Task.Delay(200, _cts.Token).ConfigureAwait(false); } catch { break; }
                        }
                        continue;
                    }

                    _httpServer?.UpdateQrCode(qr.ImageBytes, qr.MimeType);
                    _httpServer?.UpdateStatus($"等待使用{typeName}扫码...");

                    PostToMainThread(() =>
                    {
                        if (_httpServer != null)
                        {
                            _webLanUrlLabel.Text = $"局域网地址: {_httpServer.LanUrl}";
                            _webLocalUrlLabel.Text = $"本机地址: {_httpServer.LocalUrl}";
                        }
                        _webStatusLabel.Text = $"状态: 等待使用{typeName}扫码...";
                        _qrStatusLabel.Text = $"状态: 等待使用{typeName}扫码...";
                        if (qr.AsciiLines.Count > 0)
                        {
                            _qrView.Width = qr.AsciiLines[0].Length;
                            _qrView.Height = qr.AsciiLines.Count;
                        }
                        _qrView.SetSource(new ObservableCollection<string>(qr.AsciiLines));
                        _qrTipLabel.Text = $"使用{typeName}扫码，或浏览器打开: {_httpServer?.LanUrl} (按 R 刷新)";
                    });

                    if (selectedType == LoginService.QrLoginType.OfficialApp)
                    {
                        var status = await LoginService.WaitForOfficialAppQrLoginAsync(qr, next =>
                        {
                            _httpServer?.UpdateStatus(next.Message);
                            PostToMainThread(() =>
                            {
                                _webStatusLabel.Text = $"状态: {next.Message}";
                                _qrStatusLabel.Text = $"状态: {next.Message}";
                            });
                        }, _cts.Token).ConfigureAwait(false);

                        if (status.Event == LoginService.QrLoginEvent.Done)
                        {
                            _httpServer?.UpdateStatus($"登录成功 [{UserSession.Current.Nick}]", isSuccess: true, nick: UserSession.Current.Nick);
                            PostToMainThread(() =>
                            {
                                _onLoginSuccess?.Invoke();
                                CloseSelf();
                            });
                            return;
                        }
                        if (status.Event == LoginService.QrLoginEvent.Expired) continue;
                        while (!_cts.Token.IsCancellationRequested && !_refreshRequested && selectedType == _loginType)
                        {
                            try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch { break; }
                        }
                        continue;
                    }

                    while (!_cts.Token.IsCancellationRequested && !_refreshRequested && selectedType == _loginType)
                    {
                        try { await Task.Delay(1500, _cts.Token).ConfigureAwait(false); } catch { break; }
                        if (_refreshRequested || selectedType != _loginType) break;

                        var status = await LoginService.PollQrStatusAsync(qr, _cts.Token).ConfigureAwait(false);
                        if (status.Event == LoginService.QrLoginEvent.Done)
                        {
                            _httpServer?.UpdateStatus($"登录成功 [{UserSession.Current.Nick}]", isSuccess: true, nick: UserSession.Current.Nick);
                            PostToMainThread(() =>
                            {
                                _webStatusLabel.Text = $"状态: 登录成功 [{UserSession.Current.Nick}]";
                                _qrStatusLabel.Text = $"状态: 登录成功 [{UserSession.Current.Nick}]";
                                _onLoginSuccess?.Invoke();
                            });
                            try { await Task.Delay(1500, _cts.Token).ConfigureAwait(false); } catch { }
                            _httpServer?.Stop();
                            PostToMainThread(CloseSelf);
                            return;
                        }

                        if (status.Event == LoginService.QrLoginEvent.Expired)
                        {
                            _httpServer?.UpdateStatus("二维码已失效，正在自动换新...");
                            PostToMainThread(() =>
                            {
                                _webStatusLabel.Text = "状态: 二维码已失效，正在自动换新...";
                                _qrStatusLabel.Text = "状态: 二维码已失效，正在自动换新...";
                            });
                            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch { }
                            break;
                        }

                        _httpServer?.UpdateStatus(status.Message);
                        PostToMainThread(() =>
                        {
                            _webStatusLabel.Text = $"状态: {status.Message}";
                            _qrStatusLabel.Text = $"状态: {status.Message}";
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Error("LoginDialog", "Background login task error", ex);
            }
        }, _cts.Token);
    }

    private static void TryOpenBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore headless or browser missing error
            }
        }
    }

    public void CloseSelf()
    {
        try
        {
            _httpServer?.Dispose();
            _httpServer = null;
        }
        catch
        {
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        if (_uiTimerToken != null)
        {
            try { Application.RemoveTimeout(_uiTimerToken); } catch { }
            _uiTimerToken = null;
        }

        Application.RequestStop(this);
    }

    protected override bool OnAccepting(CommandEventArgs? args)
    {
        // 阻断基类 Dialog 默认将内部任意 Button 的 Accepting 冒泡作为 RequestStop() 的行为。
        // LoginDialog 的退出由 closeBtn 与 Esc 快捷键显式调用 CloseSelf() 负责。
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_uiTimerToken != null)
            {
                try { Application.RemoveTimeout(_uiTimerToken); } catch { }
                _uiTimerToken = null;
            }

            try
            {
                _httpServer?.Dispose();
                _httpServer = null;
            }
            catch {}

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch {}
        }
        base.Dispose(disposing);
    }
}
