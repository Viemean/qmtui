using System.Linq;
using QmTui.UI;
using Xunit;

namespace QmTui.Tests;

/// <summary>
/// 帮助弹窗文案回归：锁住本次补上的键位不丢失，并保证同组内键位标签唯一、文案不被弹窗宽度裁掉。
/// </summary>
public class HelpDialogTests
{
    // ── 过去有功能却从未被提示过的键位 ───────────────────────────

    [Theory]
    [InlineData("Space")]
    [InlineData("Tab / S-Tab")]
    [InlineData("/  F3  C-V")]
    [InlineData("Q")]
    [InlineData("Backspace")]
    [InlineData("Home")]
    [InlineData("N")]
    public void PreviouslyUnadvertisedKeys_AreListedInHelp(string keyLabel)
    {
        Assert.Contains(HelpDialog.KeysHelpLines.SelectMany(g => g.Rows), r => r.Key == keyLabel);
    }

    // ── 文案完整性 ───────────────────────────────────────────────

    [Fact]
    public void KeyLabels_AreUniqueWithinEachGroup()
    {
        foreach (var group in HelpDialog.KeysHelpLines)
        {
            var labels = group.Rows.Select(r => r.Key).ToList();
            Assert.NotEmpty(labels);
            Assert.Equal(labels.Count, labels.Distinct().Count());
        }
    }

    [Fact]
    public void KeyColumn_KeepsDescriptionsAligned()
    {
        var lines = HelpDialog.BuildDisplayLines();
        int lineIdx = 0;
        int? alignedCol = null;

        foreach (var (_, rows) in HelpDialog.KeysHelpLines)
        {
            lineIdx++;   // 分组标题
            foreach (var (key, desc) in rows)
            {
                var line = lines[lineIdx++];
                Assert.EndsWith(desc, line);

                var prefix = line[..^desc.Length];
                Assert.StartsWith($" {key}", prefix);
                Assert.EndsWith(" ", prefix);   // 键名与说明之间至少一个空格

                // 全部键名的说明必须从同一列开始；键名本身超过列宽就会破坏这一点
                int prefixCols = prefix.Sum(ch => ch > 127 ? 2 : 1);
                Assert.Equal(alignedCol ??= prefixCols, prefixCols);
            }
            lineIdx++;   // 分组间空行
        }
    }

    [Fact]
    public void KeysContainOnlyAsciiOrCjkIdeographs()
    {
        // 箭头、省略号等 Ambiguous 宽度字符在终端里可能占 1 列也可能占 2 列，
        // 会破坏说明列对齐；键名只允许 ASCII 和汉字（汉字固定两列）。
        foreach (var (_, rows) in HelpDialog.KeysHelpLines)
        {
            foreach (var (key, _) in rows)
            {
                Assert.All(key, ch => Assert.True(
                    ch <= 0x7F || (ch >= 0x4E00 && ch <= 0x9FFF),
                    $"键名含宽度不可控字符: {key}"));
            }
        }
    }

    [Fact]
    public void HelpLines_FitDialogWidth()
    {
        // 弹窗宽 76 列，CJK 按 2 列折算，超出部分会被终端裁掉
        int tooWide = HelpDialog.BuildDisplayLines()
                                .Count(line => line.Sum(ch => ch > 127 ? 2 : 1) > 74);

        Assert.Equal(0, tooWide);
    }
}
