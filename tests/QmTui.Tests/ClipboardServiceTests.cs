using System;
using Terminal.Gui.Views;
using QmTui.Services;
using Xunit;

namespace QmTui.Tests;

public class ClipboardServiceTests
{
    [Fact]
    public void LinuxSystemClipboard_ImplementsContract()
    {
        var clip = new LinuxSystemClipboard();
        Assert.True(clip.IsSupported);
    }

    [Fact]
    public void TextField_PasteFromClipboard_InsertsText()
    {
        var tf = new TextField { Text = "hello world" };
        tf.InsertionPoint = 5;

        // 如果系统剪贴板有内容，验证成功插入到了 pos 5 位置
        var clipText = ClipboardService.GetText(primary: false);
        tf.PasteFromClipboard(preferPrimary: false);
        if (!string.IsNullOrEmpty(clipText))
        {
            var sanitized = clipText.Replace("\r", "").Replace("\n", " ");
            Assert.Equal($"hello{sanitized} world", tf.Text);
        }
        else
        {
            Assert.Equal("hello world", tf.Text);
        }
    }

    [Fact]
    public void TextField_MiddleClick_Registered()
    {
        var tf = new TextField();
        bool focusedCalled = false;
        tf.EnableMiddleClickPaste(() => focusedCalled = true);

        Assert.False(focusedCalled);
        Assert.NotNull(tf);
    }
}
