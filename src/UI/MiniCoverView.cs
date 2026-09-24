using System;
using System.IO;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Models;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace QmTui.UI;

/// <summary>
/// 左侧导航栏下方常驻迷你直角正方形封面视窗
/// </summary>
public sealed class MiniCoverView : FrameView
{
    private readonly Label _placeholderLabel;
    private string? _currentCoverPath;
    private Song? _currentSong;
    private object? _resizeTimerToken;

    public event Action? CoverClicked;

    public MiniCoverView()
    {
        Title = "";
        Width = 14;
        Height = 8;
        CanFocus = false;
        TabStop = TabBehavior.NoStop;
        SetScheme(MikuTheme.FrameBorderDim);

        _placeholderLabel = new Label
        {
            Text = "┤唱片├",
            X = Pos.Center(),
            Y = Pos.Center(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        _placeholderLabel.SetScheme(new Scheme
        {
            Normal = new Attribute(MikuTheme.MikuTextMuted, MikuTheme.MikuBgSurface)
        });

        Add(_placeholderLabel);

        MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) || m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                CoverClicked?.Invoke();
                m.Handled = true;
            }
        };
    }

    public void SetSong(Song? song, string? qualityBadge)
    {
        _currentSong = song;

        if (song == null)
        {
            _currentCoverPath = null;
            _placeholderLabel.Text = "┤唱片├";
            _placeholderLabel.Visible = true;
            ClearCover();
            SetNeedsDraw();
        }
        else
        {
            _placeholderLabel.Text = "┤读取中├";
            SetNeedsDraw();
        }
    }

    public void UpdateCover(string? coverPath)
    {
        _currentCoverPath = coverPath;
        if (!Visible) return;

        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath))
        {
            _placeholderLabel.Visible = true;
            ClearCover();
            SetNeedsDraw();
            return;
        }

        TriggerRenderDelayed();
    }

    public void ClearCover()
    {
        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }
        TerminalImageHelper.DeleteKittyImage(TerminalImageHelper.ImageIdMiniCover);
    }

    public void OnWindowResized()
    {
        if (!Visible) return;
        TerminalImageHelper.DeleteKittyImage(TerminalImageHelper.ImageIdMiniCover);
        TriggerRenderDelayed();
    }

    public void TriggerRenderDelayed()
    {
        if (!Visible) return;

        bool hasValidImage = !string.IsNullOrEmpty(_currentCoverPath) && File.Exists(_currentCoverPath);
        if (!TerminalImageHelper.IsImageSupported || !hasValidImage)
        {
            _placeholderLabel.Visible = true;
            SetNeedsDraw();
            return;
        }

        if (_resizeTimerToken != null)
        {
            Application.RemoveTimeout(_resizeTimerToken);
            _resizeTimerToken = null;
        }

        // 60ms 快速首绘
        _resizeTimerToken = Application.AddTimeout(TimeSpan.FromMilliseconds(60), () =>
        {
            _resizeTimerToken = null;
            if (Visible)
            {
                RenderCoverIfVisible();

                // 250ms 二次补位重绘，确保所有字符边框绘制完毕后 Kitty 图像稳定贴合
                Application.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
                {
                    if (Visible)
                    {
                        RenderCoverIfVisible();
                    }
                    return false;
                });
            }
            return false;
        });
    }

    private void RenderCoverIfVisible()
    {
        if (!Visible || !TerminalImageHelper.IsImageSupported || string.IsNullOrEmpty(_currentCoverPath) || !File.Exists(_currentCoverPath))
        {
            _placeholderLabel.Visible = true;
            SetNeedsDraw();
            return;
        }

        try
        {
            var origin = FrameToScreen();
            // FrameView 包含 1 字符外边框。origin 为外框左上角在屏幕的 0-based 坐标。
            // 映射为终端 1-based ANSI 光标后，内容可视区域第 0 列为 origin.X + 2，第 0 行为 origin.Y + 2。
            int contentCol = origin.X + 2;
            int contentRow = origin.Y + 2;
            int contentWidth = Math.Max(4, Frame.Width - 2);
            int contentHeight = Math.Max(4, Frame.Height - 2);

            if (contentHeight < 4 || contentWidth < 4)
            {
                _placeholderLabel.Visible = true;
                return;
            }

            // 满幅贴合：占满内部 12 列宽度与 6 行高度（1:1 正方形在 1:2 字符比例下正好为 6 行）
            int targetCols = contentWidth;
            int expectedRows = (targetCols + 1) / 2;
            int colOffset = Math.Max(0, (contentWidth - targetCols) / 2);
            int rowOffset = Math.Max(0, (contentHeight - expectedRows) / 2);

            int renderCol = contentCol + colOffset;
            int renderRow = contentRow + rowOffset;

            // 原画 1:1 满幅贴合微圆角封面渲染
            TerminalImageHelper.RenderKittyImage(_currentCoverPath, renderCol, renderRow, targetCols, rows: 0, TerminalImageHelper.ImageIdMiniCover);
            _placeholderLabel.Visible = false;
        }
        catch
        {
            _placeholderLabel.Visible = true;
        }
    }
}
