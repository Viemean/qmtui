using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using QmTui.UI;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Xunit;

namespace QmTui.Tests;

public class NativeCursorOutputTests
{
    private sealed class RecordingOutput : IOutput
    {
        public List<Cursor> Cursors { get; } = [];
        public List<string> Writes { get; } = [];

        public void SetCursor(Cursor cursor) => Cursors.Add(cursor);

        public bool Force16Colors { get; set; }
        public bool IsLegacyConsole { get; set; }
        public bool UseKittyGraphics { get; set; }
        public ConcurrentQueue<SixelToRender> GetSixels() => new();
        public Size GetSize() => new(80, 24);
        public void SetSize(int width, int height) { }
        public Cursor GetCursor() => new();
        public void Write(ReadOnlySpan<char> text) => Writes.Add(text.ToString());
        public void Write(IOutputBuffer buffer) => Writes.Add("<frame>");
        public void Suspend() { }
        public string GetLastOutput() => "";
        public string ToAnsi(IOutputBuffer buffer) => "";
        public void Dispose() { }
    }

    // Refresh() 的临时隐藏：Position 置空、保留当前可见样式。
    [Fact]
    public void SetCursor_RefreshHideRequest_IsSwallowed()
    {
        var inner = new RecordingOutput();
        var output = new NativeCursorOutput(inner);

        output.SetCursor(new Cursor { Position = null, Style = CursorStyle.BlinkingBar });

        Assert.Empty(inner.Cursors);
    }

    // 失焦等真正的隐藏：默认 Cursor（Style = Hidden）。
    [Fact]
    public void SetCursor_RealHide_IsForwarded()
    {
        var inner = new RecordingOutput();
        var output = new NativeCursorOutput(inner);

        output.SetCursor(new Cursor());

        Assert.Single(inner.Cursors);
    }

    [Fact]
    public void SetCursor_VisibleCursor_IsForwarded()
    {
        var inner = new RecordingOutput();
        var output = new NativeCursorOutput(inner);

        var cursor = new Cursor { Position = new Point(3, 4), Style = CursorStyle.BlinkingBar };
        output.SetCursor(cursor);

        Assert.Same(cursor, Assert.Single(inner.Cursors));
    }

    // 帧写入必须被同步输出序列（DECSET 2026）包住，否则一帧可能在终端被分段渲染。
    [Fact]
    public void Write_Frame_IsWrappedInSynchronizedUpdate()
    {
        var inner = new RecordingOutput();
        var output = new NativeCursorOutput(inner);

        output.Write((IOutputBuffer)null!);

        Assert.Equal(new[] { "\x1b[?2026h", "<frame>", "\x1b[?2026l" }, inner.Writes);
    }

    // NativeCursorGuard.Install 靠反射找 DriverImpl 里的 IOutput 字段；TG 升级改动结构时这里先失败。
    [Fact]
    public void DriverImpl_StillHasAnOutputField()
    {
        Type? driverType = typeof(Terminal.Gui.App.Application).Assembly.GetType("Terminal.Gui.Drivers.DriverImpl");

        Assert.NotNull(driverType);
        Assert.Contains(
            driverType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            f => typeof(IOutput).IsAssignableFrom(f.FieldType));
    }
}
