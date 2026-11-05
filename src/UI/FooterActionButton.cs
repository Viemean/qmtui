using System;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace QmTui.UI;

/// <summary>
/// 底部操作栏轻量交互按钮：纯鼠标交互，悬停高亮，不参与键盘 Tab 焦点流转
/// </summary>
public sealed class FooterActionButton : Label
{
    private bool _isHovered;
    private readonly Action<MouseFlags>? _onClickWithMouse;

    public string FullText { get; }
    public string ShortText { get; }

    public FooterActionButton(string fullText, string shortText, Action<MouseFlags> onClickWithMouse)
    {
        FullText = fullText;
        ShortText = shortText;
        Text = fullText;
        _onClickWithMouse = onClickWithMouse;

        CanFocus = false;
        TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;
        MousePositionTracking = true;

        UpdateScheme();

        MouseEnter += (s, e) =>
        {
            _isHovered = true;
            UpdateScheme();
        };

        MouseLeave += (s, e) =>
        {
            _isHovered = false;
            UpdateScheme();
        };

        MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked) ||
                m.Flags.HasFlag(MouseFlags.RightButtonClicked) ||
                m.Flags.HasFlag(MouseFlags.WheeledDown) ||
                m.Flags.HasFlag(MouseFlags.WheeledUp))
            {
                _onClickWithMouse?.Invoke(m.Flags);
                m.Handled = true;
            }
        };
    }

    public FooterActionButton(string fullText, string shortText, Action onClick)
        : this(fullText, shortText, _ => onClick())
    {
    }

    public void UpdateScheme()
    {
        var fg = _isHovered ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
        SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(fg, MikuTheme.MikuBgSurface)
        });
        SetNeedsDraw();
    }
}
