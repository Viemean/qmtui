using System.Diagnostics;
using System.Text;

namespace QmTui.Services;

/// <summary>
/// 跨桌面环境（Wayland / X11）与虚拟终端的系统剪贴板服务
/// </summary>
public static class ClipboardService
{
    /// <summary>
    /// 将指定文本复制到系统剪贴板（支持 Wayland wl-copy、X11 xclip/xsel 及终端 OSC 52 转义序列）
    /// </summary>
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        bool success = false;

        // 1. Wayland 环境优先尝试 wl-copy
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            if (TryRunWithStdin("wl-copy", text))
            {
                success = true;
            }
        }

        // 2. X11 环境（或 Wayland 未能成功）尝试 xclip / xsel
        if (!success && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            if (TryRunWithStdin("xclip", text, "-selection", "clipboard") ||
                TryRunWithStdin("xsel", text, "--clipboard", "--input"))
            {
                success = true;
            }
        }

        // 3. 增强：发送 ANSI OSC 52 剪贴板转义码至标准输出（兼容现代终端如 Kitty, Alacritty, Konsole 等）
        try
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            Console.Out.Write($"\x1b]52;c;{base64}\x07");
            Console.Out.Flush();
            success = true;
        }
        catch
        {
            // 忽略终端转义写出异常
        }

        return success;
    }

    /// <summary>
    /// 从系统剪贴板读取文本（支持 Wayland wl-paste、X11 xclip/xsel）
    /// </summary>
    /// <param name="primary">是否优先读取鼠标选区 (Primary Selection，通常用于鼠标中键粘贴)</param>
    public static string GetText(bool primary = false)
    {
        // 1. Wayland 环境优先尝试 wl-paste
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            var text = primary
                ? TryReadStdout("wl-paste", "-n", "--primary")
                : TryReadStdout("wl-paste", "-n");
            if (!string.IsNullOrEmpty(text)) return text;
        }

        // 2. X11 环境（或 Wayland 未能成功）尝试 xclip / xsel
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            var text = primary
                ? (TryReadStdout("xclip", "-selection", "primary", "-o") ?? TryReadStdout("xsel", "--primary", "--output"))
                : (TryReadStdout("xclip", "-selection", "clipboard", "-o") ?? TryReadStdout("xsel", "--clipboard", "--output"));
            if (!string.IsNullOrEmpty(text)) return text;
        }

        // 若请求 primary 但为空，可尝试回退读取普通剪贴板
        if (primary)
        {
            return GetText(primary: false);
        }

        return "";
    }

    private static string? TryReadStdout(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(500);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRunWithStdin(string fileName, string input, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using (var sw = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                sw.Write(input);
                sw.Flush();
            }

            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 适配 Terminal.Gui 的跨桌面环境 Linux 系统剪贴板实现
/// </summary>
public sealed class LinuxSystemClipboard : Terminal.Gui.App.IClipboard
{
    public bool IsSupported => true;

    public string GetClipboardData()
    {
        return ClipboardService.GetText(primary: false);
    }

    public void SetClipboardData(string text)
    {
        ClipboardService.SetText(text);
    }

    public bool TryGetClipboardData(out string result)
    {
        result = GetClipboardData();
        return !string.IsNullOrEmpty(result);
    }

    public bool TrySetClipboardData(string text)
    {
        return ClipboardService.SetText(text);
    }
}

/// <summary>
/// 文本输入控件剪贴板与鼠标中键粘贴扩展
/// </summary>
public static class TextFieldClipboardExtensions
{
    /// <summary>
    /// 将剪贴板或 Primary 选区文本粘贴至输入框当前光标或指定位置
    /// </summary>
    public static void PasteFromClipboard(this Terminal.Gui.Views.TextField textField, bool preferPrimary = false, int? targetPosition = null)
    {
        var pasteText = ClipboardService.GetText(primary: preferPrimary);
        if (string.IsNullOrEmpty(pasteText)) return;

        pasteText = pasteText.Replace("\r", "").Replace("\n", " ");
        var current = textField.Text ?? "";
        int pos = targetPosition.HasValue
            ? Math.Clamp(targetPosition.Value, 0, current.Length)
            : Math.Clamp(textField.InsertionPoint, 0, current.Length);

        if (textField.SelectedLength > 0 && textField.SelectedStart >= 0)
        {
            int selStart = Math.Clamp(textField.SelectedStart, 0, current.Length);
            int selLen = Math.Clamp(textField.SelectedLength, 0, current.Length - selStart);
            current = current.Remove(selStart, selLen);
            pos = selStart;
        }

        textField.Text = current.Insert(pos, pasteText);
        textField.InsertionPoint = pos + pasteText.Length;
        textField.SetNeedsDraw();
    }

    /// <summary>
    /// 为 TextField 启用鼠标中键粘贴（Primary Selection 或系统剪贴板）并自动获焦
    /// </summary>
    public static void EnableMiddleClickPaste(this Terminal.Gui.Views.TextField textField, Action? onFocused = null)
    {
        textField.MouseEvent += (s, m) =>
        {
            if (m.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.MiddleButtonClicked) ||
                m.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.MiddleButtonPressed))
            {
                m.Handled = true;
                onFocused?.Invoke();
                if (!textField.CanFocus)
                {
                    textField.CanFocus = true;
                }
                if (!textField.HasFocus)
                {
                    textField.SetFocus();
                }

                int clickX = m.Position?.X ?? textField.InsertionPoint;
                int clickPos = Math.Clamp(clickX + textField.ScrollOffset, 0, (textField.Text ?? "").Length);

                textField.PasteFromClipboard(preferPrimary: true, targetPosition: clickPos);
            }
        };
    }
}
