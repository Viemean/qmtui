using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Services;

namespace QmTui.UI;

/// <summary>
/// 新建歌单对话框：轻量半透明输入框，回车创建歌单
/// </summary>
public sealed class CreatePlaylistDialog : Dialog
{
    private readonly TextField _nameField;
    private readonly Action<string> _onConfirm;

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

    public CreatePlaylistDialog(Action<string> onConfirm)
    {
        _onConfirm = onConfirm;

        Title = "新建歌单";
        Width = 56;
        Height = 9;
        X = Pos.Center();
        Y = Pos.Center();
        SetScheme(TransparentDialogScheme);

        var promptLabel = new Label
        {
            Text = "请输入歌单名称:",
            X = 2,
            Y = 1
        };
        promptLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None)
        });
        Add(promptLabel);

        _nameField = new TextField
        {
            X = 2,
            Y = 3,
            Width = Dim.Fill(2),
            Height = 1,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        _nameField.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(Color.White, Color.Black),
            Focus = new Terminal.Gui.Drawing.Attribute(Color.White, MikuTheme.QqGreenDark)
        });
        _nameField.EnableMiddleClickPaste();
        Add(_nameField);

        var confirmBtn = new Button
        {
            Text = "[Enter] 确认创建",
            X = Pos.Center() - 14,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            CanFocus = false
        };
        confirmBtn.SetScheme(new Scheme { Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Color.None) });
        confirmBtn.Accepting += (s, e) => Submit();
        confirmBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                Submit();
                m.Handled = true;
            }
        };

        var cancelBtn = new Button
        {
            Text = "[Esc] 取消",
            X = Pos.Center() + 4,
            Y = Pos.AnchorEnd(1),
            NoDecorations = true,
            CanFocus = false
        };
        cancelBtn.SetScheme(new Scheme { Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None) });
        cancelBtn.Accepting += (s, e) => Application.RequestStop(this);
        cancelBtn.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                Application.RequestStop(this);
                m.Handled = true;
            }
        };

        Add(confirmBtn, cancelBtn);

        _nameField.Accepting += (s, e) =>
        {
            e.Handled = true;
            Submit();
        };

        _nameField.KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        };

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc)
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        };

        _nameField.SetFocus();
        Application.Invoke(() => _nameField.SetFocus());
    }

    private void Submit()
    {
        var name = _nameField.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name))
        {
            Application.RequestStop(this);
            _onConfirm(name);
        }
    }
}
