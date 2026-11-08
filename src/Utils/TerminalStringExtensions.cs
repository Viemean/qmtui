using System;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;

namespace QmTui.Utils;

/// <summary>
/// 终端文本与字符视觉列宽计算与排版扩展
/// </summary>
public static class TerminalStringExtensions
{
    private static readonly string s_spaces = new(' ', 256);

    /// <summary>
    /// 获取指定数量的空格字符串
    /// </summary>
    /// <param name="count">空格数量</param>
    /// <returns>空格字符串</returns>
    public static string GetPadding(int count)
    {
        if (count <= 0) return "";
        if (count <= s_spaces.Length) return s_spaces[..count];
        return new string(' ', count);
    }

    /// <summary>
    /// 计算文本在终端中的显示视觉列宽（基于 Terminal.Gui 的 Unicode 字符列宽规范）
    /// </summary>
    /// <param name="text">待计算的文本</param>
    /// <returns>文本所占终端列数；若为 null 或空字符串则返回 0</returns>
    public static int GetVisualWidth(this string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.GetColumns();
    }

    /// <summary>
    /// 计算单个字符在终端中的显示视觉列宽
    /// </summary>
    /// <param name="ch">待计算的字符</param>
    /// <returns>字符所占终端列数（1 或 2）</returns>
    public static int GetVisualWidth(this char ch)
    {
        if (ch <= 127) return 1;
        return Rune.TryCreate(ch, out var rune) ? Math.Max(1, rune.GetColumns()) : 2;
    }

    /// <summary>
    /// 计算单个 Rune 在终端中的显示视觉列宽
    /// </summary>
    /// <param name="rune">待计算的 Rune</param>
    /// <returns>Rune 所占终端列数</returns>
    public static int GetVisualWidth(this Rune rune)
    {
        int cols = rune.GetColumns();
        return cols > 0 ? cols : 1;
    }

    /// <summary>
    /// 将文本右侧填充空格至指定的终端视觉列宽
    /// </summary>
    /// <param name="text">待填充的原文本</param>
    /// <param name="targetWidth">目标终端视觉列宽</param>
    /// <returns>符合目标列宽的格式化字符串</returns>
    public static string PadRightVisual(this string? text, int targetWidth)
    {
        if (string.IsNullOrEmpty(text)) return GetPadding(targetWidth);
        int w = text.GetVisualWidth();
        int pad = targetWidth - w;
        return pad > 0 ? text + GetPadding(pad) : text;
    }

    /// <summary>
    /// 将文本左侧填充空格至指定的终端视觉列宽
    /// </summary>
    /// <param name="text">待填充的原文本</param>
    /// <param name="targetWidth">目标终端视觉列宽</param>
    /// <returns>符合目标列宽的格式化字符串</returns>
    public static string PadLeftVisual(this string? text, int targetWidth)
    {
        if (string.IsNullOrEmpty(text)) return GetPadding(targetWidth);
        int w = text.GetVisualWidth();
        int pad = targetWidth - w;
        return pad > 0 ? GetPadding(pad) + text : text;
    }

    /// <summary>
    /// 将文本截断并在右侧填充空格至指定的终端显示列宽
    /// </summary>
    /// <param name="text">待对齐的原文本</param>
    /// <param name="targetWidth">目标终端显示列宽</param>
    /// <returns>符合目标列宽的格式化字符串</returns>
    public static string TruncateAndPadWide(this string? text, int targetWidth)
    {
        if (targetWidth <= 0) return "";
        if (string.IsNullOrEmpty(text))
        {
            return GetPadding(targetWidth);
        }

        if (targetWidth < 3)
        {
            int w = 0;
            int idx = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                int runeW = rune.GetColumns();
                if (w + runeW > targetWidth) break;
                w += runeW;
                idx += rune.Utf16SequenceLength;
            }
            return string.Concat(text.AsSpan(0, idx), GetPadding(targetWidth - w));
        }

        int limit = Math.Max(1, targetWidth - 2);
        int cutCharIndex = -1;
        int cutWidth = 0;
        int totalWidth = 0;
        int charIndex = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            int runeW = rune.GetColumns();
            if (cutCharIndex < 0 && totalWidth + runeW > limit)
            {
                cutCharIndex = charIndex;
                cutWidth = totalWidth;
            }
            totalWidth += runeW;
            charIndex += rune.Utf16SequenceLength;

            if (totalWidth > targetWidth)
            {
                break;
            }
        }

        if (totalWidth == targetWidth)
        {
            return text;
        }

        if (totalWidth < targetWidth)
        {
            return text + GetPadding(targetWidth - totalWidth);
        }

        int pad = targetWidth - cutWidth - 2;
        if (pad <= 0)
        {
            return string.Concat(text.AsSpan(0, cutCharIndex), "..");
        }

        return string.Concat(text.AsSpan(0, cutCharIndex), "..", GetPadding(pad));
    }
}
