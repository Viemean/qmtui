using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public sealed class PlaybackCredentialsDialog : Dialog
{
    private static Scheme TransparentDialogScheme => MikuTheme.TransparentDialog;

    private readonly Label _statusLabel;
    private readonly Button _exportBtn;
    private readonly Button _importBtn;
    private readonly Button _clearBtn;
    private readonly Button _closeBtn;
    private readonly Action? _onChanged;

    public PlaybackCredentialsDialog(Action? onChanged = null)
    {
        _onChanged = onChanged;

        Title = "播放凭证管理";
        Width = 68;
        Height = 16;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        _statusLabel = new Label
        {
            X = 2,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = 4
        };
        _statusLabel.SetScheme(TransparentDialogScheme);

        _exportBtn = new Button
        {
            Text = "导出播放凭证",
            X = 2,
            Y = 6,
            ShadowStyle = ShadowStyles.None
        };
        _exportBtn.SetScheme(TransparentDialogScheme);
        _exportBtn.Accepting += (_, _) => ShowExportDialog();

        _importBtn = new Button
        {
            Text = "导入播放凭证",
            X = Pos.Right(_exportBtn) + 2,
            Y = 6,
            ShadowStyle = ShadowStyles.None
        };
        _importBtn.SetScheme(TransparentDialogScheme);
        _importBtn.Accepting += (_, _) => ShowImportDialog();

        _clearBtn = new Button
        {
            Text = "清除凭证",
            X = Pos.Right(_importBtn) + 2,
            Y = 6,
            ShadowStyle = ShadowStyles.None
        };
        _clearBtn.SetScheme(TransparentDialogScheme);
        _clearBtn.Accepting += (_, _) =>
        {
            PlaybackCredentialsManager.ClearCredentials();
            UpdateStatusView();
            _onChanged?.Invoke();
        };

        _closeBtn = new Button
        {
            Text = "关闭 (Esc)",
            X = Pos.AnchorEnd(14),
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        _closeBtn.SetScheme(TransparentDialogScheme);
        _closeBtn.Accepting += (_, _) => RequestStop();

        Add(_statusLabel, _exportBtn, _importBtn, _clearBtn, _closeBtn);

        UpdateStatusView();
    }

    private void UpdateStatusView()
    {
        var creds = PlaybackCredentialsManager.CurrentCredentials;
        if (creds != null)
        {
            bool isInvalid = PlaybackCredentialsManager.IsCredentialInvalid(creds);
            bool canRefresh = PlaybackCredentialsManager.CanRefreshCredentials(creds);

            if (isInvalid)
            {
                _statusLabel.Text = $"当前状态: 导入的凭证为已失效\n" +
                                    $"账号: {(string.IsNullOrEmpty(creds.Nick) ? creds.Uin : creds.Nick)} (UIN: {creds.Uin})\n" +
                                    $"该凭证可能已被云端吊销或已过期，请重新导入有效凭证。";
            }
            else
            {
                string refreshInfo = canRefresh ? " [支持自动续签]" : " [只读票据]";
                _statusLabel.Text = $"当前状态: 已启用独立播放凭证{refreshInfo}\n" +
                                    $"账号: {(string.IsNullOrEmpty(creds.Nick) ? creds.Uin : creds.Nick)} (UIN: {creds.Uin})\n" +
                                    $"播放与下载将使用该账号权限，歌单与推荐保持使用主账号。";
            }
            _clearBtn.Visible = true;
        }
        else
        {
            _statusLabel.Text = "当前状态: 未绑定独立凭证\n" +
                                "播放与下载默认使用当前主登录账号。";
            _clearBtn.Visible = false;
        }
    }

    private static void ShowExportDialog()
    {
        if (!UserSession.Current.IsLoggedIn)
        {
            ShowNotice("导出提示", "当前主账号尚未登录，无法导出凭证。");
            return;
        }

        var dlg = new Dialog
        {
            Title = "导出播放凭证",
            Width = 66,
            Height = 10,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var pwdLabel = new Label { Text = "保护密码 (可选留空):", X = 2, Y = 1 };
        pwdLabel.SetScheme(TransparentDialogScheme);

        var pwdField = new TextField { X = 2, Y = 2, Width = Dim.Fill() - 2, Secret = true };
        pwdField.SetScheme(TransparentDialogScheme);

        bool allowRefresh = false;
        var refreshBtn = new Button
        {
            Text = "允许续签: 否",
            X = 2,
            Y = 4,
            ShadowStyle = ShadowStyles.None
        };
        refreshBtn.SetScheme(TransparentDialogScheme);
        refreshBtn.Accepting += (s, e) =>
        {
            allowRefresh = !allowRefresh;
            refreshBtn.Text = allowRefresh ? "允许续签: 是" : "允许续签: 否";
            e.Handled = true;
        };

        var okBtn = new Button
        {
            Text = "生成并复制 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        okBtn.SetScheme(TransparentDialogScheme);

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.Right(okBtn) + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);
        cancelBtn.Accepting += (_, _) => dlg.RequestStop();

        okBtn.Accepting += (_, _) =>
        {
            string pwd = pwdField.Text?.ToString() ?? "";

            try
            {
                string token = PlaybackCredentialsManager.ExportToken(UserSession.Current, pwd, allowRefresh);
                ClipboardService.SetText(token);
                dlg.RequestStop();
                ShowTokenResultDialog(token);
            }
            catch (Exception ex)
            {
                ShowNotice("导出失败", ex.Message);
            }
        };

        dlg.Add(pwdLabel, pwdField, refreshBtn, okBtn, cancelBtn);
        Application.Run(dlg);
    }

    private static void ShowTokenResultDialog(string token)
    {
        var dlg = new Dialog
        {
            Title = "凭证已生成 (已复制到剪贴板)",
            Width = 66,
            Height = 13,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var tip = new Label
        {
            Text = "凭证字符串已自动复制到系统剪贴板，亦可从下方选中文本：",
            X = 2,
            Y = 1
        };
        tip.SetScheme(TransparentDialogScheme);

        var tokenField = new TextField
        {
            Text = token,
            X = 2,
            Y = 3,
            Width = Dim.Fill() - 2,
            ReadOnly = true
        };
        tokenField.SetScheme(TransparentDialogScheme);

        var closeBtn = new Button
        {
            Text = "完成 (Enter)",
            X = Pos.Center() - 6,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        closeBtn.SetScheme(TransparentDialogScheme);
        closeBtn.Accepting += (_, _) => dlg.RequestStop();

        dlg.Add(tip, tokenField, closeBtn);
        Application.Run(dlg);
    }

    private void ShowImportDialog()
    {
        var dlg = new Dialog
        {
            Title = "导入播放凭证",
            Width = 72,
            Height = 16,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        dlg.SetScheme(TransparentDialogScheme);

        var tokenLabel = new Label { Text = "粘贴凭证 Token:", X = 2, Y = 1 };
        tokenLabel.SetScheme(TransparentDialogScheme);

        var tokenView = new TextView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill() - 2,
            Height = 6,
            WordWrap = true
        };
        tokenView.SetScheme(TransparentDialogScheme);

        var pasteBtn = new Button
        {
            Text = "从剪贴板粘贴",
            X = Pos.AnchorEnd(16),
            Y = 1,
            ShadowStyle = ShadowStyles.None
        };
        pasteBtn.SetScheme(TransparentDialogScheme);
        pasteBtn.Accepting += (s, e) =>
        {
            e.Handled = true;
            var text = ClipboardService.GetText()?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                tokenView.Text = text;
                tokenView.SetFocus();
            }
        };

        var pwdLabel = new Label { Text = "解密密码 (若凭证未加密可留空):", X = 2, Y = 9 };
        pwdLabel.SetScheme(TransparentDialogScheme);

        var pwdField = new TextField { X = 2, Y = 10, Width = Dim.Fill() - 2, Secret = true };
        pwdField.SetScheme(TransparentDialogScheme);

        var okBtn = new Button
        {
            Text = "验证并导入 (Enter)",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        okBtn.SetScheme(TransparentDialogScheme);

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.Right(okBtn) + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);
        cancelBtn.Accepting += (_, _) => dlg.RequestStop();

        okBtn.Accepting += (_, _) =>
        {
            string token = tokenView.Text?.ToString()?.Trim() ?? "";
            string pwd = pwdField.Text?.ToString() ?? "";

            if (string.IsNullOrEmpty(token))
            {
                ShowNotice("导入提示", "请先输入或粘贴凭据字符串。");
                return;
            }

            try
            {
                var result = PlaybackCredentialsManager.InspectToken(token, pwd);
                switch (result)
                {
                    case ImportValidationResult.Expired:
                        ShowNotice("导入被拦截", "此播放凭据已经过期，无法导入。");
                        break;

                    case ImportValidationResult.NonVip:
                        ShowNotice("导入被拦截", "此账号未开通 VIP，挂载该凭证无音质收益。");
                        break;

                    case ImportValidationResult.DowngradeWarning warning:
                        dlg.RequestStop();
                        ShowDowngradeConfirmDialog(warning.Credentials);
                        break;

                    case ImportValidationResult.Success success:
                        PlaybackCredentialsManager.SetCredentials(success.Credentials);
                        dlg.RequestStop();
                        UpdateStatusView();
                        _onChanged?.Invoke();
                        ShowNotice("导入成功", $"已成功绑定独立播放凭证：{success.Credentials.Nick.IfBlank(success.Credentials.Uin)}");
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowNotice("导入失败", ex.Message);
            }
        };

        dlg.Add(tokenLabel, tokenView, pasteBtn, pwdLabel, pwdField, okBtn, cancelBtn);
        Application.Run(dlg);
    }

    private void ShowDowngradeConfirmDialog(PlaybackCredentials creds)
    {
        var confirmDlg = new Dialog
        {
            Title = "降级警告确认",
            Width = 56,
            Height = 10,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        confirmDlg.SetScheme(TransparentDialogScheme);

        var msg = new Label
        {
            Text = "当前主账号权限高于或等于导入凭据权限，\n覆盖后可能导致可用音质降级，确定覆盖吗？",
            X = Pos.Center(),
            Y = 1
        };
        msg.SetScheme(TransparentDialogScheme);

        var okBtn = new Button
        {
            Text = "确定覆盖",
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        okBtn.SetScheme(TransparentDialogScheme);
        okBtn.Accepting += (_, _) =>
        {
            PlaybackCredentialsManager.SetCredentials(creds);
            confirmDlg.RequestStop();
            UpdateStatusView();
            _onChanged?.Invoke();
            ShowNotice("导入成功", $"已覆盖并绑定独立播放凭据：{creds.Nick.IfBlank(creds.Uin)}");
        };

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.Right(okBtn) + 2,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        cancelBtn.SetScheme(TransparentDialogScheme);
        cancelBtn.Accepting += (_, _) => confirmDlg.RequestStop();

        confirmDlg.Add(msg, okBtn, cancelBtn);
        Application.Run(confirmDlg);
    }

    private static void ShowNotice(string title, string message)
    {
        var noticeDlg = new Dialog
        {
            Title = title,
            Width = 50,
            Height = 8,
            X = Pos.Center(),
            Y = Pos.Center()
        };
        noticeDlg.SetScheme(TransparentDialogScheme);

        var msgLabel = new Label
        {
            Text = message,
            X = Pos.Center(),
            Y = 1
        };
        msgLabel.SetScheme(TransparentDialogScheme);

        var okBtn = new Button
        {
            Text = "确定 (Enter)",
            X = Pos.Center() - 6,
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None
        };
        okBtn.SetScheme(TransparentDialogScheme);
        okBtn.Accepting += (_, _) => noticeDlg.RequestStop();

        noticeDlg.Add(msgLabel, okBtn);
        Application.Run(noticeDlg);
    }
}

internal static class StringExtensions
{
    public static string IfBlank(this string? str, string fallback)
    {
        return string.IsNullOrWhiteSpace(str) ? fallback : str;
    }
}
