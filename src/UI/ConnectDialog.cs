using System.Collections.ObjectModel;
using System.Text.Json;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Connect.Discovery;
using QmTui.Connect.Models;
using QmTui.Connect.Server;
using QmTui.Connect.Storage;
using QmTui.Utils;

namespace QmTui.UI;

public sealed class ConnectDialog : Dialog
{
    private readonly TvConnectServer _server;
    private readonly ConnectStorage _storage;
    private readonly ConnectMdnsService? _mdnsService;

    private readonly ListView _qrView;
    private readonly View _rightPane;
    private readonly Label _pinCodeLabel;
    private readonly Label _addrLabel;
    private readonly Label _statusLabel;
    private readonly Label _tipLabel;
    private readonly Label _pairedLabel;
    private readonly Button _refreshPinBtn;
    private readonly Button _toggleModeBtn;
    private readonly Button _clearDevicesBtn;
    private readonly Button _closeBtn;

    private readonly bool _canSplit;
    private bool _isDownloadMode;
    private const string AppDownloadFallbackUrl = "https://github.com/Viemean/melodist/releases";
    private static (string DownloadUrl, string Tag, string FileName, DateTimeOffset ExpireAt)? s_cachedRelease;
    private static readonly HttpClient s_releaseHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

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

    public ConnectDialog(TvConnectServer server, ConnectStorage storage, ConnectMdnsService? mdnsService = null)
    {
        _server = server;
        _storage = storage;
        _mdnsService = mdnsService;

        int screenCols = Application.Driver?.Cols ?? 80;
        int screenRows = Application.Driver?.Rows ?? 24;

        // 1. 先生成初始配对数据并测算二维码实际矩阵尺寸
        var initialPin = _storage.CurrentPinCode;
        var localIp = ConnectMdnsService.GetBestLocalIpAddress();
        var initialQrLines = GenerateQrLines(initialPin, localIp);

        int qrW = initialQrLines.Count > 0 ? initialQrLines[0].Length : 43;
        int qrH = initialQrLines.Count > 0 ? initialQrLines.Count : 22;

        const int rightPaneMinWidth = 48;
        const int rightPaneMinHeight = 18;

        // 测算左右分栏与上下分栏所需物理空间
        int splitNeededWidth = qrW + rightPaneMinWidth + 6;
        int splitNeededHeight = Math.Max(qrH, rightPaneMinHeight) + 3;

        _canSplit = screenCols >= splitNeededWidth && screenRows >= splitNeededHeight;
        bool canSplit = _canSplit;
        bool canStack = !_canSplit && screenCols >= qrW + 4 && screenRows >= qrH + 8;

        int dlgW;
        int dlgH;

        if (canSplit)
        {
            dlgW = Math.Min(screenCols - 2, splitNeededWidth);
            dlgH = Math.Min(screenRows - 2, splitNeededHeight);
        }
        else if (canStack)
        {
            dlgW = Math.Min(screenCols - 2, qrW + 6);
            dlgH = Math.Min(screenRows - 2, qrH + 8);
        }
        else
        {
            // 终端物理空间不足以容纳大二维码，优雅自适应为纯 PIN 码连接引导
            dlgW = Math.Clamp(screenCols - 4, 44, 56);
            dlgH = Math.Clamp(screenRows - 2, 12, 14);
        }

        Title = "远程控制配对 (Melodist Connect)";
        Width = dlgW;
        Height = dlgH;
        X = Pos.Center();
        Y = Pos.Center();
        CanFocus = true;
        SetScheme(TransparentDialogScheme);

        // 2. 二维码视图配置
        _qrView = new ListView
        {
            Width = qrW,
            Height = qrH,
            CanFocus = false,
            Visible = canSplit || canStack
        };
        _qrView.SetScheme(MikuTheme.QrCode);

        if (canSplit)
        {
            _qrView.X = 1;
            _qrView.Y = 0;
        }
        else if (canStack)
        {
            _qrView.X = Pos.Center();
            _qrView.Y = 0;
        }
        Add(_qrView);

        // 3. 右侧信息容器 (若左右分栏则严格使用 Pos.Right(_qrView) + 2 相对约束，杜绝遮挡)
        _rightPane = new View
        {
            CanFocus = true
        };
        _rightPane.SetScheme(TransparentDialogScheme);

        if (canSplit)
        {
            _rightPane.X = Pos.Right(_qrView) + 2;
            _rightPane.Y = 0;
            _rightPane.Width = Dim.Fill(1);
            _rightPane.Height = Dim.Fill();
        }
        else if (canStack)
        {
            _rightPane.X = 1;
            _rightPane.Y = Pos.Bottom(_qrView) + 1;
            _rightPane.Width = Dim.Fill(1);
            _rightPane.Height = Dim.Fill();
        }
        else
        {
            _rightPane.X = 1;
            _rightPane.Y = 0;
            _rightPane.Width = Dim.Fill(1);
            _rightPane.Height = Dim.Fill();
        }
        Add(_rightPane);

        // 4. 信息与提示 Label
        _pinCodeLabel = new Label
        {
            Text = $"6位 PIN 码:  [ {initialPin} ]",
            X = canStack ? Pos.Center() : 0,
            Y = 0
        };
        _pinCodeLabel.SetScheme(TransparentDialogScheme);
        _rightPane.Add(_pinCodeLabel);

        _addrLabel = new Label
        {
            Text = $"服务地址:   {localIp}:{_server.ActualPort}",
            X = canStack ? Pos.Center() : 0,
            Y = canStack ? 2 : 2
        };
        _addrLabel.SetScheme(TransparentDialogScheme);
        _rightPane.Add(_addrLabel);

        _statusLabel = new Label
        {
            Text = $"在线连接:   {_server.ConnectedCount} 个设备",
            X = 0,
            Y = 4,
            Visible = !canStack
        };
        _statusLabel.SetScheme(TransparentDialogScheme);
        _rightPane.Add(_statusLabel);

        _tipLabel = new Label
        {
            Text = canSplit
                ? "连接说明:\n1. 手机打开 Melodist Mobile\n2. 扫描左侧二维码，或局域网\n   发现后输入上方 6位 PIN 码"
                : (canStack
                    ? "请用手机扫码，或输入 6位 PIN 码"
                    : "连接说明:\n1. 手机打开 Melodist Mobile\n2. 终端空间较小，请在手机发现\n   页直接输入上方 6位 PIN 码连接"),
            X = 0,
            Y = canStack ? 4 : 6
        };
        _tipLabel.SetScheme(TransparentDialogScheme);
        _rightPane.Add(_tipLabel);

        _pairedLabel = new Label
        {
            Text = $"已配对信任: {_storage.PairedDevices.Count} 个设备",
            X = 0,
            Y = 11,
            Visible = _canSplit
        };
        _pairedLabel.SetScheme(TransparentDialogScheme);
        _rightPane.Add(_pairedLabel);

        // 5. 按钮控制栏（采用双行排布，彻底消除窄屏或长文字下右侧关闭按钮被截断问题）
        _refreshPinBtn = new Button
        {
            Text = "刷新 PIN (R)",
            X = canStack ? Pos.Center() - 17 : 0,
            Y = Pos.AnchorEnd(2)
        };
        _refreshPinBtn.SetScheme(TransparentDialogScheme);
        _refreshPinBtn.Accepting += (s, e) => RefreshPairingData();
        _rightPane.Add(_refreshPinBtn);

        _toggleModeBtn = new Button
        {
            Text = "App下载 (D)",
            X = canStack ? Pos.Center() + 1 : Pos.Right(_refreshPinBtn) + 1,
            Y = Pos.AnchorEnd(2)
        };
        _toggleModeBtn.SetScheme(TransparentDialogScheme);
        _toggleModeBtn.Accepting += (s, e) => ToggleQrMode();
        _rightPane.Add(_toggleModeBtn);

        _clearDevicesBtn = new Button
        {
            Text = "清空信任 (C)",
            X = canStack ? Pos.Center() - 17 : 0,
            Y = Pos.AnchorEnd(1)
        };
        _clearDevicesBtn.SetScheme(TransparentDialogScheme);
        _clearDevicesBtn.Accepting += (s, e) =>
        {
            _storage.ClearAll();
            RefreshPairingData();
        };
        _rightPane.Add(_clearDevicesBtn);

        _closeBtn = new Button
        {
            Text = "关闭窗口 (Esc)",
            X = canStack ? Pos.Center() + 1 : Pos.Right(_clearDevicesBtn) + 1,
            Y = Pos.AnchorEnd(1)
        };
        _closeBtn.SetScheme(TransparentDialogScheme);
        _closeBtn.Accepting += (s, e) => Application.RequestStop();
        _rightPane.Add(_closeBtn);

        // 填充初始二维码
        if (initialQrLines.Count > 0)
        {
            _qrView.SetSource(new ObservableCollection<string>(initialQrLines));
        }

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop();
            }
            else if (k == Key.R || k.AsRune.Value == 'r' || k.AsRune.Value == 'R')
            {
                k.Handled = true;
                if (!_isDownloadMode)
                {
                    RefreshPairingData();
                }
            }
            else if (k == Key.D || k.AsRune.Value == 'd' || k.AsRune.Value == 'D')
            {
                k.Handled = true;
                if (!_isDownloadMode)
                {
                    ToggleQrMode();
                }
            }
            else if (k == Key.P || k.AsRune.Value == 'p' || k.AsRune.Value == 'P')
            {
                k.Handled = true;
                if (_isDownloadMode)
                {
                    ToggleQrMode();
                }
            }
            else if (k == Key.C || k.AsRune.Value == 'c' || k.AsRune.Value == 'C')
            {
                k.Handled = true;
                if (!_isDownloadMode)
                {
                    _storage.ClearAll();
                    RefreshPairingData();
                }
            }
        };
    }

    private void ToggleQrMode()
    {
        _isDownloadMode = !_isDownloadMode;
        if (_isDownloadMode)
        {
            Title = "Melodist Mobile 客户端下载";
            _toggleModeBtn.Text = "配对码 (P)";
            _pairedLabel.Visible = false;
            _refreshPinBtn.Visible = false;
            _clearDevicesBtn.Visible = false;

            // 启动异步获取最新 APK 直链（有缓存或完成请求后更新为直链，无网或超时兜底为 Releases 页面）
            _ = Task.Run(LoadDownloadQrCodeAsync);
        }
        else
        {
            Title = "远程控制配对 (Melodist Connect)";
            _toggleModeBtn.Text = "App下载 (D)";
            _pairedLabel.Visible = _canSplit;
            _refreshPinBtn.Visible = true;
            _clearDevicesBtn.Visible = true;
            RefreshPairingData();
        }
    }

    private async Task LoadDownloadQrCodeAsync()
    {
        // 1. 若有 15 分钟内的有效缓存，直接秒开直链二维码
        if (s_cachedRelease.HasValue && s_cachedRelease.Value.ExpireAt > DateTimeOffset.UtcNow)
        {
            var cached = s_cachedRelease.Value;
            Application.Invoke(() =>
            {
                if (_isDownloadMode)
                {
                    ApplyDownloadReleaseInfo(cached.DownloadUrl, cached.Tag, cached.FileName);
                }
            });
            return;
        }

        // 2. 初始立即展示兜底发布页二维码，杜绝界面等待卡顿
        Application.Invoke(() =>
        {
            if (_isDownloadMode)
            {
                ApplyDownloadReleaseInfo(AppDownloadFallbackUrl, "最新发布页", "melodist/releases");
            }
        });

        // 3. 异步探测 GitHub Releases API 获取最新构建产物
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Viemean/melodist/releases/latest");
            req.Headers.UserAgent.ParseAdd("QmTui/1.0");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await s_releaseHttpClient.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tElem) ? (tElem.GetString() ?? "") : "";
            string? matchedUrl = null;
            string? matchedName = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nElem) && asset.TryGetProperty("browser_download_url", out var uElem))
                    {
                        var name = nElem.GetString() ?? "";
                        var url = uElem.GetString() ?? "";
                        if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            if (name.Contains("mobile", StringComparison.OrdinalIgnoreCase))
                            {
                                matchedUrl = url;
                                matchedName = name;
                                break;
                            }
                            matchedUrl ??= url;
                            matchedName ??= name;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(matchedUrl))
            {
                var fileName = matchedName ?? "melodist-mobile.apk";
                s_cachedRelease = (matchedUrl, tag, fileName, DateTimeOffset.UtcNow.AddMinutes(15));
                Application.Invoke(() =>
                {
                    if (_isDownloadMode)
                    {
                        ApplyDownloadReleaseInfo(matchedUrl, tag, fileName);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConnectDialog", $"Fetch latest release failed, fallback to releases page: {ex.Message}");
        }
    }

    private void ApplyDownloadReleaseInfo(string targetUrl, string tag, string fileName)
    {
        bool isDirectApk = targetUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
        _pinCodeLabel.Text = isDirectApk ? $"客户端:   Melodist Mobile ({tag})" : "客户端:   Melodist Mobile (Android)";
        _addrLabel.Text = isDirectApk ? $"最新安装包: {fileName}" : "发布页:   github.com/Viemean/melodist";
        _statusLabel.Text = isDirectApk ? "类型:     直链 APK (扫码直接下载安装)" : "类型:     GitHub Release 发布页";
        _tipLabel.Text = isDirectApk
            ? "下载说明:\n1. 手机扫描左侧二维码直接下载最新 APK 安装包\n2. 安装后点击下方 [配对码] 扫码连接本机"
            : "下载说明:\n1. 手机扫描左侧二维码前往 GitHub 发布页\n2. 下载并安装最新版 APK\n3. 安装后点击下方 [配对码] 扫码连接本机";

        var dlLines = QrCodeEncoder.EncodeToBlockText(targetUrl, QrCodeEncoder.EccLevel.L, quietZone: 1);
        if (dlLines.Count > 0)
        {
            _qrView.Width = dlLines[0].Length;
            _qrView.Height = dlLines.Count;
            _qrView.SetSource(new ObservableCollection<string>(dlLines));
        }
    }

    private List<string> GenerateQrLines(string pin, string localIp)
    {
        try
        {
            var shortDeviceId = _storage.LocalDeviceId.Length > 8 ? _storage.LocalDeviceId[..8] : _storage.LocalDeviceId;
            var shortToken = _storage.LocalToken.Length > 8 ? _storage.LocalToken[..8] : _storage.LocalToken;

            var qrData = new QrPairData(
                Version: 1,
                DeviceId: shortDeviceId,
                DeviceName: "QMTUI",
                Host: localIp,
                Port: _server.ActualPort,
                Token: shortToken,
                PinCode: pin
            );
            var json = JsonSerializer.Serialize(qrData, ConnectJsonContext.Default.QrPairData);
            return QrCodeEncoder.EncodeToBlockText(json, QrCodeEncoder.EccLevel.L, quietZone: 1);
        }
        catch (Exception ex)
        {
            return ["二维码生成失败", ex.Message];
        }
    }

    private void RefreshPairingData()
    {
        var pin = _storage.GenerateNewPinCode();
        var localIp = ConnectMdnsService.GetBestLocalIpAddress();

        _pinCodeLabel.Text = $"6位 PIN 码:  [ {pin} ]";
        _addrLabel.Text = $"服务地址:   {localIp}:{_server.ActualPort}";
        _statusLabel.Text = $"在线连接:   {_server.ConnectedCount} 个设备";
        _tipLabel.Text = _canSplit
            ? "连接说明:\n1. 手机打开 Melodist Mobile\n2. 扫描左侧二维码，或局域网\n   发现后输入上方 6位 PIN 码"
            : "连接说明:\n1. 手机打开 Melodist Mobile\n2. 扫描二维码或输入 6位 PIN 码";
        _pairedLabel.Text = $"已配对信任: {_storage.PairedDevices.Count} 个设备";

        var asciiLines = GenerateQrLines(pin, localIp);
        if (asciiLines.Count > 0)
        {
            _qrView.Width = asciiLines[0].Length;
            _qrView.Height = asciiLines.Count;
            _qrView.SetSource(new ObservableCollection<string>(asciiLines));
        }

        _mdnsService?.Start();
    }
}
