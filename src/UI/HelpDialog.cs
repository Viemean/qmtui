using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Utils;

namespace QmTui.UI;

/// <summary>
/// 快捷键与鼠标操作总览（只读），由全局 ? / F1 键唤起
/// </summary>
public sealed class HelpDialog : Dialog
{
    internal static (string Group, (string Key, string Desc)[] Rows)[] KeysHelpLines => HelpKeyRegistry.Groups;

    public HelpDialog()
    {
        Title = "快捷键与鼠标操作帮助";
        Width = 76;
        Height = 22;
        SetScheme(MikuTheme.TransparentDialog);

        var listView = new ListView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2)
        };
        listView.SetScheme(MikuTheme.TransparentDialog);
        listView.KeyBindings.Remove(Key.Space);
        listView.SetSource(new ObservableCollection<string>(BuildDisplayLines()));
        Add(listView);

        var scrollHintLabel = new Label
        {
            Text = "[↑↓/PgUp] 滚动",
            X = 2,
            Y = Pos.AnchorEnd(1),
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        scrollHintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
        });
        Add(scrollHintLabel);

        var closeButton = new Label
        {
            Text = "[Esc/?/F1/Enter] 关闭",
            X = Pos.Right(scrollHintLabel) + 3,
            Y = Pos.AnchorEnd(1),
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            MousePositionTracking = true
        };

        void UpdateCloseButtonScheme(bool isHovered)
        {
            var fg = isHovered ? MikuTheme.QqGreenLight : MikuTheme.MikuTextMuted;
            closeButton.SetScheme(new Scheme
            {
                Normal = new Terminal.Gui.Drawing.Attribute(fg, Color.None)
            });
            closeButton.SetNeedsDraw();
        }

        UpdateCloseButtonScheme(false);

        closeButton.MouseEnter += (s, e) => UpdateCloseButtonScheme(true);
        closeButton.MouseLeave += (s, e) => UpdateCloseButtonScheme(false);

        closeButton.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                m.Handled = true;
                Application.RequestStop(this);
            }
        };
        Add(closeButton);

        void HandleClose(Key k)
        {
            if (!MainWindow.IsTerminalWindowFocused)
            {
                k.Handled = true;
                return;
            }

            bool isClose = k == Key.Esc || k == Key.F1 || k == Key.Enter || k.AsRune.Value == '?';
            if (isClose)
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        }

        listView.KeyDown += (s, k) => HandleClose(k);
        KeyDown += (s, k) =>
        {
            if (k.Handled) return;
            HandleClose(k);
        };
    }

    internal static List<string> BuildDisplayLines()
    {
        var lines = new List<string>
        {
            $" 提示：{HelpKeyRegistry.IntroTip}",
            $"       {HelpKeyRegistry.MouseTip}",
            ""
        };
        foreach (var (group, rows) in HelpKeyRegistry.Groups)
        {
            lines.Add($"── {group} ──");
            foreach (var (key, desc) in rows)
            {
                int keyCols = key.GetVisualWidth();
                int padLen = Math.Max(1, HelpKeyRegistry.KeyColumnWidth + 1 - keyCols);
                lines.Add($" {key}{TerminalStringExtensions.GetPadding(padLen)}{desc}");
            }
            lines.Add("");
        }

        return lines;
    }
}
